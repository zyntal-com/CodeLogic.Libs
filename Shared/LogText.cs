using System.Globalization;

namespace CL.Internal;

/// <summary>Formats untrusted values for a single bounded log field; credential redaction is separate.</summary>
internal static class LogText
{
    internal static string Sanitize(string? value, int maxLength = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (value is null) return "(null)";
        var length = Math.Min(value.Length, maxLength);
        var result = string.Create(length, value, static (span, input) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = input[i];
                span[i] = char.IsControl(c) || char.GetUnicodeCategory(c) is
                    UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format
                    ? '?' : c;
            }
        });
        return length < value.Length ? result + "..." : result;
    }
}
