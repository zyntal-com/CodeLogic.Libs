using System.Text.RegularExpressions;

namespace CL.GitHelper.Services;

/// <summary>
/// Credential hygiene helpers: redacting secrets before they reach logs or error messages, and
/// deciding which hosts may receive the configured credentials.
/// </summary>
internal static partial class GitSecurity
{
    /// <summary>Placeholder written in place of a redacted secret.</summary>
    internal const string Mask = "***";

    // scheme://userinfo@ — the user-info part of a URL may hold a user name, a password or a token.
    [GeneratedRegex(@"(?<scheme>\b[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    /// <summary>
    /// Removes credentials from <paramref name="text"/>: the user-info part of every URL
    /// (<c>https://user:token@host/…</c> becomes <c>https://***@host/…</c>) and every literal
    /// occurrence of <paramref name="secret"/>.
    /// </summary>
    /// <param name="text">A URL, log line or exception message. Null becomes an empty string.</param>
    /// <param name="secret">A configured password or token to mask wherever it appears. Optional.</param>
    internal static string Redact(string? text, string? secret = null)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var redacted = UserInfoPattern().Replace(text, m => m.Groups["scheme"].Value + Mask + "@");
        if (!string.IsNullOrEmpty(secret))
            redacted = redacted.Replace(secret, Mask, StringComparison.Ordinal);
        return redacted;
    }

    /// <summary>
    /// True when credentials configured for <paramref name="configuredUrl"/> may be sent to
    /// <paramref name="requestUrl"/>: both must be absolute URLs with the same scheme, host and port.
    /// A submodule, redirect or second remote on another host does not get the token, and an
    /// <c>https</c> token is never sent over plain <c>http</c>.
    /// </summary>
    internal static bool CredentialsAllowedFor(string? configuredUrl, string? requestUrl)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var configured) ||
            !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request))
            return false;

        if (configured.IsFile || configured.IsUnc) return false;

        return string.Equals(configured.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(configured.IdnHost, request.IdnHost, StringComparison.OrdinalIgnoreCase) &&
               configured.Port == request.Port;
    }
}
