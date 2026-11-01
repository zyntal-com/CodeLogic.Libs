using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CL.Common.Web;

/// <summary>
/// Provides utilities for working with HTTP headers, client detection,
/// and IP address utilities. Framework-agnostic — pass header values directly.
/// </summary>
public static class HttpHeaderHelper
{
    // ── Accept-Language ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses an Accept-Language header value and returns the preferred culture.
    /// Example: "da-DK,da;q=0.9,en-US;q=0.8" → "da-DK"
    /// </summary>
    public static string GetPrimaryLocale(string? acceptLanguageHeader)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguageHeader)) return "en-US";

        return acceptLanguageHeader
            .Split(',')
            .Select(lang =>
            {
                var parts = lang.Trim().Split(';');
                var tag   = parts[0].Trim();
                var q     = 1.0;
                if (parts.Length > 1 && parts[1].Trim().StartsWith("q="))
                    double.TryParse(parts[1][2..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out q);
                return (tag, q);
            })
            .OrderByDescending(x => x.q)
            .Select(x => x.tag)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "en-US";
    }

    // ── Client IP ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the client IP address, consulting <c>X-Forwarded-For</c> only when the direct peer is a
    /// non-public address (a reverse proxy on the same host or network), as if called with
    /// <see cref="TrustedProxies.PrivateNetworks"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Behaviour change in 5.0:</b> earlier versions returned the first non-RFC 1918 entry of
    /// <c>X-Forwarded-For</c> whatever the peer was, so any client could spoof its address. Now a peer with a
    /// public address is returned as is and its header is ignored. When your proxies have public addresses, or
    /// you want to trust only specific ones, use the overload that takes <see cref="TrustedProxies"/>.
    /// </para>
    /// <para>The result is always a parsed, normalised address (IPv4-mapped IPv6 becomes IPv4), or <c>"0.0.0.0"</c>
    /// when <paramref name="remoteIp"/> is missing or unparsable.</para>
    /// </remarks>
    /// <param name="remoteIp">The direct peer address (the socket's remote address).</param>
    /// <param name="xForwardedFor">The raw <c>X-Forwarded-For</c> header value, if any.</param>
    public static string GetEffectiveClientIp(string? remoteIp, string? xForwardedFor) =>
        GetEffectiveClientIp(remoteIp, xForwardedFor, TrustedProxies.PrivateNetworks);

    /// <summary>
    /// Returns the client IP address behind the given trusted proxies. If the direct peer is not trusted, it is the
    /// client and <c>X-Forwarded-For</c> is ignored. Otherwise the header is walked right to left, skipping trusted
    /// proxies, and the first untrusted address is returned. If every hop is trusted, the leftmost one is returned.
    /// An unparsable entry stops the walk (nothing to its left can be believed) and the last trusted hop is returned.
    /// </summary>
    /// <param name="remoteIp">The direct peer address (the socket's remote address). Ports and brackets are stripped.</param>
    /// <param name="xForwardedFor">The raw <c>X-Forwarded-For</c> header value, if any. Join multiple header lines with commas, in order.</param>
    /// <param name="trustedProxies">The proxies whose <c>X-Forwarded-For</c> entries are believed.</param>
    /// <returns>A parsed, normalised address string, or <c>"0.0.0.0"</c> when <paramref name="remoteIp"/> is missing or unparsable.</returns>
    public static string GetEffectiveClientIp(string? remoteIp, string? xForwardedFor, TrustedProxies trustedProxies) =>
        GetEffectiveClientIpAddress(remoteIp, xForwardedFor, trustedProxies)?.ToString() ?? "0.0.0.0";

    /// <summary>
    /// Same as <see cref="GetEffectiveClientIp(string?, string?, TrustedProxies)"/> but returns the parsed address,
    /// or <c>null</c> when <paramref name="remoteIp"/> is missing or unparsable.
    /// </summary>
    public static IPAddress? GetEffectiveClientIpAddress(string? remoteIp, string? xForwardedFor, TrustedProxies trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(trustedProxies);
        if (!TryParseIp(remoteIp, out var current)) return null;
        if (!trustedProxies.IsTrusted(current) || string.IsNullOrWhiteSpace(xForwardedFor)) return current;

        var entries = xForwardedFor.Split(',');
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(entries[i])) continue;
            if (!TryParseIp(entries[i], out var hop)) return current; // garbage: stop at the last trusted hop
            current = hop;
            if (!trustedProxies.IsTrusted(hop)) return hop;
        }
        return current;
    }

    /// <summary>
    /// Parses an IP address as it appears in <c>X-Forwarded-For</c> or a remote-address field: trims whitespace and
    /// quotes, strips a port (<c>1.2.3.4:80</c>, <c>[::1]:443</c>) and brackets, rejects IPv4 shorthand such as
    /// <c>127.1</c>, and normalises IPv4-mapped IPv6 (<c>::ffff:1.2.3.4</c>) to IPv4.
    /// </summary>
    public static bool TryParseIp(string? value, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.AsSpan().Trim().Trim('"');

        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0) return false;
            var rest = s[(close + 1)..];
            if (!rest.IsEmpty && (rest[0] != ':' || !IsPort(rest[1..]))) return false;
            s = s[1..close];
            if (!s.Contains(':')) return false; // only IPv6 goes in brackets
        }
        else
        {
            var colon = s.IndexOf(':');
            if (colon >= 0 && s.LastIndexOf(':') == colon)
            {
                // Exactly one colon: IPv4 with a port (IPv6 always has at least two colons).
                if (!IsPort(s[(colon + 1)..])) return false;
                s = s[..colon];
            }
        }

        if (s.IsEmpty) return false;
        var isV6 = s.Contains(':');
        if (!isV6 && s.Count('.') != 3) return false; // dotted quad only
        if (!IPAddress.TryParse(s, out var ip)) return false;
        if (isV6 != (ip.AddressFamily == AddressFamily.InterNetworkV6)) return false;

        address = Normalize(ip);
        return true;

        static bool IsPort(ReadOnlySpan<char> port) =>
            !port.IsEmpty && port.Length <= 5 &&
            ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Returns true if the address is not publicly routable: RFC 1918 (10/8, 172.16/12, 192.168/16), loopback
    /// (127/8, ::1), "this network" (0/8, ::), link-local (169.254/16, fe80::/10), carrier-grade NAT (100.64/10) or
    /// IPv6 unique-local (fc00::/7). IPv4-mapped IPv6 addresses are checked as IPv4. Unparsable input returns false.
    /// </summary>
    public static bool IsPrivateIp(string ipAddress) =>
        TryParseIp(ipAddress, out var ip) && IsPrivateIp(ip);

    /// <inheritdoc cref="IsPrivateIp(string)"/>
    public static bool IsPrivateIp(IPAddress ipAddress)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        return TrustedProxies.PrivateNetworks.IsTrusted(ipAddress);
    }

    // IPv4-mapped IPv6 becomes IPv4; an IPv6 zone id (fe80::1%eth0) is dropped.
    internal static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4()
        : ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0 ? new IPAddress(ip.GetAddressBytes())
        : ip;

    // ── User-Agent ────────────────────────────────────────────────────────────

    private static readonly Regex BotRegex = new(
        @"bot|crawler|spider|scraper|baiduspider|ia_archiver|wget|curl|python|java|go-http",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns true if the User-Agent string matches known bot/crawler patterns.</summary>
    public static bool IsBot(string? userAgent) =>
        !string.IsNullOrWhiteSpace(userAgent) && BotRegex.IsMatch(userAgent);

    /// <summary>Returns true if the User-Agent suggests a mobile device.</summary>
    public static bool IsMobile(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return false;
        return Regex.IsMatch(userAgent, @"android|iphone|ipad|ipod|windows phone|mobile",
            RegexOptions.IgnoreCase);
    }

    // ── Content negotiation ───────────────────────────────────────────────────

    /// <summary>Returns true if the Accept header includes application/json.</summary>
    public static bool AcceptsJson(string? acceptHeader) =>
        acceptHeader?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true ||
        acceptHeader?.Contains("*/*", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Returns the value of a header by name (case-insensitive) from a dictionary.</summary>
    public static string? GetHeader(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var val) ? val
        : headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
