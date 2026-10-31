using System.Text.RegularExpressions;

namespace CL.Data.Schema;

/// <summary>Helpers providers use to normalize the type names their catalogs report.</summary>
public static partial class StoreTypeNormalizer
{
    /// <summary>MySQL: drops integer display widths (<c>int(11)</c> → <c>int</c>) except <c>tinyint(1)</c>; maps aliases.</summary>
    public static string MySql(string type)
    {
        type = IntDisplayWidth().Replace(type, m => m.Groups["base"].Value == "tinyint" && m.Groups["width"].Value == "1" ? m.Value : m.Groups["base"].Value);
        return type switch
        {
            "bool" or "boolean" => "tinyint(1)",
            "integer" => "int",
            _ when type.StartsWith("numeric(", StringComparison.Ordinal) => "decimal" + type["numeric".Length..],
            _ => type,
        };
    }

    /// <summary>PostgreSQL: <c>timestamp(6) with time zone</c> → <c>timestamptz(6)</c>, <c>time(6) without time zone</c> → <c>time(6)</c>.</summary>
    public static string PostgreSqlTimes(string type)
    {
        var m = PgTime().Match(type);
        if (!m.Success) return type;
        var zoned = m.Groups["zone"].Value == "with";
        return (m.Groups["base"].Value, zoned) switch
        {
            ("timestamp", true) => "timestamptz" + m.Groups["size"].Value,
            ("time", true) => "timetz" + m.Groups["size"].Value,
            _ => m.Groups["base"].Value + m.Groups["size"].Value,
        };
    }

    /// <summary>SQL Server: adds the default fractional precision to <c>datetime2</c>, <c>time</c> and <c>datetimeoffset</c>.</summary>
    public static string SqlServer(string type) => type switch
    {
        "datetime2" or "time" or "datetimeoffset" => type + "(7)",
        "timestamp" => "rowversion",
        "numeric" => "decimal(18,0)",
        _ when type.StartsWith("numeric(", StringComparison.Ordinal) => "decimal" + type["numeric".Length..],
        _ => type,
    };

    [GeneratedRegex(@"^(?<base>tinyint|smallint|mediumint|int|bigint)\((?<width>\d+)\)")]
    private static partial Regex IntDisplayWidth();

    [GeneratedRegex(@"^(?<base>timestamp|time)(?<size>\(\d+\))? (?<zone>with|without) time zone$")]
    private static partial Regex PgTime();
}
