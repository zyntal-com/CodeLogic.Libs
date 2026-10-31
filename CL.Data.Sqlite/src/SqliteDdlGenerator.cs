using System.Text;
using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.Sqlite;

/// <summary>
/// SQLite DDL. Identity keys are <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> and foreign keys are declared inside
/// CREATE TABLE. SQLite cannot alter a column's type or add/drop constraints, so those changes rebuild the table:
/// create the new shape, copy the rows, drop the old table, rename, recreate indexes.
/// </summary>
public sealed class SqliteDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
    private const string RebuildSuffix = "__cl_rebuild";

    /// <inheritdoc />
    protected override bool SupportsInclude => false;

    /// <inheritdoc />
    protected override string IfNotExists => "IF NOT EXISTS ";   // a rebuild earlier in the plan may have created it

    /// <inheritdoc />
    protected override bool InlinePrimaryKey(TableSchema table) => IdentityKey(table) is not null;

    /// <inheritdoc />
    protected override string ColumnDefinition(ColumnSchema column, TableSchema? table)
    {
        if (column.Identity && table is not null && IdentityKey(table) == column)
            return $"{Q(column.Name)} INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL";
        return base.ColumnDefinition(column with { Identity = false }, table);
    }

    /// <inheritdoc />
    protected override IEnumerable<string> InlineForeignKeys(TableSchema table) => table.ForeignKeys.Select(ForeignKeyClause);

    /// <inheritdoc />
    protected override IEnumerable<string> PostCreateTable(TableSchema table) => [];

    /// <inheritdoc />
    protected override string TableSuffix(TableSchema table)
    {
        var suffix = new StringBuilder();
        if (table.Options.ContainsKey("without_rowid")) suffix.Append(" WITHOUT ROWID");
        if (table.Options.ContainsKey("strict")) suffix.Append(suffix.Length > 0 ? ", STRICT" : " STRICT");
        return suffix.ToString();
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> AlterColumn(AlterColumnChange change) => Rebuild(change.Desired);

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropColumn(DropColumnChange change) => Rebuild(change.Desired);

    /// <inheritdoc />
    protected override IReadOnlyList<string> AddForeignKey(AddForeignKeyChange change) => Rebuild(change.Desired);

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropForeignKey(DropForeignKeyChange change) => Rebuild(change.Desired);

    /// <summary>The statements that rebuild a table into <paramref name="desired"/>, keeping its rows.</summary>
    public IReadOnlyList<string> Rebuild(TableSchema desired)
    {
        var temp = desired.Name + RebuildSuffix;
        var columns = string.Join(", ", desired.Columns.Select(c => Q(c.Name)));
        var create = CreateTable(desired with { Name = temp, Indexes = [] });
        List<string> statements = [.. create];
        statements.Add($"INSERT INTO {Q(temp)} ({columns}) SELECT {columns} FROM {Q(desired.Name)}");
        statements.Add($"DROP TABLE {Q(desired.Name)}");
        statements.Add($"ALTER TABLE {Q(temp)} RENAME TO {Q(desired.Name)}");
        statements.AddRange(desired.Indexes.Select(i => CreateIndex(desired.Name, null, i)));
        return statements;
    }

    private static ColumnSchema? IdentityKey(TableSchema table) =>
        table.PrimaryKey.Count == 1 && table.Column(table.PrimaryKey[0]) is { Identity: true } column ? column : null;
}
