using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.SqlServer;

/// <summary>
/// SQL Server DDL: <c>IDENTITY(1,1)</c>, <c>ADD</c> without <c>COLUMN</c>, single-statement <c>ALTER COLUMN</c>,
/// <c>sp_rename</c>, <c>DROP INDEX ... ON</c>, and default constraints dropped before their column.
/// </summary>
public sealed class SqlServerDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
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
        var path = (change.Schema is null ? "" : change.Schema + ".") + change.Table + "." + change.From;
        return $"EXEC sp_rename N'{Escape(path)}', N'{Escape(change.To)}', N'COLUMN'";
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropColumn(DropColumnChange change)
    {
        // A column with a default constraint cannot be dropped until the constraint is.
        var table = Table(change.Table, change.Schema);
        var objectName = Escape((change.Schema is null ? "" : change.Schema + ".") + change.Table);
        return
        [
            $"DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns c ON c.default_object_id = dc.object_id " +
            $"WHERE dc.parent_object_id = OBJECT_ID(N'{objectName}') AND c.name = N'{Escape(change.Column)}'); " +
            $"IF @df IS NOT NULL EXEC(N'ALTER TABLE {Escape(table)} DROP CONSTRAINT [' + @df + N']')",
            $"ALTER TABLE {table} DROP COLUMN {Q(change.Column)}",
        ];
    }

    /// <inheritdoc />
    protected override string DropIndex(DropIndexChange change) => $"DROP INDEX {Q(change.Name)} ON {Table(change.Table, change.Schema)}";

    /// <inheritdoc />
    protected override string Action(ForeignKeyAction action) =>
        action == ForeignKeyAction.Restrict ? "NO ACTION" : base.Action(action);   // SQL Server has no RESTRICT

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
