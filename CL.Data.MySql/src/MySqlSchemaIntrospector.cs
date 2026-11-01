using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.MySql;

/// <summary>Reads MySQL tables from <c>information_schema</c> (the current database unless a schema is given).</summary>
public sealed class MySqlSchemaIntrospector : SchemaIntrospector
{
    /// <inheritdoc />
    public override async ValueTask<Result<TableSchema?>> ReadTableAsync(Database db, string table, string? schema = null, CancellationToken ct = default)
    {
        var database = await DatabaseOf(db, schema, ct).ConfigureAwait(false);
        if (database.IsFailure) return database.Error!;
        var name = database.Value!;

        var info = await db.SqlReadAsync(
            $"SELECT ENGINE, TABLE_COLLATION, TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = {name} AND TABLE_NAME = {table} AND TABLE_TYPE = 'BASE TABLE'",
            r => (Engine: Text(r, 0), Collation: Text(r, 1), Comment: Text(r, 2)), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (info.IsFailure) return info.Error!;
        if (info.Value!.Count == 0) return (TableSchema?)null;

        var columns = await db.SqlReadAsync(
            $"SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLUMN_COMMENT, COLLATION_NAME FROM information_schema.COLUMNS " +
            $"WHERE TABLE_SCHEMA = {name} AND TABLE_NAME = {table} ORDER BY ORDINAL_POSITION",
            r => new ColumnSchema(Text(r, 0)!, db.Provider.NormalizeStoreType(Text(r, 1)!), Text(r, 2) == "YES",
                (Text(r, 4) ?? "").Contains("auto_increment", StringComparison.OrdinalIgnoreCase), Text(r, 3))
            {
                Comment = Text(r, 5) is { Length: > 0 } comment ? comment : null,
                Collation = Text(r, 6),
            }, SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (columns.IsFailure) return columns.Error!;

        var statistics = await db.SqlReadAsync(
            $"SELECT INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME, COLLATION FROM information_schema.STATISTICS " +
            $"WHERE TABLE_SCHEMA = {name} AND TABLE_NAME = {table} AND COLUMN_NAME IS NOT NULL ORDER BY INDEX_NAME, SEQ_IN_INDEX",
            r => new IndexColumnRow(Text(r, 0)!, Number(r, 1) == 0, (int)Number(r, 2), Text(r, 3)!, Text(r, 4) == "D", false), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (statistics.IsFailure) return statistics.Error!;

        var foreignKeys = await db.SqlReadAsync(
            $"SELECT k.CONSTRAINT_NAME, k.ORDINAL_POSITION, k.COLUMN_NAME, k.REFERENCED_TABLE_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_COLUMN_NAME, " +
            $"r.DELETE_RULE, r.UPDATE_RULE FROM information_schema.KEY_COLUMN_USAGE k " +
            $"JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME " +
            $"WHERE k.TABLE_SCHEMA = {name} AND k.TABLE_NAME = {table} AND k.REFERENCED_TABLE_NAME IS NOT NULL",
            r => new ForeignKeyColumnRow(Text(r, 0)!, (int)Number(r, 1), Text(r, 2)!, Text(r, 3)!,
                string.Equals(Text(r, 4), name, StringComparison.OrdinalIgnoreCase) ? null : Text(r, 4),
                Text(r, 5)!, ParseAction(Text(r, 6)), ParseAction(Text(r, 7))), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (foreignKeys.IsFailure) return foreignKeys.Error!;

        var (engine, collation, tableComment) = info.Value[0];
        Dictionary<string, string> options = [];
        if (engine is not null) options["engine"] = engine;
        if (collation is not null)
        {
            options["collation"] = collation;
            options["charset"] = collation.Split('_')[0];
        }

        return new TableSchema(table, schema, columns.Value!,
            statistics.Value!.Where(s => s.Index == "PRIMARY").OrderBy(s => s.Ordinal).Select(s => s.Column).ToArray())
        {
            Indexes = BuildIndexes(statistics.Value!.Where(s => s.Index != "PRIMARY")),
            ForeignKeys = BuildForeignKeys(foreignKeys.Value!),
            Options = options,
            Comment = tableComment is { Length: > 0 } ? tableComment : null,
        };
    }

    /// <inheritdoc />
    public override async ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(Database db, string? schema = null, CancellationToken ct = default)
    {
        var database = await DatabaseOf(db, schema, ct).ConfigureAwait(false);
        if (database.IsFailure) return database.Error!;
        var tables = await db.SqlQueryAsync<string>(
            $"SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = {database.Value!} AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        return tables.IsFailure ? tables.Error! : tables.Value!;
    }

    private static async ValueTask<Result<string>> DatabaseOf(Database db, string? schema, CancellationToken ct)
    {
        if (schema is not null) return schema;
        var current = await db.SqlScalarAsync<string>($"SELECT DATABASE()", SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (current.IsFailure) return current.Error!;
        return current.Value is { } name ? name : CL.Data.Errors.DataErrors.Validation("The connection has no current database.");
    }
}
