using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace CL.Common.Web;

/// <summary>
/// Provides URL parsing, building, and encoding utilities.
/// </summary>
public static class UrlHelper
{
    /// <summary>Encodes a string for safe use in a URL query string.</summary>
    public static string Encode(string value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>Decodes a URL-encoded string.</summary>
    public static string Decode(string value) => Uri.UnescapeDataString(value ?? string.Empty);

    /// <summary>Returns true if the string is a valid absolute HTTP or HTTPS URL.</summary>
    public static bool IsValid(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Combines a base URL and a relative path, handling trailing/leading slashes correctly.
    /// Example: Combine("https://api.example.com/", "/users") → "https://api.example.com/users".
    /// A null argument counts as empty.
    /// </summary>
    public static string Combine(string? baseUrl, string? relativePath)
    {
        baseUrl      = (baseUrl ?? string.Empty).TrimEnd('/');
        relativePath = (relativePath ?? string.Empty).TrimStart('/');
        return $"{baseUrl}/{relativePath}";
    }

    /// <summary>
    /// Appends query string parameters to a URL.
    /// Existing query strings are preserved.
    /// </summary>
    public static string AppendQuery(string url, Dictionary<string, string> parameters)
    {
        if (parameters == null || parameters.Count == 0) return url;

        var builder   = new StringBuilder(url);
        bool hasQuery = url.Contains('?');

        foreach (var (key, value) in parameters)
        {
            builder.Append(hasQuery ? '&' : '?');
            builder.Append(Encode(key));
            builder.Append('=');
            builder.Append(Encode(value));
            hasQuery = true;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a query string into a dictionary of key-value pairs, decoding percent-escapes and, as in
    /// form encoding (<c>application/x-www-form-urlencoded</c>), <c>+</c> as a space. A fragment is ignored.
    /// Example: "?foo=bar&amp;baz=1&amp;q=a+b" → { "foo": "bar", "baz": "1", "q": "a b" }
    /// </summary>
    public static Dictionary<string, string> ParseQuery(string url)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(url)) return result;

        var queryStart = url.IndexOf('?');
        var query      = queryStart >= 0 ? url[(queryStart + 1)..] : url;
        var fragment   = query.IndexOf('#');
        if (fragment >= 0) query = query[..fragment];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) { result[DecodeComponent(pair)] = string.Empty; continue; }
            var key   = DecodeComponent(pair[..idx]);
            var value = DecodeComponent(pair[(idx + 1)..]);
            result[key] = value;
        }
        return result;

        // "+" is a space in a query component; Uri.UnescapeDataString leaves it alone.
        static string DecodeComponent(string component) => Uri.UnescapeDataString(component.Replace('+', ' '));
    }

    /// <summary>Extracts just the domain (host) from a URL. Returns empty string on failure.</summary>
    public static string GetDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return string.Empty;
    }

    /// <summary>Extracts the path component from a URL (without query string or fragment).</summary>
    public static string GetPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath;
        return string.Empty;
    }

    /// <summary>Returns true if the URL uses HTTPS.</summary>
    public static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
