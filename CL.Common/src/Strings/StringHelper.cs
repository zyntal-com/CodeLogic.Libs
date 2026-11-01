using System.Text;
using System.Text.RegularExpressions;

namespace CL.Common.Strings;

/// <summary>
/// Provides string manipulation utilities: truncation, slugification,
/// case conversion, HTML stripping, and more.
/// </summary>
public static partial class StringHelper
{
    // Source-generated: no parsing, no Regex cache lookup and no interpreter on each call.
    [GeneratedRegex(@"[_\s]+")] private static partial Regex SlugSeparators();
    [GeneratedRegex(@"[^a-z0-9\-]")] private static partial Regex SlugInvalidChars();
    [GeneratedRegex(@"-{2,}")] private static partial Regex RepeatedHyphens();
    [GeneratedRegex(@"[\s_\-]+")] private static partial Regex WordSeparators();
    [GeneratedRegex(@"([a-z0-9])([A-Z])")] private static partial Regex LowerUpperBoundary();
    [GeneratedRegex(@"[\s\-]+")] private static partial Regex SpacesAndHyphens();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"\S+")] private static partial Regex NonWhitespaceRun();
    [GeneratedRegex(@"\d+")] private static partial Regex DigitRun();

    /// <summary>
    /// Truncates a string to <paramref name="maxLength"/> characters,
    /// appending <paramref name="suffix"/> if truncation occurs. The result never exceeds
    /// <paramref name="maxLength"/>: when the suffix itself does not fit, the value is clipped without it,
    /// and a <paramref name="maxLength"/> of zero or less yields an empty string.
    /// </summary>
    public static string Truncate(string value, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
        if (maxLength <= 0) return string.Empty;
        suffix ??= string.Empty;
        if (suffix.Length >= maxLength) return value[..maxLength];
        return value[..(maxLength - suffix.Length)] + suffix;
    }

    /// <summary>
    /// Converts a string to a URL-friendly slug.
    /// Lowercases, replaces spaces/underscores with hyphens,
    /// removes non-alphanumeric characters, and collapses multiple hyphens.
    /// </summary>
    public static string ToSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var slug = value.ToLowerInvariant().Trim();
        slug = SlugSeparators().Replace(slug, "-");
        slug = SlugInvalidChars().Replace(slug, "");
        slug = RepeatedHyphens().Replace(slug, "-");
        return slug.Trim('-');
    }

    /// <summary>
    /// Converts a string to camelCase.
    /// Example: "hello world" → "helloWorld"
    /// </summary>
    public static string ToCamelCase(string value)
    {
        var pascal = ToPascalCase(value);
        if (pascal.Length == 0) return pascal;
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>
    /// Converts a string to PascalCase.
    /// Example: "hello world" → "HelloWorld"
    /// </summary>
    public static string ToPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var words = WordSeparators().Split(value.Trim());
        return string.Concat(words.Select(w => w.Length > 0
            ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()
            : string.Empty));
    }

    /// <summary>
    /// Converts a string to snake_case.
    /// Example: "HelloWorld" → "hello_world"
    /// </summary>
    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        // Insert underscore before uppercase letters that follow lowercase
        var result = LowerUpperBoundary().Replace(value.Trim(), "$1_$2");
        result = SpacesAndHyphens().Replace(result, "_");
        return result.ToLowerInvariant().Trim('_');
    }

    /// <summary>
    /// Converts a string to kebab-case.
    /// Example: "HelloWorld" → "hello-world"
    /// </summary>
    public static string ToKebabCase(string value) =>
        ToSnakeCase(value).Replace('_', '-');

    /// <summary>
    /// Removes all HTML tags from the input, returning plain text.
    /// Does not decode HTML entities.
    /// </summary>
    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        return HtmlTag().Replace(html, string.Empty);
    }

    /// <summary>Returns the number of words in the text (split on whitespace).</summary>
    public static int WordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return NonWhitespaceRun().Count(text.Trim());
    }

    /// <summary>Repeats <paramref name="value"/> exactly <paramref name="count"/> times.</summary>
    public static string Repeat(string value, int count)
    {
        if (string.IsNullOrEmpty(value) || count <= 0) return string.Empty;
        var sb = new StringBuilder(value.Length * count);
        for (int i = 0; i < count; i++) sb.Append(value);
        return sb.ToString();
    }

    /// <summary>Reverses the characters in a string.</summary>
    public static string Reverse(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return new string(value.Reverse().ToArray());
    }

    /// <summary>
    /// Returns true if <paramref name="value"/> contains any of the given substrings.
    /// Case-sensitive by default.
    /// </summary>
    public static bool ContainsAny(string value, params string[] substrings) =>
        substrings.Any(s => value.Contains(s, StringComparison.Ordinal));

    /// <summary>Counts how many times <paramref name="substring"/> appears in <paramref name="text"/>.</summary>
    public static int CountOccurrences(string text, string substring)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(substring)) return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }

    /// <summary>Extracts all numeric sequences from the text and returns them as longs.</summary>
    public static IEnumerable<long> ExtractNumbers(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in DigitRun().Matches(text))
            if (long.TryParse(m.Value, out var n)) yield return n;
    }

    /// <summary>Returns true if the string contains only ASCII characters.</summary>
    public static bool IsAscii(string value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c < 128);

    /// <summary>Converts the first character to uppercase and the rest to lowercase.</summary>
    public static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
