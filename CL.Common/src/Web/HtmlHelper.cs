using System.Text;
using System.Text.RegularExpressions;

namespace CL.Common.Web;

/// <summary>
/// Provides HTML sanitization and trimming utilities. Every method runs in time linear in the input
/// length, so untrusted input of any size (malformed tags, unbalanced quotes, runs of <c>&lt;</c>) cannot
/// stall the caller.
/// </summary>
public static partial class HtmlHelper
{
    // NonBacktracking: a run of '<' without '>' made the backtracking engine rescan to the end from every one.
    [GeneratedRegex(@"<[^>]+>", RegexOptions.NonBacktracking)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Strips all HTML tags, returning plain text.
    /// Does not decode HTML entities (e.g. &amp;amp; stays as &amp;amp;).
    /// </summary>
    public static string StripTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return TagRegex().Replace(html, string.Empty);
    }

    // Declared before SanitizeAllowedTags: static initializers run in textual order.
    private static readonly HashSet<string> AllowedTags =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "p", "br", "hr", "b", "strong", "i", "em", "u", "s", "del", "ins", "mark", "small", "sub", "sup",
        "blockquote", "pre", "code", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "span", "div", "a"
    ];

    /// <summary>Formatting elements <see cref="Sanitize"/> keeps (without attributes).</summary>
    public static IReadOnlySet<string> SanitizeAllowedTags { get; } = AllowedTags;

    private static readonly HashSet<string> DroppedWithContent =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "script", "style", "iframe", "object", "embed", "applet", "template", "noscript", "textarea", "title",
        "svg", "math", "select", "xmp", "noembed", "noframes"
    ];

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> AllowedLookup =
        AllowedTags.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> DroppedLookup =
        DroppedWithContent.GetAlternateLookup<ReadOnlySpan<char>>();

    // Runs on one tag's attribute text only, which the scanner has already bounded.
    [GeneratedRegex(@"(?<![\w-])href\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

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
    /// <remarks>
    /// Tags are found by a single linear scan (no regular expression), so pathological input such as
    /// <c>&lt;a &lt;a &lt;a …</c> or thousands of unbalanced quotes costs O(n) time and O(n) memory.
    /// A tag runs from <c>&lt;</c> through the first <c>&gt;</c> outside quotes; a <c>&lt;</c> with no such
    /// <c>&gt;</c> is text.
    /// </remarks>
    public static string Sanitize(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var tagEnd = BuildTagEndTable(html);
        var output = new StringBuilder(html.Length);
        string? dropping = null;
        var position = 0;   // start of the text not yet copied
        var i = html.IndexOf('<');
        while (i >= 0)
        {
            if (dropping is null)
                AppendText(output, html.AsSpan(position, i - position));

            if (html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                // A comment ends at the first "-->", or at the end of the input.
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                position = close < 0 ? html.Length : close + 3;
            }
            else
            {
                var p = i + 1;
                var closing = p < html.Length && html[p] == '/';
                if (closing) p++;
                var nameStart = p;
                if (p < html.Length && char.IsAsciiLetter(html[p]))
                {
                    p++;
                    while (p < html.Length && char.IsAsciiLetterOrDigit(html[p])) p++;
                }
                var end = p > nameStart ? tagEnd[p] : -1;

                if (end < 0)
                {
                    // Not a tag: a lone '<' is text, and scanning resumes right after it.
                    if (dropping is null) output.Append("&lt;");
                    position = i + 1;
                }
                else
                {
                    position = end + 1;
                    var name = html.AsSpan(nameStart, p - nameStart);
                    var selfClosing = html[end - 1] == '/';
                    EmitTag(output, html, name, closing, selfClosing, html.AsSpan(p, end - p), ref dropping);
                }
            }

            i = position < html.Length ? html.IndexOf('<', position) : -1;
        }
        if (dropping is null && position < html.Length)
            AppendText(output, html.AsSpan(position));
        return output.ToString().Trim();
    }

    /// <summary>
    /// For every position <c>p</c>, the index of the <c>&gt;</c> that ends a tag whose attribute text starts
    /// at <c>p</c> (the first <c>&gt;</c> outside <c>"…"</c> / <c>'…'</c> quoting), or -1 when the input ends
    /// first. One right-to-left pass, so every tag start is answered in O(1) and the whole scan is linear.
    /// </summary>
    private static int[] BuildTagEndTable(string html)
    {
        var n = html.Length;
        var table = new int[n + 1];
        table[n] = -1;
        int nextDouble = -1, nextSingle = -1;   // nearest quote to the right of p
        for (var p = n - 1; p >= 0; p--)
        {
            var c = html[p];
            table[p] = c switch
            {
                '>' => p,
                '"' => nextDouble < 0 ? -1 : table[nextDouble + 1],
                '\'' => nextSingle < 0 ? -1 : table[nextSingle + 1],
                _ => table[p + 1],
            };
            if (c == '"') nextDouble = p;
            else if (c == '\'') nextSingle = p;
        }
        return table;
    }

    private static void EmitTag(StringBuilder output, string html, ReadOnlySpan<char> name, bool closing, bool selfClosing,
        ReadOnlySpan<char> attrs, ref string? dropping)
    {
        if (dropping is not null)
        {
            if (closing && name.Equals(dropping, StringComparison.OrdinalIgnoreCase)) dropping = null;
            return;
        }
        if (DroppedLookup.Contains(name))
        {
            if (!closing && !selfClosing) dropping = name.ToString();
            return;
        }
        if (!AllowedLookup.Contains(name)) return;

        var tag = name.ToString().ToLowerInvariant();
        if (closing)
        {
            if (tag is not ("br" or "hr")) output.Append("</").Append(tag).Append('>');
            return;
        }
        output.Append('<').Append(tag);
        if (tag == "a" && HrefRegex().Match(attrs.ToString()) is { Success: true } href
            && SafeHref(System.Net.WebUtility.HtmlDecode(href.Groups["v"].Value)) is { } url)
        {
            output.Append(" href=\"").Append(Encode(url)).Append("\" rel=\"nofollow noopener noreferrer\"");
        }
        output.Append('>');
    }

    private static void AppendText(StringBuilder output, ReadOnlySpan<char> text)
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
        return WhitespaceRegex().Replace(html, " ").Trim();
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
