using System.Globalization;

namespace CL.SocialConnect.Models;

/// <summary>Helpers for caller-supplied text that reaches logs, events or error messages.</summary>
internal static class InputText
{
    private const int MaxLoggedLength = 64;

    /// <summary>
    /// Makes caller input safe to log or publish: control characters (CR, LF, ...) and line separators become
    /// <c>?</c>, so a crafted parameter cannot forge log lines, and long input is cut to 64 characters.
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
}
