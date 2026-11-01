using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.SqlServer;

/// <summary>
/// SQL Server DDL: <c>IDENTITY(1,1)</c>, <c>ADD</c> without <c>COLUMN</c>, single-statement <c>ALTER COLUMN</c>,
/// <c>sp_rename</c>, <c>DROP INDEX ... ON</c>, default constraints dropped before their column, and the
/// <c>[SqlServerTable]</c> options (<c>ON filegroup</c>, <c>DATA_COMPRESSION</c>) when a table is created.
/// </summary>
public sealed class SqlServerDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
    /// <inheritdoc />
    protected override string TableSuffix(TableSchema table)
    {
        var suffix = "";
        if (table.Options.TryGetValue(SqlServerTableOptions.FileGroup, out var fileGroup) && fileGroup.Length > 0)
            suffix += " ON " + Q(fileGroup);
        if (table.Options.TryGetValue(SqlServerTableOptions.DataCompression, out var compression))
            suffix += " WITH (DATA_COMPRESSION = " + compression + ")";
        return suffix;
    }

    /// <inheritdoc />
    protected override string IdentityClause(ColumnSchema column) => " IDENTITY(1,1)";

    /// <inheritdoc />
    public override IReadOnlyList<string> IdentityInsertSetup(TableSchema table) => [$"SET IDENTITY_INSERT {Table(table.Name, table.Schema)} ON"];

    /// <inheritdoc />
    public override IReadOnlyList<string> IdentityInsertTeardown(TableSchema table) => [$"SET IDENTITY_INSERT {Table(table.Name, table.Schema)} OFF"];

    /// <inheritdoc />
    protected override string AddColumnKeyword => "ADD";

    /// <summary>JSON columns (<c>nvarchar(max)</c>) get <c>CHECK (ISJSON(col) = 1)</c>, so invalid JSON is rejected like on the 4.x library.</summary>
    protected override string ColumnDefinition(ColumnSchema column, TableSchema? table) =>
        column.Json ? $"{base.ColumnDefinition(column, table)} CHECK (ISJSON({Q(column.Name)}) = 1)" : base.ColumnDefinition(column, table);

    /// <inheritdoc />
    protected override string ImplicitDefault(ColumnSchema column) =>
        column.StoreType.StartsWith("bool", StringComparison.Ordinal) ? "0" : base.ImplicitDefault(column);

    /// <inheritdoc />
    protected override IReadOnlyList<string> AlterColumn(AlterColumnChange change) =>
        [$"ALTER TABLE {Table(change.Table, change.Schema)} ALTER COLUMN {Q(change.To.Name)} {change.To.StoreType} {(change.To.Nullable ? "NULL" : "NOT NULL")}"];

    /// <inheritdoc />
    protected override string RenameColumn(RenameColumnChange change)
    {
        // The object path is quoted part by part, so names holding dots or brackets resolve to the right column.
        var path = Table(change.Table, change.Schema) + "." + Q(change.From);
        return $"EXEC sp_rename N'{Escape(path)}', N'{Escape(change.To)}', N'COLUMN'";
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropColumn(DropColumnChange change)
    {
        // A column with a default constraint cannot be dropped until the constraint is.
        // The constraint name comes from the catalog and goes through QUOTENAME, so a name holding ] cannot break out.
        var table = Table(change.Table, change.Schema);
        return
        [
            $"DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.default_object_id = dc.object_id " +
            $"WHERE dc.parent_object_id = OBJECT_ID(N'{Escape(table)}') AND c.name = N'{Escape(change.Column)}'); " +
            $"IF @df IS NOT NULL EXEC(N'ALTER TABLE {Escape(table)} DROP CONSTRAINT ' + QUOTENAME(@df))",
            $"ALTER TABLE {table} DROP COLUMN {Q(change.Column)}",
        ];
    }

    /// <summary>
    /// A unique index over nullable columns is filtered (<c>WHERE col IS NOT NULL</c>): SQL Server otherwise treats NULLs
    /// as equal and allows one row without a value, unlike the other providers.
    /// </summary>
    protected override string CreateIndex(string table, string? schema, IndexSchema index)
    {
        var sql = base.CreateIndex(table, schema, index);
        return index.Unique && index.NullableColumns.Count > 0
            ? sql + " WHERE " + string.Join(" AND ", index.NullableColumns.Select(c => Q(c) + " IS NOT NULL"))
            : sql;
    }

    /// <inheritdoc/>
    protected override string DropIndex(DropIndexChange change) => $"DROP INDEX {Q(change.Name)} ON {Table(change.Table, change.Schema)}";

    /// <inheritdoc />
    protected override string Action(ForeignKeyAction action) =>
        action == ForeignKeyAction.Restrict ? "NO ACTION" : base.Action(action);   // SQL Server has no RESTRICT

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

/// <summary>Table options of SQL Server tables, from <c>[SqlServerTable]</c>.</summary>
internal static class SqlServerTableOptions
{
    public const string FileGroup = "filegroup";
    public const string DataCompression = "data_compression";

    public static IReadOnlyDictionary<string, string> For(CL.Data.Mapping.EntityMap map)
    {
        var result = new Dictionary<string, string>();
        if (map.Hint<SqlServerTableAttribute>() is not { } hint) return result;
        if (!string.IsNullOrWhiteSpace(hint.FileGroup)) result[FileGroup] = hint.FileGroup;
        // Only the documented values reach the DDL; anything else is ignored rather than spliced into it.
        var compression = hint.DataCompression?.Trim().ToUpperInvariant();
        if (compression is "NONE" or "ROW" or "PAGE") result[DataCompression] = compression;
        return result;
    }
}
