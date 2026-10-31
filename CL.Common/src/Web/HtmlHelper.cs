using System.Text.RegularExpressions;

namespace CL.Common.Web;

/// <summary>
/// Provides HTML sanitization and trimming utilities.
/// </summary>
public static class HtmlHelper
{
    private static readonly Regex TagRegex          = new(@"<[^>]+>",    RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex   = new(@"\s{2,}",    RegexOptions.Compiled);

    /// <summary>
    /// Strips all HTML tags, returning plain text.
    /// Does not decode HTML entities (e.g. &amp;amp; stays as &amp;amp;).
    /// </summary>
    public static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return TagRegex.Replace(html, string.Empty);
    }

    /// <summary>Formatting elements <see cref="Sanitize"/> keeps (without attributes).</summary>
    public static IReadOnlySet<string> SanitizeAllowedTags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "hr", "b", "strong", "i", "em", "u", "s", "del", "ins", "mark", "small", "sub", "sup",
        "blockquote", "pre", "code", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "span", "div", "a"
    };

    private static readonly HashSet<string> DroppedWithContent =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "script", "style", "iframe", "object", "embed", "applet", "template", "noscript", "textarea", "title",
        "svg", "math", "select", "xmp", "noembed", "noframes"
    ];

    private static readonly Regex SanitizeTagRegex = new(
        @"<!--.*?(-->|$)|<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>|<",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex HrefRegex = new(
        @"(?<![\w-])href\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Allowlist sanitizer for simple user HTML: keeps the formatting elements in
    /// <see cref="SanitizeAllowedTags"/> with no attributes (links keep an <c>href</c> when it is http, https,
    /// mailto or relative, and get <c>rel="nofollow noopener noreferrer"</c>); removes script, style, iframe,
    /// object, svg and similar elements with their content, and comments; every other tag is removed and
    /// its text kept. Text outside tags has <c>&lt;</c>, <c>&gt;</c> and quotes encoded (entities are kept).
    /// The output is rebuilt, never copied, so attribute tricks (unquoted handlers, <c>javascript:</c> URLs,
    /// malformed tags) cannot survive. For rich text with styles, tables or embeds, use a full HTML
    /// sanitizer such as CL.WebLogic's <c>WebHtmlSanitizer</c>.
    /// </summary>
    public static string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var output = new System.Text.StringBuilder(html.Length);
        string? dropping = null;
        var position = 0;
        foreach (Match match in SanitizeTagRegex.Matches(html))
        {
            if (dropping is null)
                AppendText(output, html.AsSpan(position, match.Index - position));
            position = match.Index + match.Length;

            var name = match.Groups["name"].Value;
            if (name.Length == 0)
            {
                if (dropping is null && match.Value == "<") output.Append("&lt;");
                continue; // comment, or a lone '<'
            }

            var closing = match.Groups["close"].Value.Length > 0;
            if (dropping is not null)
            {
                if (closing && name.Equals(dropping, StringComparison.OrdinalIgnoreCase)) dropping = null;
                continue;
            }
            if (DroppedWithContent.Contains(name))
            {
                if (!closing && !match.Value.EndsWith("/>", StringComparison.Ordinal)) dropping = name;
                continue;
            }
            if (!SanitizeAllowedTags.Contains(name)) continue;

            var tag = name.ToLowerInvariant();
            if (closing)
            {
                if (tag is not ("br" or "hr")) output.Append("</").Append(tag).Append('>');
                continue;
            }
            output.Append('<').Append(tag);
            if (tag == "a" && HrefRegex.Match(match.Groups["attrs"].Value) is { Success: true } href
                && SafeHref(System.Net.WebUtility.HtmlDecode(href.Groups["v"].Value)) is { } url)
            {
                output.Append(" href=\"").Append(Encode(url)).Append("\" rel=\"nofollow noopener noreferrer\"");
            }
            output.Append('>');
        }
        if (dropping is null && position < html.Length)
            AppendText(output, html.AsSpan(position));
        return output.ToString().Trim();
    }

    private static void AppendText(System.Text.StringBuilder output, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '"': output.Append("&quot;"); break;
                default: output.Append(c); break;
            }
        }
    }

    /// <summary>The URL when it is http(s), mailto or relative (no scheme), otherwise null.</summary>
    private static string? SafeHref(string url)
    {
        var compact = new string(url.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) return null;
        var colon = compact.IndexOf(':');
        var slash = compact.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (slash >= 0 && slash < colon)) return compact; // relative
        var scheme = compact[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" ? compact : null;
    }

    /// <summary>
    /// Trims whitespace inside HTML, collapsing multiple spaces/newlines to single spaces.
    /// Preserves HTML structure.
    /// </summary>
    public static string TrimWhitespace(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return WhitespaceRegex.Replace(html, " ").Trim();
    }

    /// <summary>
    /// Encodes a plain-text string so it is safe to embed in HTML.
    /// Converts &lt; &gt; &amp; &quot; to their HTML entity equivalents.
    /// </summary>
    public static string Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&",  "&amp;")
            .Replace("<",  "&lt;")
            .Replace(">",  "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'",  "&#39;");
    }

    /// <summary>
    /// Decodes HTML entities back to plain text.
    /// </summary>
    public static string Decode(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return html
            .Replace("&amp;",  "&")
            .Replace("&lt;",   "<")
            .Replace("&gt;",   ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;",  "'")
            .Replace("&nbsp;", " ");
    }

    /// <summary>
    /// Truncates HTML content to approximately <paramref name="maxLength"/> visible characters,
    /// stripping tags first. Appends <paramref name="suffix"/> if truncated.
    /// </summary>
    public static string TruncateText(string html, int maxLength, string suffix = "...")
    {
        var text = StripTags(html);
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - suffix.Length)] + suffix;
    }
}
