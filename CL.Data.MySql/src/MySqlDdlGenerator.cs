using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.Schema;
using CL.Data.Sql;

namespace CL.Data.MySql;

/// <summary>MySQL DDL: <c>AUTO_INCREMENT</c>, <c>ENGINE</c>/<c>CHARSET</c>/<c>COLLATE</c> table options, <c>MODIFY COLUMN</c>,
/// <c>DROP INDEX ... ON</c>, <c>DROP FOREIGN KEY</c>; covering columns are appended to index keys.</summary>
public sealed class MySqlDdlGenerator(SqlDialect dialect) : DdlGenerator(dialect)
{
    /// <inheritdoc />
    protected override string IdentityClause(ColumnSchema column) => " AUTO_INCREMENT";

    /// <inheritdoc />
    public override bool TransactionalDdl => false;   // MySQL commits implicitly around DDL

    /// <inheritdoc />
    protected override bool SupportsInclude => false;

    /// <inheritdoc />
    protected override string TableSuffix(TableSchema table)
    {
        var options = table.Options;
        var suffix = "";
        if (options.TryGetValue("engine", out var engine)) suffix += $" ENGINE={engine}";
        if (options.TryGetValue("charset", out var charset)) suffix += $" DEFAULT CHARSET={charset}";
        if (options.TryGetValue("collation", out var collation)) suffix += $" COLLATE={collation}";
        if (options.TryGetValue("row_format", out var rowFormat)) suffix += $" ROW_FORMAT={rowFormat}";
        return suffix;
    }

    /// <inheritdoc />
    protected override string ImplicitDefault(ColumnSchema column) =>
        column.StoreType.Contains("text", StringComparison.Ordinal) || column.StoreType.Contains("blob", StringComparison.Ordinal)
            || column.StoreType == "json"
            ? "('')"     // TEXT/BLOB/JSON take only expression defaults
            : base.ImplicitDefault(column);

    /// <summary>
    /// One ALTER TABLE per table for the column and index changes (ADD/MODIFY/DROP COLUMN, DROP/ADD INDEX as clauses of a
    /// single statement): MySQL copies the table for most column changes, so separate statements would rebuild a large
    /// table once per column, and an index dropped after the copy would have been rebuilt for nothing. Index-only changes
    /// become one in-place pass. NULL backfills run first; renames and foreign keys keep their own statements, in plan order.
    /// </summary>
    public override IReadOnlyList<string> GenerateAll(IReadOnlyList<SchemaChange> changes)
    {
        List<string> statements = [];
        List<string> backfills = [];
        List<string> clauses = [];
        string? table = null;

        void Flush()
        {
            if (clauses.Count > 0)
            {
                statements.AddRange(backfills);
                statements.Add($"ALTER TABLE {table} {string.Join(", ", clauses)}");
            }
            backfills.Clear();
            clauses.Clear();
            table = null;
        }

        foreach (var change in changes)
        {
            var clause = change switch
            {
                AddColumnChange add => $"{AddColumnKeyword} {ColumnDefinition(ColumnToAdd(add.Column), null)}",
                AlterColumnChange alter => $"MODIFY COLUMN {ColumnDefinition(alter.To, alter.Desired)}",
                DropColumnChange drop => $"DROP COLUMN {Q(drop.Column)}",
                DropIndexChange dropIndex => $"DROP INDEX {Q(dropIndex.Name)}",
                CreateIndexChange create => $"ADD {(create.Index.Unique ? "UNIQUE " : "")}INDEX {Q(create.Index.Name)} ({IndexKey(create.Index)})",
                _ => null,
            };
            var target = Table(change.Table, change.Schema);
            if (clause is null || (table is not null && table != target))
                Flush();
            if (clause is null)
            {
                statements.AddRange(Generate(change));
                continue;
            }
            table = target;
            if (change is AlterColumnChange alterColumn)
                backfills.AddRange(BackfillNulls(alterColumn));
            clauses.Add(clause);
        }
        Flush();
        return statements;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> AlterColumn(AlterColumnChange change) =>
        [$"ALTER TABLE {Table(change.Table, change.Schema)} MODIFY COLUMN {ColumnDefinition(change.To, change.Desired)}"];

    /// <inheritdoc />
    protected override string DropIndex(DropIndexChange change) => $"DROP INDEX {Q(change.Name)} ON {Table(change.Table, change.Schema)}";

    /// <inheritdoc />
    protected override IReadOnlyList<string> DropForeignKey(DropForeignKeyChange change) =>
        [$"ALTER TABLE {Table(change.Table, change.Schema)} DROP FOREIGN KEY {Q(change.Name)}"];
}

/// <summary>Table options of MySQL tables: <c>[MySqlTable]</c> hints over the connection's defaults.</summary>
internal static class MySqlTableOptions
{
    public static IReadOnlyDictionary<string, string> For(EntityMap map, DataConnectionConfig config)
    {
        var options = config.MySql ?? new MySqlOptions();
        var hint = map.Hint<MySqlTableAttribute>();
        var result = new Dictionary<string, string>
        {
            ["engine"] = hint?.Engine ?? options.DefaultEngine,
            ["charset"] = hint?.Charset ?? options.CharacterSet,
            ["collation"] = hint?.Collation ?? options.Collation,
        };
        if (hint?.RowFormat is { } rowFormat) result["row_format"] = rowFormat;
        return result;
    }
}
