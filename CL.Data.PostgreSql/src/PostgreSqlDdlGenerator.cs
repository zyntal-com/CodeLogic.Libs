using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.PostgreSql;

/// <summary>
/// PostgreSQL DDL: identity columns, <c>ALTER COLUMN ... TYPE ... USING</c>, native <c>INCLUDE</c>, and the
/// <c>[PostgresTable]</c> options (<c>UNLOGGED</c>, <c>fillfactor</c>, <c>TABLESPACE</c>) when a table is created.
/// </summary>
public sealed class PostgreSqlDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
    /// <inheritdoc />
    protected override string CreateTableKeyword(TableSchema table) =>
        table.Options.ContainsKey(PostgreSqlTableOptions.Unlogged) ? "CREATE UNLOGGED TABLE " : "CREATE TABLE ";

    /// <inheritdoc />
    protected override string TableSuffix(TableSchema table)
    {
        var suffix = "";
        if (table.Options.TryGetValue(PostgreSqlTableOptions.FillFactor, out var fill) && int.TryParse(fill, System.Globalization.CultureInfo.InvariantCulture, out var factor))
            suffix += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" WITH (fillfactor = {Math.Clamp(factor, 10, 100)})");
        if (table.Options.TryGetValue(PostgreSqlTableOptions.Tablespace, out var tablespace) && tablespace.Length > 0)
            suffix += " TABLESPACE " + Q(tablespace);
        return suffix;
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> IdentityInsertTeardown(TableSchema table)
    {
        // Explicit identity values do not advance the sequence; move it past the highest restored value.
        var quoted = Table(table.Name, table.Schema);
        var literal = "'" + quoted.Replace("'", "''", StringComparison.Ordinal) + "'";
        return table.Columns.Where(c => c.Identity).Select(c =>
            $"SELECT setval(pg_get_serial_sequence({literal}, '{c.Name.Replace("'", "''", StringComparison.Ordinal)}'), " +
            $"COALESCE(MAX({Q(c.Name)}), 1), MAX({Q(c.Name)}) IS NOT NULL) FROM {quoted}").ToArray();
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> AlterColumn(AlterColumnChange change)
    {
        var table = Table(change.Table, change.Schema);
        var column = Q(change.To.Name);
        List<string> statements = [];
        if (!string.Equals(change.From.StoreType, change.To.StoreType, StringComparison.OrdinalIgnoreCase))
            statements.Add($"ALTER TABLE {table} ALTER COLUMN {column} TYPE {change.To.StoreType} USING {column}::{change.To.StoreType}");
        if (change.From.Nullable != change.To.Nullable)
            statements.Add($"ALTER TABLE {table} ALTER COLUMN {column} {(change.To.Nullable ? "DROP NOT NULL" : "SET NOT NULL")}");
        return statements;
    }
}

/// <summary>Table options of PostgreSQL tables, from <c>[PostgresTable]</c>.</summary>
internal static class PostgreSqlTableOptions
{
    public const string Unlogged = "unlogged";
    public const string FillFactor = "fillfactor";
    public const string Tablespace = "tablespace";

    public static IReadOnlyDictionary<string, string> For(CL.Data.Mapping.EntityMap map)
    {
        var result = new Dictionary<string, string>();
        if (map.Hint<PostgresTableAttribute>() is not { } hint) return result;
        if (hint.Unlogged) result[Unlogged] = "true";
        if (hint.FillFactor > 0) result[FillFactor] = hint.FillFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(hint.Tablespace)) result[Tablespace] = hint.Tablespace;
        return result;
    }
}
