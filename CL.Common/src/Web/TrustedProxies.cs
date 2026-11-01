using System.Net;
using System.Net.Sockets;
using CodeLogic.Core.Results;

namespace CL.Common.Web;

/// <summary>
/// An immutable set of proxy addresses and networks whose <c>X-Forwarded-For</c> entries are believed by
/// <see cref="HttpHeaderHelper.GetEffectiveClientIp(string?, string?, TrustedProxies)"/>. Build it once at startup
/// (for example from configuration) and reuse it; it is thread-safe.
/// </summary>
/// <example>
/// <code>
/// var proxies = TrustedProxies.Parse(["10.0.0.0/8", "203.0.113.7", "2001:db8::/32"]).ValueOrThrow();
/// string clientIp = HttpHeaderHelper.GetEffectiveClientIp(remoteIp, xForwardedFor, proxies);
/// </code>
/// </example>
public sealed class TrustedProxies
{
    /// <summary>Error code returned by <see cref="Parse"/> for an entry that is neither an IP address nor a CIDR network.</summary>
    public const string InvalidEntryCode = "web.invalid_trusted_proxy";

    private readonly IPNetwork[] _networks;

    private TrustedProxies(IPNetwork[] networks) => _networks = networks;

    /// <summary>Trusts no proxy: the direct peer is always the client and <c>X-Forwarded-For</c> is ignored.</summary>
    public static TrustedProxies None { get; } = new([]);

    /// <summary>Trusts loopback only (127/8, ::1): a reverse proxy on the same host.</summary>
    public static TrustedProxies Loopback { get; } = new([IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("::1/128")]);

    /// <summary>
    /// Trusts every non-public address (see <see cref="HttpHeaderHelper.IsPrivateIp(IPAddress)"/>): "this network",
    /// loopback, RFC 1918, link-local, carrier-grade NAT and IPv6 unique-local. This is what the two-argument
    /// <see cref="HttpHeaderHelper.GetEffectiveClientIp(string?, string?)"/> uses. Anyone on those networks can
    /// still forge the header, so prefer an explicit list of your proxies in production.
    /// </summary>
    public static TrustedProxies PrivateNetworks { get; } = new(
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ]);

    /// <summary>The trusted networks (single addresses appear as /32 or /128).</summary>
    public IReadOnlyList<IPNetwork> Networks => _networks;

    /// <summary>Creates a set from networks.</summary>
    public static TrustedProxies FromNetworks(IEnumerable<IPNetwork> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        return new([.. networks.Select(Normalize)]);
    }

    /// <summary>Creates a set from single proxy addresses.</summary>
    public static TrustedProxies FromAddresses(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return new([.. addresses.Select(a => SingleHost(HttpHeaderHelper.Normalize(a)))]);
    }

    /// <summary>
    /// Parses addresses (<c>203.0.113.7</c>, <c>::1</c>) and CIDR networks (<c>10.0.0.0/8</c>, <c>2001:db8::/32</c>).
    /// Blank entries are skipped. Fails with <see cref="InvalidEntryCode"/> (validation) on the first invalid entry.
    /// </summary>
    public static Result<TrustedProxies> Parse(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var networks = new List<IPNetwork>();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var entry = raw.Trim();
            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out var network))
                    return Error.Validation(InvalidEntryCode, $"Invalid trusted proxy network '{entry}'.");
                networks.Add(Normalize(network));
            }
            else
            {
                if (!HttpHeaderHelper.TryParseIp(entry, out var ip))
                    return Error.Validation(InvalidEntryCode, $"Invalid trusted proxy address '{entry}'.");
                networks.Add(SingleHost(ip));
            }
        }
        return new TrustedProxies([.. networks]);
    }

    /// <summary>Returns a new set containing this set's networks plus <paramref name="other"/>'s.</summary>
    public TrustedProxies Union(TrustedProxies other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new([.. _networks, .. other._networks]);
    }

    /// <summary>Returns true if <paramref name="address"/> (IPv4-mapped IPv6 is treated as IPv4) is in the set.</summary>
    public bool IsTrusted(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = HttpHeaderHelper.Normalize(address);
        foreach (var network in _networks)
            if (network.Contains(ip)) return true;
        return false;
    }

    /// <summary>Parses <paramref name="address"/> with <see cref="HttpHeaderHelper.TryParseIp"/> and checks it; unparsable input is not trusted.</summary>
    public bool IsTrusted(string? address) =>
        HttpHeaderHelper.TryParseIp(address, out var ip) && IsTrusted(ip);

    private static IPNetwork SingleHost(IPAddress ip) =>
        new(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);

    // An IPv4-mapped IPv6 network (::ffff:10.0.0.0/104) is stored as its IPv4 equivalent so normalised peers match.
    private static IPNetwork Normalize(IPNetwork network) =>
        network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96
            ? new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96)
            : network;
}
