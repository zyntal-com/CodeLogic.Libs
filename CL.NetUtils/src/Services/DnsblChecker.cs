using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;

namespace CL.NetUtils.Services;

/// <summary>
/// Checks IP addresses against DNS-based blacklists (DNSBLs).
/// Supports both IPv4 and IPv6 addresses, primary and fallback service lists.
/// </summary>
public class DnsblChecker
{
    private readonly DnsblConfig _config;
    private readonly ILogger? _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly bool _enabled;

    /// <summary>
    /// Initializes a new <see cref="DnsblChecker"/>.
    /// </summary>
    /// <param name="config">DNSBL configuration.</param>
    /// <param name="logger">Optional logger; pass <see langword="null"/> to suppress logging.</param>
    public DnsblChecker(DnsblConfig config, ILogger? logger = null)
        : this(config, logger, static (host, ct) => Dns.GetHostAddressesAsync(host, ct))
    {
    }

    /// <summary>Test seam: resolves DNSBL names through <paramref name="resolve"/> instead of the system resolver.</summary>
    internal DnsblChecker(DnsblConfig config, ILogger? logger, Func<string, CancellationToken, Task<IPAddress[]>> resolve, bool enabled = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _resolve = resolve;
        _enabled = enabled;
    }

    /// <summary>
    /// A checker that queries nothing: every check returns <see cref="DnsblCheckStatus.Error"/> with a "DNSBL checking
    /// is disabled" message. <see cref="NetUtilsLibrary.Dnsbl"/> hands it out while DNSBL checking is switched off.
    /// </summary>
    internal static DnsblChecker CreateDisabled(ILogger? logger) =>
        new(new DnsblConfig { Enabled = false }, logger, static (_, _) => Task.FromResult(Array.Empty<IPAddress>()), enabled: false);

    /// <summary>
    /// <see langword="false"/> when DNSBL checking is switched off (<c>netutils.enabled</c> or <c>netutils.dnsbl.enabled</c>
    /// is <see langword="false"/>). A disabled checker sends no query; each check returns
    /// <see cref="DnsblCheckStatus.Error"/> with a "disabled" <see cref="DnsblCheckResult.ErrorMessage"/>.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>Supplies the (localized) messages; set by <see cref="NetUtilsLibrary"/>.</summary>
    internal Func<NetUtilsStrings> Strings { get; set; } = static () => InputText.DefaultStrings;

    /// <summary>
    /// Checks whether <paramref name="ipAddress"/> appears on any configured DNSBL,
    /// using the services declared in <see cref="NetUtilsConfig"/>.
    /// Local/private addresses are skipped and returned as <em>not blacklisted</em>.
    /// </summary>
    /// <remarks>
    /// The primary zones are queried first (concurrently when <see cref="DnsblConfig.ParallelQueries"/> is set);
    /// the fallback zones are queried only when no primary zone lists the address. A zone that times out, fails
    /// to resolve, or answers with a DNSBL error code is reported in <see cref="DnsblCheckResult.FailedServices"/>,
    /// and a check with no listing but failed zones is <see cref="DnsblCheckStatus.Indeterminate"/>, never
    /// <see cref="DnsblCheckStatus.NotBlacklisted"/>.
    /// </remarks>
    /// <param name="ipAddress">The IP address string to check (IPv4 or IPv6).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A <see cref="DnsblCheckResult"/> describing the outcome.</returns>
    public Task<DnsblCheckResult> CheckIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var request = new DnsblCheckRequest
        {
            Ipv4Services = _config.Ipv4Services,
            Ipv4FallbackServices = _config.Ipv4FallbackServices,
            Ipv6Services = _config.Ipv6Services,
            Ipv6FallbackServices = _config.Ipv6FallbackServices,
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds),
            ParallelQueries = _config.ParallelQueries,
            DetailedLogging = _config.DetailedLogging
        };
        return CheckIpAsync(ipAddress, request, cancellationToken);
    }

    /// <summary>
    /// Checks <paramref name="ipAddress"/> against the DNSBL zones supplied in
    /// <paramref name="request"/>. Callers that manage their own service lists
    /// and allowlists (e.g. DB-backed admin UIs) use this overload to bypass
    /// the static library configuration.
    /// </summary>
    /// <param name="ipAddress">The IP address string to check (IPv4 or IPv6).</param>
    /// <param name="request">Per-call service lists, allowlist predicate, and timeout.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task<DnsblCheckResult> CheckIpAsync(
        string ipAddress,
        DnsblCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var strings = Strings();
        var shown = InputText.ForLog(ipAddress);
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger?.Warning(InputText.Format(strings.InvalidIpAddress, shown));
                return DnsblCheckResult.Error(ipAddress, IpAddressType.Unknown, strings.InvalidIpAddressFormat);
            }

            if (!_enabled)
                return DnsblCheckResult.Error(ipAddress, GetAddressType(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip), strings.DnsblDisabled);

            // An IPv4 client seen through a dual-stack socket (::ffff:a.b.c.d) is listed in the IPv4 zones.
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            if (IsLocalOrPrivateIp(ip))
            {
                _logger?.Debug($"Skipping DNSBL check for local/private IP: {shown}");
                return DnsblCheckResult.NotBlacklisted(ipAddress, GetAddressType(ip));
            }

            if (request.IsAllowedAsync is not null &&
                await request.IsAllowedAsync(ipAddress, cancellationToken).ConfigureAwait(false))
            {
                _logger?.Debug($"IP {shown} is on the caller-supplied allowlist — skipping DNSBL");
                return DnsblCheckResult.NotBlacklisted(ipAddress, GetAddressType(ip));
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return await CheckIpv4Async(ip, ipAddress, request, cancellationToken).ConfigureAwait(false);

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return await CheckIpv6Async(ip, ipAddress, request, cancellationToken).ConfigureAwait(false);

            _logger?.Debug($"DNSBL check skipped for unsupported address family: {shown}");
            return DnsblCheckResult.NotBlacklisted(ipAddress, IpAddressType.Unknown);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error checking DNSBL for {shown}", ex);
            return DnsblCheckResult.Error(ipAddress, IpAddressType.Unknown, ex.Message);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private enum ZoneOutcome { NotListed, Listed, Failed }

    private async Task<DnsblCheckResult> CheckIpv4Async(IPAddress ip, string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIp = ReverseLookupLabel(ip);
        return await CheckZonesAsync(ipAddress, reversedIp, IpAddressType.IPv4, request.Ipv4Services, request.Ipv4FallbackServices, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DnsblCheckResult> CheckIpv6Async(IPAddress ip, string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIpv6 = ReverseLookupLabel(ip);
        return await CheckZonesAsync(ipAddress, reversedIpv6, IpAddressType.IPv6, request.Ipv6Services, request.Ipv6FallbackServices, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DnsblCheckResult> CheckZonesAsync(
        string ipAddress,
        string reversed,
        IpAddressType type,
        IReadOnlyList<string> primaries,
        IReadOnlyList<string> fallbacks,
        DnsblCheckRequest request,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();
        var strings = Strings();
        var shown = InputText.ForLog(ipAddress);

        var listedBy = await CheckListAsync(reversed, primaries, request, failed, cancellationToken).ConfigureAwait(false)
            ?? await CheckListAsync(reversed, fallbacks, request, failed, cancellationToken).ConfigureAwait(false);
        if (listedBy is not null)
        {
            _logger?.Warning(InputText.Format(strings.IpBlacklisted, shown, InputText.ForLog(listedBy)));
            return DnsblCheckResult.Blacklisted(ipAddress, type, listedBy, failed);
        }

        if (failed.Count > 0)
        {
            _logger?.Warning($"{shown} is not listed by the zones that answered, but {failed.Count} zone(s) could not be checked: " +
                             string.Join(", ", failed.Select(InputText.ForLog)));
            return DnsblCheckResult.Indeterminate(ipAddress, type, failed);
        }

        _logger?.Debug(InputText.Format(strings.IpClean, shown));
        return DnsblCheckResult.NotBlacklisted(ipAddress, type);
    }

    /// <summary>Queries one zone list; returns the first listing zone (in list order) or null. Failed zones go to <paramref name="failed"/>.</summary>
    private async Task<string?> CheckListAsync(
        string reversed,
        IReadOnlyList<string> services,
        DnsblCheckRequest request,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        if (services.Count == 0)
            return null;

        // Only well-formed DNS names are queried; a malformed entry (blank line, IP literal, spaces) counts as a zone
        // that could not be checked, so it cannot silently turn a check clean.
        var zones = new List<string>(services.Count);
        foreach (var service in services)
        {
            if (InputText.NormalizeZone(service, reversed.Length) is { } zone)
                zones.Add(zone);   // reported (MatchedService, FailedServices) in normalized form
            else
            {
                _logger?.Warning($"Ignoring invalid DNSBL zone '{InputText.ForLog(service)}' (not a DNS name)");
                failed.Add(InputText.ForLog(service));
            }
        }

        if (request.ParallelQueries && zones.Count > 1)
        {
            var outcomes = await Task.WhenAll(zones.Select(zone =>
                CheckServiceAsync($"{reversed}.{zone}", zone, request.Timeout, request.DetailedLogging, cancellationToken))).ConfigureAwait(false);

            string? listed = null;
            for (var i = 0; i < zones.Count; i++)
            {
                if (outcomes[i] == ZoneOutcome.Listed)
                    listed ??= zones[i];
                else if (outcomes[i] == ZoneOutcome.Failed)
                    failed.Add(zones[i]);
            }
            return listed;
        }

        foreach (var zone in zones)
        {
            var outcome = await CheckServiceAsync($"{reversed}.{zone}", zone, request.Timeout, request.DetailedLogging, cancellationToken)
                .ConfigureAwait(false);
            if (outcome == ZoneOutcome.Listed)
                return zone;
            if (outcome == ZoneOutcome.Failed)
                failed.Add(zone);
        }
        return null;
    }

    private async Task<ZoneOutcome> CheckServiceAsync(
        string lookup,
        string service,
        TimeSpan timeout,
        bool detailedLogging,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            addresses = await _resolve(lookup, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup timeout for {lookup}");
            return ZoneOutcome.Failed;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            // NXDOMAIN / no A record: the zone answered, and the address is not listed.
            return ZoneOutcome.NotListed;
        }
        catch (Exception ex)
        {
            if (detailedLogging)
                _logger?.Debug($"DNS lookup failed for {lookup}: {ex.Message}");
            return ZoneOutcome.Failed;
        }

        var sawError = false;
        foreach (var answer in addresses)
        {
            // DNSBL convention: a listing is an A record inside 127.0.0.0/8.
            // Anything else is a wildcarded/parked domain answering for a dead
            // zone — treating it as a listing would flag every IP checked.
            var bytes = answer.AddressFamily == AddressFamily.InterNetwork ? answer.GetAddressBytes() : null;
            if (bytes is null || bytes[0] != 127)
            {
                if (detailedLogging)
                    _logger?.Debug($"Ignoring non-127/8 DNSBL answer {answer} from {service}");
                continue;
            }

            // 127.255.255.0/24 is the Spamhaus error range (open resolver,
            // query volume exceeded, typing error) — a service-side problem,
            // not a listing, and the zone was not actually checked.
            if (bytes[1] == 255 && bytes[2] == 255)
            {
                _logger?.Warning(
                    $"DNSBL {service} returned error code {answer} (public resolver blocked / query limit / config error) — " +
                    "results from this zone are unusable until the resolver setup is fixed.");
                sawError = true;
                continue;
            }

            return ZoneOutcome.Listed;
        }

        return sawError ? ZoneOutcome.Failed : ZoneOutcome.NotListed;
    }

    /// <summary>
    /// Whether an address is not publicly routable, so no DNSBL can list it: loopback, private, link-local,
    /// shared (CGNAT), documentation/benchmark, multicast, reserved and unspecified ranges.
    /// </summary>
    internal static bool IsLocalOrPrivateIp(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 "this network"
                10 => true,                                  // 10.0.0.0/8 private
                100 => b[1] is >= 64 and <= 127,             // 100.64.0.0/10 shared address space (CGNAT)
                169 => b[1] == 254,                          // 169.254.0.0/16 link-local
                172 => b[1] is >= 16 and <= 31,              // 172.16.0.0/12 private
                192 => (b[1] == 168)                         // 192.168.0.0/16 private
                       || (b[1] == 0 && b[2] is 0 or 2),     // 192.0.0.0/24 IETF, 192.0.2.0/24 TEST-NET-1
                198 => b[1] is 18 or 19                      // 198.18.0.0/15 benchmarking
                       || (b[1] == 51 && b[2] == 100),       // 198.51.100.0/24 TEST-NET-2
                203 => b[1] == 0 && b[2] == 113,             // 203.0.113.0/24 TEST-NET-3
                >= 224 => true,                              // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast
                _ => false
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || ip.IsIPv6UniqueLocal)
                return true;

            var b = ip.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;   // 2001:db8::/32 documentation
        }

        return false;
    }

    /// <summary>
    /// The DNSBL query label for an address, built from its bytes (not the input text, which may be a shorthand
    /// such as <c>127.1</c> or <c>0x7f.0.0.1</c>): reversed octets for IPv4, reversed nibbles for IPv6.
    /// </summary>
    internal static string ReverseLookupLabel(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}";

        var nibbles = bytes
            .SelectMany(b => new[] { (b >> 4) & 0xF, b & 0xF })
            .Reverse();
        return string.Join(".", nibbles.Select(n => n.ToString("x")));
    }

    private static IpAddressType GetAddressType(IPAddress ip) =>
        ip.AddressFamily switch
        {
            AddressFamily.InterNetwork => IpAddressType.IPv4,
            AddressFamily.InterNetworkV6 => IpAddressType.IPv6,
            _ => IpAddressType.Unknown
        };
}
