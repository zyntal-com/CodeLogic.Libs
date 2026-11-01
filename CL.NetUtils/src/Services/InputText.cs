using System.Globalization;
using CL.NetUtils.Localization;

namespace CL.NetUtils.Services;

/// <summary>Helpers for caller-supplied text that reaches logs or DNS names.</summary>
internal static class InputText
{
    private const int MaxLoggedLength = 64;

    /// <summary>The default (English) strings, used until the library supplies localized ones.</summary>
    internal static readonly NetUtilsStrings DefaultStrings = new();

    /// <summary>
    /// Makes caller input safe to log: control characters (CR, LF, ...) become <c>?</c>, so a crafted "IP address" cannot
    /// forge log lines, and long input is cut to 64 characters.
    /// </summary>
    internal static string ForLog(string? value)
    {
        if (value is null) return "(null)";
        var cut = value.Length > MaxLoggedLength;
        var span = cut ? value.AsSpan(0, MaxLoggedLength) : value.AsSpan();
        var chars = new char[span.Length];
        for (var i = 0; i < span.Length; i++)
            chars[i] = char.IsControl(span[i]) || char.GetUnicodeCategory(span[i]) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? '?'
                : span[i];
        return cut ? new string(chars) + "..." : new string(chars);
    }

    /// <summary>Formats a localized template with the current culture.</summary>
    internal static string Format(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>
    /// Normalizes a DNSBL zone (trims whitespace and a trailing dot) and checks that it is a DNS name: 1 to 63 character
    /// labels of letters, digits, <c>-</c> and <c>_</c>, at least two labels, not starting or ending a label with
    /// <c>-</c>, and short enough that the query name (<paramref name="prefixLength"/> characters plus a dot plus the
    /// zone) stays within 253 characters. Returns <see langword="null"/> for anything else, so a malformed or hostile
    /// entry (an IP literal, spaces, an empty line) is never sent to the resolver.
    /// </summary>
    internal static string? NormalizeZone(string? zone, int prefixLength)
    {
        if (string.IsNullOrWhiteSpace(zone)) return null;
        var z = zone.Trim().TrimEnd('.');
        if (z.Length == 0 || prefixLength + 1 + z.Length > 253) return null;

        var labels = z.Split('.');
        if (labels.Length < 2) return null;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return null;
            foreach (var c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        }
        // An all-numeric last label would make the query name look like an IP literal.
        if (labels[^1].All(char.IsAsciiDigit)) return null;
        return z;
    }
}
