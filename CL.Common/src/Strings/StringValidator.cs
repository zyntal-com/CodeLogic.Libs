using System.Net;
using System.Text.RegularExpressions;

namespace CL.Common.Strings;

/// <summary>
/// Provides common string validation methods. All methods are stateless and thread-safe.
/// </summary>
public static class StringValidator
{
    // Pre-compiled regexes for performance
    private static readonly Regex EmailRegex = new(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PhoneRegex = new(
        @"^\+?[\d\s\-().]{7,20}$",
        RegexOptions.Compiled);

    private static readonly Regex AlphanumericRegex = new(
        @"^[a-zA-Z0-9]+$",
        RegexOptions.Compiled);

    private static readonly Regex NumericRegex = new(
        @"^-?\d+(\.\d+)?$",
        RegexOptions.Compiled);

    private static readonly Regex GuidRegex = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    /// <summary>Returns true if the string is a valid email address.</summary>
    public static bool IsEmail(string email) =>
        !string.IsNullOrWhiteSpace(email) && EmailRegex.IsMatch(email.Trim());

    /// <summary>
    /// Returns true if the string is a valid absolute URL (http or https).
    /// </summary>
    public static bool IsUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Returns true if the string looks like a phone number (7–20 digits, optional +, spaces, dashes, parens).</summary>
    public static bool IsPhoneNumber(string phone) =>
        !string.IsNullOrWhiteSpace(phone) && PhoneRegex.IsMatch(phone.Trim());

    /// <summary>Returns true if the string contains only digits and an optional leading minus/decimal point.</summary>
    public static bool IsNumeric(string value) =>
        !string.IsNullOrWhiteSpace(value) && NumericRegex.IsMatch(value.Trim());

    /// <summary>Returns true if the string contains only letters and digits (a-z, A-Z, 0-9).</summary>
    public static bool IsAlphanumeric(string value) =>
        !string.IsNullOrWhiteSpace(value) && AlphanumericRegex.IsMatch(value);

    /// <summary>Returns true if the string is a valid IPv4 address (e.g. "192.168.1.1").</summary>
    public static bool IsIPv4(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return IPAddress.TryParse(ip.Trim(), out var addr) &&
               addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>Returns true if the string is a valid IPv6 address.</summary>
    public static bool IsIPv6(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return IPAddress.TryParse(ip.Trim(), out var addr) &&
               addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }

    /// <summary>Returns true if the string is a valid GUID in standard format.</summary>
    public static bool IsGuid(string value) =>
        !string.IsNullOrWhiteSpace(value) && GuidRegex.IsMatch(value.Trim());

    /// <summary>Returns true if the string length is at least <paramref name="min"/> characters.</summary>
    public static bool HasMinLength(string value, int min) =>
        value != null && value.Length >= min;

    /// <summary>Returns true if the string length does not exceed <paramref name="max"/> characters.</summary>
    public static bool HasMaxLength(string value, int max) =>
        value != null && value.Length <= max;

    /// <summary>
    /// Returns true if the entire string matches the given regular expression pattern.
    /// </summary>
    /// <param name="value">The string to test.</param>
    /// <param name="pattern">A .NET regular expression pattern.</param>
    /// <param name="matchTimeout">
    /// How long the match may run before it is abandoned and treated as no match (guards against
    /// catastrophic backtracking, ReDoS). Default: <see cref="DefaultMatchTimeout"/> (one second).
    /// </param>
    public static bool MatchesPattern(string value, string pattern, TimeSpan? matchTimeout = null)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(pattern)) return false;
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, matchTimeout ?? DefaultMatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Default timeout for <see cref="MatchesPattern"/>: one second.</summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Returns true if the string is null or consists only of whitespace.</summary>
    public static bool IsNullOrWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>Returns true if the string is not null and not whitespace.</summary>
    public static bool IsNotEmpty(string? value) => !string.IsNullOrWhiteSpace(value);
}
