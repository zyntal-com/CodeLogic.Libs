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
