using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.PostgreSql;

/// <summary>PostgreSQL DDL: identity columns, <c>ALTER COLUMN ... TYPE ... USING</c>, native <c>INCLUDE</c>.</summary>
public sealed class PostgreSqlDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
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
