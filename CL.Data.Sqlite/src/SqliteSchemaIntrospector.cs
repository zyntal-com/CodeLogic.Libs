using System.Text.RegularExpressions;
using CL.Data.Execution;
using CL.Data.Schema;
using CodeLogic.Core.Results;

namespace CL.Data.Sqlite;

/// <summary>
/// Reads SQLite tables through the <c>pragma_*</c> table-valued functions. SQLite does not report foreign-key
/// names, so they are read from the table's CREATE statement (or generated when the key is unnamed).
/// </summary>
public sealed partial class SqliteSchemaIntrospector : SchemaIntrospector
{
    /// <inheritdoc />
    public override async ValueTask<Result<TableSchema?>> ReadTableAsync(Database db, string table, string? schema = null, CancellationToken ct = default)
    {
        var create = await db.SqlScalarAsync<string>($"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = {table}", ct).ConfigureAwait(false);
        if (create.IsFailure) return create.Error!;
        if (create.Value is not { } sql) return (TableSchema?)null;

        var columns = await db.SqlReadAsync($"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info({table}) ORDER BY cid",
            r => (Name: Text(r, 0)!, Type: Text(r, 1) ?? "", NotNull: Number(r, 2) != 0, Default: Text(r, 3), Pk: (int)Number(r, 4)), ct).ConfigureAwait(false);
        if (columns.IsFailure) return columns.Error!;

        var indexes = await db.SqlReadAsync(
            $"SELECT il.name, il.\"unique\", ii.seqno, ii.name, ii.\"desc\" FROM pragma_index_list({table}) il " +
            $"JOIN pragma_index_xinfo(il.name) ii WHERE il.origin <> 'pk' AND ii.\"key\" = 1 AND ii.cid >= 0",
            r => new IndexColumnRow(Text(r, 0)!, Number(r, 1) != 0, (int)Number(r, 2), Text(r, 3)!, Number(r, 4) != 0, false), ct).ConfigureAwait(false);
        if (indexes.IsFailure) return indexes.Error!;

        var names = ForeignKeyNames(sql);
        var foreignKeys = await db.SqlReadAsync(
            $"SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete FROM pragma_foreign_key_list({table})",
            r =>
            {
                var from = Text(r, 3)!;
                return (Id: Number(r, 0), Row: new ForeignKeyColumnRow("", (int)Number(r, 1), from, Text(r, 2)!, null, Text(r, 4) ?? from,
                    ParseAction(Text(r, 6)), ParseAction(Text(r, 5))));
            }, ct).ConfigureAwait(false);
        if (foreignKeys.IsFailure) return foreignKeys.Error!;
        var fkRows = foreignKeys.Value!
            .GroupBy(f => f.Id)
            .SelectMany(g =>
            {
                var first = g.OrderBy(f => f.Row.Ordinal).First().Row;
                var name = names.GetValueOrDefault(first.Column) ?? $"fk_{table}_{first.Column}";
                return g.Select(f => f.Row with { Name = name });
            });

        var pk = columns.Value!.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Select(c => c.Name).ToArray();
        var autoincrement = AutoIncrement().IsMatch(sql);
        var provider = db.Provider;
        var result = new TableSchema(table, null,
            columns.Value!.Select(c => new ColumnSchema(c.Name, provider.NormalizeStoreType(c.Type),
                !c.NotNull && c.Pk == 0,
                autoincrement && pk.Length == 1 && c.Pk == 1 && c.Type.Equals("integer", StringComparison.OrdinalIgnoreCase),
                c.Default)).ToArray(),
            pk)
        {
            Indexes = BuildIndexes(indexes.Value!),
            ForeignKeys = BuildForeignKeys(fkRows),
            Options = Options(sql),
        };
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(Database db, string? schema = null, CancellationToken ct = default)
    {
        var tables = await db.SqlQueryAsync<string>(
            $"SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name", ct).ConfigureAwait(false);
        return tables.IsFailure ? tables.Error! : tables.Value!;
    }

    private static Dictionary<string, string> ForeignKeyNames(string sql)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in NamedForeignKey().Matches(sql))
        {
            var firstColumn = m.Groups["cols"].Value.Split(',')[0].Trim().Trim('"', '`', '[', ']');
            names.TryAdd(firstColumn, m.Groups["name"].Value);
        }
        return names;
    }

    private static Dictionary<string, string> Options(string sql)
    {
        var tail = sql[(sql.LastIndexOf(')') + 1)..];
        Dictionary<string, string> options = [];
        if (tail.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)) options["without_rowid"] = "true";
        if (tail.Contains("STRICT", StringComparison.OrdinalIgnoreCase)) options["strict"] = "true";
        return options;
    }

    [GeneratedRegex(@"\bAUTOINCREMENT\b", RegexOptions.IgnoreCase)]
    private static partial Regex AutoIncrement();

    [GeneratedRegex(@"CONSTRAINT\s+[""`\[]?(?<name>[^""`\]\s]+)[""`\]]?\s+FOREIGN\s+KEY\s*\((?<cols>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex NamedForeignKey();
}
