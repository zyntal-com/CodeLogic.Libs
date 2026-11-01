using System.Text.RegularExpressions;
using CL.Data.Errors;
using CL.Data.Schema;
using CodeLogic.Core.Results;

namespace CL.Data.Backup;

/// <summary>
/// Checks a backup manifest before a restore turns it into DDL. Table, column, index and foreign-key names are always
/// quoted, but store types, collations, defaults and table options are written into the DDL as they are, so anyone who
/// can write the backup directory could otherwise run any SQL. Each of them must match a strict grammar, and every rows
/// file must stay inside the backup directory.
/// </summary>
internal static partial class BackupValidator
{
    // varchar(64), decimal(18,2), nvarchar(max), double precision, timestamp(3) with time zone, int unsigned, text[]
    [GeneratedRegex(@"^[a-z][a-z0-9_]*(?: (?<word>[a-z][a-z0-9_]*))*(?:\(\s*(?:max|\d+)\s*(?:,\s*\d+\s*)?\))?(?: (?<word>[a-z][a-z0-9_]*))*(?:\[\])*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoreTypePattern();

    // MySQL enum('a','b') / set('a','b'): single-quoted members, quotes doubled, no backslashes.
    [GeneratedRegex(@"^(?:enum|set)\(\s*'(?:[^'\\]|'')*'(?:\s*,\s*'(?:[^'\\]|'')*')*\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnumTypePattern();

    [GeneratedRegex(@"^(?:[A-Za-z0-9_]+|""[A-Za-z0-9_.@ \-]+"")$", RegexOptions.CultureInvariant)]
    private static partial Regex CollationPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionPattern();

    // Words a default may use: literals, clock and id functions, casts. Anything else (SELECT, a column, ...) is rejected.
    private static readonly HashSet<string> DefaultWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "true", "false", "current_timestamp", "current_date", "current_time", "localtimestamp", "localtime", "now",
        "getdate", "getutcdate", "sysdatetime", "sysutcdatetime", "sysdatetimeoffset", "newid", "newsequentialid",
        "gen_random_uuid", "uuid_generate_v4", "uuid", "utc_timestamp", "datetime", "date", "time", "strftime", "julianday",
        "convert", "cast", "as", "json_array", "json_object", "timezone", "statement_timestamp", "transaction_timestamp",
        "clock_timestamp",
    };

    // Words that may follow the first word of a type (character varying, timestamp with time zone, int unsigned): never
    // NOT NULL, DEFAULT, REFERENCES, CHECK or other clauses.
    private static readonly HashSet<string> TypeContinuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "varying", "precision", "with", "without", "time", "zone", "unsigned", "signed", "zerofill",
        "to", "year", "month", "day", "hour", "minute", "second",   // PostgreSQL interval fields
    };

    /// <summary>The first problem in <paramref name="manifest"/>, or null when it may be restored from <paramref name="directory"/>.</summary>
    public static Error? Check(BackupManifest manifest, string directory)
    {
        var root = Path.GetFullPath(directory);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        foreach (var entry in manifest.Tables)
        {
            if (entry.Definition is null) return Invalid(entry.Table, "the table definition is missing");
            if (string.IsNullOrEmpty(entry.File) || Path.IsPathRooted(entry.File)
                || !Path.GetFullPath(Path.Combine(root, entry.File)).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Invalid(entry.Table, $"rows file '{entry.File}' is outside the backup directory");

            foreach (var (key, value) in entry.Definition.Options)
                if (!OptionPattern().IsMatch(key) || !OptionPattern().IsMatch(value))
                    return Invalid(entry.Table, $"table option '{key}' has an unsafe value");

            foreach (var column in entry.Definition.Columns)
            {
                if (!IsStoreType(column.StoreType)) return Invalid(entry.Table, $"column '{column.Name}' has an unsafe type");
                if (column.Collation is { } collation && !CollationPattern().IsMatch(collation))
                    return Invalid(entry.Table, $"column '{column.Name}' has an unsafe collation");
                if (column.DefaultSql is { } defaultSql && !IsSafeDefault(defaultSql))
                    return Invalid(entry.Table, $"column '{column.Name}' has a default that is not a plain literal or a known function");
            }
        }
        return null;
    }

    public static bool IsStoreType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 200) return false;
        if (EnumTypePattern().IsMatch(type)) return true;
        var match = StoreTypePattern().Match(type);
        return match.Success && match.Groups["word"].Captures.All(w => TypeContinuations.Contains(w.Value));
    }

    /// <summary>
    /// True for a default made only of literals (numbers, quoted strings, <c>N'...'</c>, <c>b'0'</c>, <c>x'00'</c>), the words in
    /// <see cref="DefaultWords"/>, parentheses, commas, signs, <c>::type</c> casts and <c>[type]</c> (SQL Server's
    /// <c>CONVERT([bit],(0))</c>). No semicolons, comments, double quotes or other identifiers.
    /// </summary>
    public static bool IsSafeDefault(string sql)
    {
        if (sql.Length > 500) return false;
        var s = sql.AsSpan();
        var depth = 0;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (--depth < 0) return false; i++; continue; }
            if (c is ',' or '+' or '.') { i++; continue; }
            if (c == '-')
            {
                if (i + 1 < s.Length && s[i + 1] == '-') return false;   // comment
                i++;
                continue;
            }
            if (c == '\'' || (c is 'N' or 'n' or 'B' or 'b' or 'X' or 'x' or 'E' or 'e' && i + 1 < s.Length && s[i + 1] == '\''))
            {
                if (c != '\'') i++;
                i++;
                while (true)
                {
                    if (i >= s.Length) return false;
                    if (s[i] == '\\') return false;   // backslash escapes differ by server mode: refuse them
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                // 1, 1.5, 1e3, 1.5E-3, 0x1F; a letter straight after a number (1union) is refused.
                if (c == '0' && i + 1 < s.Length && s[i + 1] is 'x' or 'X')
                {
                    i += 2;
                    while (i < s.Length && char.IsAsciiHexDigit(s[i])) i++;
                }
                else
                {
                    while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
                    if (i < s.Length && s[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < s.Length && s[i] is '+' or '-') i++;
                        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                    }
                }
                if (i < s.Length && (char.IsAsciiLetter(s[i]) || s[i] == '_')) return false;
                continue;
            }
            if (c == '[')
            {
                var close = s[i..].IndexOf(']');
                if (close < 2) return false;
                foreach (var ch in s.Slice(i + 1, close - 1)) if (!char.IsAsciiLetterOrDigit(ch) && ch != '_') return false;
                i += close + 1;
                continue;
            }
            if (c == ':' && i + 1 < s.Length && s[i + 1] == ':')
            {
                i += 2;
                while (i < s.Length && s[i] == ' ') i++;
                var word = Word(s, ref i);
                if (word.Length == 0) return false;
                while (true)
                {
                    var save = i;
                    while (i < s.Length && s[i] == ' ') i++;
                    var next = Word(s, ref i);
                    if (next.Length == 0 || !TypeContinuations.Contains(next)) { i = save; break; }
                }
                if (i + 1 < s.Length && s[i] == '[' && s[i + 1] == ']') i += 2;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                if (!DefaultWords.Contains(Word(s, ref i))) return false;
                continue;
            }
            return false;
        }
        return depth == 0;
    }

    private static string Word(ReadOnlySpan<char> s, ref int i)
    {
        var start = i;
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return s[start..i].ToString();
    }

    private static Error Invalid(string table, string problem) =>
        DataErrors.Validation($"The backup manifest is not safe to restore: table '{table}': {problem}.");
}
