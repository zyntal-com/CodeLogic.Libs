using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.PostgreSql;

/// <summary>PostgreSQL DDL: identity columns, <c>ALTER COLUMN ... TYPE ... USING</c>, native <c>INCLUDE</c>.</summary>
public sealed class PostgreSqlDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
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
