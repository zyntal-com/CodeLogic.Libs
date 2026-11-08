using System.Net;
using System.Net.Sockets;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.NetUtils.Services;

public partial class DnsblChecker
{

    /// <summary>
    /// A checker that queries nothing: every check of a valid address fails with
    /// <see cref="NetUtilsErrorCodes.DnsblDisabled"/>. <see cref="NetUtilsLibrary.Dnsbl"/> hands it out while DNSBL checking is switched off.
    /// </summary>
    internal static DnsblChecker CreateDisabled(ILogger? logger) =>
        new(new DnsblConfig { Enabled = false }, logger, static (_, _) => Task.FromResult(Array.Empty<IPAddress>()), enabled: false);

    /// <summary>
    /// <see langword="false"/> when DNSBL checking is switched off (<c>netutils.enabled</c> or <c>netutils.dnsbl.enabled</c>
    /// is <see langword="false"/>). A disabled checker sends no query; each check of a valid address fails with an
    /// <see cref="UnavailableError"/> coded <see cref="NetUtilsErrorCodes.DnsblDisabled"/>.
    /// </summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Checks whether <paramref name="ipAddress"/> appears on any configured DNSBL,
    /// using the services declared in <see cref="NetUtilsConfig"/>.
    /// Local/private addresses are skipped and returned as <em>not blacklisted</em>.
    /// </summary>
    /// <remarks>
    /// The primary zones are queried first (concurrently when <see cref="DnsblConfig.ParallelQueries"/> is set);
    /// the fallback zones are queried only when no primary zone lists the address. A zone that times out, fails
    /// to resolve, or answers with a DNSBL error code is reported in <see cref="DnsblCheck.FailedServices"/>,
    /// and a check with no listing but failed zones is <see cref="DnsblCheckStatus.Indeterminate"/>, never
    /// <see cref="DnsblCheckStatus.NotBlacklisted"/>.
    /// </remarks>
    /// <param name="ipAddress">The IP address string to check (IPv4 or IPv6).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    /// The completed check (<see cref="DnsblCheck.Status"/> holds the verdict), or an error: a
    /// <see cref="ValidationError"/> for text that is not an IP address, an <see cref="UnavailableError"/> when DNSBL
    /// checking is disabled or the caller cancelled, an <see cref="InternalError"/> for an unexpected failure. The codes
    /// are in <see cref="NetUtilsErrorCodes"/>.
    /// </returns>
    public Task<Result<DnsblCheck>> CheckIpAsync(string ipAddress, CancellationToken cancellationToken = default)
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
    /// <param name="request">
    /// Per-call service lists, allowlist predicate, and timeout. <see cref="DnsblCheckRequest.IsAllowedAsync"/> receives
    /// the address in canonical form (<c>1.2.3.4</c>, also for the input <c>::ffff:1.2.3.4</c>), so an allowlist compares
    /// one spelling.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the check: the result is then an <see cref="UnavailableError"/> coded
    /// <see cref="NetUtilsErrorCodes.Cancelled"/>, never a verdict on the address.
    /// </param>
    /// <returns>The completed check, or an error; see <see cref="CheckIpAsync(string, CancellationToken)"/>.</returns>
    public async Task<Result<DnsblCheck>> CheckIpAsync(
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
                return NetUtilsErrors.InvalidIpAddress(strings.InvalidIpAddressFormat);
            }

            // An IPv4 client seen through a dual-stack socket (::ffff:a.b.c.d) is listed in the IPv4 zones.
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            var type = GetAddressType(ip);

            if (!_enabled)
                return NetUtilsErrors.DnsblDisabled(strings.DnsblDisabled);

            if (IsLocalOrPrivateIp(ip))
            {
                _logger?.Debug($"Skipping DNSBL check for local/private IP: {shown}");
                return DnsblCheck.NotBlacklisted(ipAddress, type);
            }

            // The allowlist sees the canonical address, so "::ffff:1.2.3.4", "1.2.3.4" and "0x01.0x02.0x03.0x04" compare equal.
            if (request.IsAllowedAsync is not null &&
                await request.IsAllowedAsync(ip.ToString(), cancellationToken).ConfigureAwait(false))
            {
                _logger?.Debug($"IP {shown} is on the caller-supplied allowlist — skipping DNSBL");
                return DnsblCheck.NotBlacklisted(ipAddress, type);
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return await CheckIpv4Async(ip, ipAddress, request, cancellationToken).ConfigureAwait(false);

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return await CheckIpv6Async(ip, ipAddress, request, cancellationToken).ConfigureAwait(false);

            _logger?.Debug($"DNSBL check skipped for unsupported address family: {shown}");
            return DnsblCheck.NotBlacklisted(ipAddress, IpAddressType.Unknown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (request aborted, shutdown): say so, instead of a verdict or an internal error.
            _logger?.Debug($"DNSBL check for {shown} was cancelled by the caller");
            return NetUtilsErrors.Cancelled(strings.DnsblCancelled);
        }
        catch (Exception ex)
        {
            // The exception text stays in the log; the result carries a fixed message and the exception type.
            _logger?.Error($"Error checking DNSBL for {shown}", ex);
            return NetUtilsErrors.DnsblCheckFailed(strings.DnsblCheckFailed, ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private enum ZoneOutcome { NotListed, Listed, Failed }

    private async Task<DnsblCheck> CheckIpv4Async(IPAddress ip, string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIp = ReverseLookupLabel(ip);
        return await CheckZonesAsync(ipAddress, reversedIp, IpAddressType.IPv4, request.Ipv4Services, request.Ipv4FallbackServices, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DnsblCheck> CheckIpv6Async(IPAddress ip, string ipAddress, DnsblCheckRequest request, CancellationToken cancellationToken)
    {
        var reversedIpv6 = ReverseLookupLabel(ip);
        return await CheckZonesAsync(ipAddress, reversedIpv6, IpAddressType.IPv6, request.Ipv6Services, request.Ipv6FallbackServices, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DnsblCheck> CheckZonesAsync(
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
            return DnsblCheck.Blacklisted(ipAddress, type, listedBy, failed);
        }

        if (failed.Count > 0)
        {
            _logger?.Warning($"{shown} is not listed by the zones that answered, but {failed.Count} zone(s) could not be checked: " +
                             string.Join(", ", failed.Select(InputText.ForLog)));
            return DnsblCheck.Indeterminate(ipAddress, type, failed);
        }

        _logger?.Debug(InputText.Format(strings.IpClean, shown));
        return DnsblCheck.NotBlacklisted(ipAddress, type);
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
}
