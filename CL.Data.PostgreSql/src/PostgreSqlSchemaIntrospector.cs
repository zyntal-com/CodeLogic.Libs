using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.PostgreSql;

/// <summary>Reads PostgreSQL tables from <c>pg_catalog</c>; types come from <c>format_type</c>.</summary>
public sealed class PostgreSqlSchemaIntrospector : SchemaIntrospector
{
    /// <inheritdoc />
    public override async ValueTask<Result<TableSchema?>> ReadTableAsync(Database db, string table, string? schema = null, CancellationToken ct = default)
    {
        var ns = SchemaOf(db, schema) ?? "public";

        var columns = await db.SqlReadAsync(
            $"SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid), a.attidentity::text, " +
            $"col_description(c.oid, a.attnum) FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum " +
            $"WHERE n.nspname = {ns} AND c.relname = {table} AND c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum",
            r =>
            {
                var defaultSql = Text(r, 3);
                var identity = Text(r, 4) is "a" or "d" || (defaultSql?.StartsWith("nextval(", StringComparison.Ordinal) ?? false);
                return new ColumnSchema(Text(r, 0)!, db.Provider.NormalizeStoreType(Text(r, 1)!), Number(r, 2) != 0, identity,
                    identity ? null : defaultSql) { Comment = Text(r, 5) };
            }, SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (columns.IsFailure) return columns.Error!;
        if (columns.Value!.Count == 0) return (TableSchema?)null;

        var indexes = await db.SqlReadAsync(
            $"SELECT i.relname, ix.indisunique, ix.indisprimary, k.ord, a.attname, COALESCE(ix.indoption[(k.ord - 1)::int] & 1, 0), k.ord > ix.indnkeyatts " +
            $"FROM pg_index ix JOIN pg_class t ON t.oid = ix.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace " +
            $"JOIN pg_class i ON i.oid = ix.indexrelid " +
            $"CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " +
            $"JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum " +
            $"WHERE n.nspname = {ns} AND t.relname = {table} ORDER BY i.relname, k.ord",
            r => (Primary: Number(r, 2) != 0,
                Row: new IndexColumnRow(Text(r, 0)!, Number(r, 1) != 0, (int)Number(r, 3), Text(r, 4)!, Number(r, 5) != 0, Number(r, 6) != 0)), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (indexes.IsFailure) return indexes.Error!;

        var foreignKeys = await db.SqlReadAsync(
            $"SELECT con.conname, k.ord, a.attname, rt.relname, rn.nspname, ra.attname, con.confdeltype::text, con.confupdtype::text " +
            $"FROM pg_constraint con JOIN pg_class t ON t.oid = con.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace " +
            $"JOIN pg_class rt ON rt.oid = con.confrelid JOIN pg_namespace rn ON rn.oid = rt.relnamespace " +
            $"CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(col, refcol, ord) " +
            $"JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.col " +
            $"JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.refcol " +
            $"WHERE con.contype = 'f' AND n.nspname = {ns} AND t.relname = {table}",
            r => new ForeignKeyColumnRow(Text(r, 0)!, (int)Number(r, 1), Text(r, 2)!, Text(r, 3)!, Text(r, 4), Text(r, 5)!,
                Action(Text(r, 6)), Action(Text(r, 7))), SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (foreignKeys.IsFailure) return foreignKeys.Error!;

        var comment = await db.SqlScalarAsync<string>(
            $"SELECT obj_description(c.oid, 'pg_class') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = {ns} AND c.relname = {table}",
            SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        if (comment.IsFailure) return comment.Error!;

        return new TableSchema(table, ns, columns.Value!,
            indexes.Value!.Where(i => i.Primary).OrderBy(i => i.Row.Ordinal).Select(i => i.Row.Column).ToArray())
        {
            Indexes = BuildIndexes(indexes.Value!.Where(i => !i.Primary).Select(i => i.Row)),
            ForeignKeys = BuildForeignKeys(foreignKeys.Value!),
            Comment = comment.Value,
        };
    }

    /// <inheritdoc />
    public override async ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(Database db, string? schema = null, CancellationToken ct = default)
    {
        var ns = SchemaOf(db, schema) ?? "public";
        var tables = await db.SqlQueryAsync<string>($"SELECT tablename::text FROM pg_tables WHERE schemaname = {ns} ORDER BY tablename", SqlIntent.ReadPrimary, ct).ConfigureAwait(false);
        return tables.IsFailure ? tables.Error! : tables.Value!;
    }

    // pg_constraint action codes.
    private static ForeignKeyAction Action(string? code) => code switch
    {
        "c" => ForeignKeyAction.Cascade,
        "n" => ForeignKeyAction.SetNull,
        "d" => ForeignKeyAction.SetDefault,
        "a" => ForeignKeyAction.NoAction,
        _ => ForeignKeyAction.Restrict,
    };
}
