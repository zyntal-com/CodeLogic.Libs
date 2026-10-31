using System.Data.Common;
using System.Globalization;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace CL.Data.Schema;

/// <summary>
/// Reads tables from the database's catalog into <see cref="TableSchema"/>. Store types are normalized through
/// <see cref="Providers.DataProvider.NormalizeStoreType"/>, so they compare directly with <see cref="SchemaBuilder.FromMap"/>.
/// </summary>
public abstract class SchemaIntrospector
{
    /// <summary>The table, or null when it does not exist. <paramref name="schema"/> null means the connection's default.</summary>
    public abstract ValueTask<Result<TableSchema?>> ReadTableAsync(Database db, string table, string? schema = null, CancellationToken ct = default);

    /// <summary>The names of the tables in <paramref name="schema"/> (null: the connection's default), sorted.</summary>
    public abstract ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(Database db, string? schema = null, CancellationToken ct = default);

    /// <summary>The connection's default schema when <paramref name="schema"/> is null.</summary>
    protected static string? SchemaOf(Database db, string? schema) => schema ?? db.Provider.GetDefaultSchema(db.Config);

    /// <summary>A catalog value as a string (null for DBNull).</summary>
    protected static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A catalog value as a long (0 for DBNull); catalogs report integers and booleans in many CLR types.</summary>
    protected static long Number(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetValue(ordinal) switch
    {
        bool b => b ? 1 : 0,
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        var v => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    /// <summary>Parses a referential action as catalogs spell it (<c>CASCADE</c>, <c>SET_NULL</c>, <c>NO ACTION</c>, ...).</summary>
    protected static ForeignKeyAction ParseAction(string? text) => text?.Replace('_', ' ').Trim().ToUpperInvariant() switch
    {
        "CASCADE" => ForeignKeyAction.Cascade,
        "SET NULL" => ForeignKeyAction.SetNull,
        "SET DEFAULT" => ForeignKeyAction.SetDefault,
        "NO ACTION" => ForeignKeyAction.NoAction,
        _ => ForeignKeyAction.Restrict,
    };

    /// <summary>Groups ordered (name, column) catalog rows into indexes.</summary>
    protected static IReadOnlyList<IndexSchema> BuildIndexes(IEnumerable<IndexColumnRow> rows) =>
        rows.GroupBy(r => r.Index, StringComparer.Ordinal)
            .Select(g =>
            {
                var keys = g.Where(r => !r.Included).OrderBy(r => r.Ordinal).ToArray();
                var descending = keys.Any(k => k.Descending) ? keys.Select(k => k.Descending).ToArray() : [];
                return new IndexSchema(g.Key, g.First().Unique, keys.Select(k => k.Column).ToArray())
                {
                    Descending = descending,
                    Include = g.Where(r => r.Included).Select(r => r.Column).ToArray(),
                };
            })
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Groups ordered catalog rows into foreign keys.</summary>
    protected static IReadOnlyList<ForeignKeySchema> BuildForeignKeys(IEnumerable<ForeignKeyColumnRow> rows) =>
        rows.GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                var ordered = g.OrderBy(r => r.Ordinal).ToArray();
                var first = ordered[0];
                return new ForeignKeySchema(g.Key, ordered.Select(r => r.Column).ToArray(), first.ReferencedTable, ordered.Select(r => r.ReferencedColumn).ToArray())
                {
                    ReferencedSchema = first.ReferencedSchema,
                    OnDelete = first.OnDelete,
                    OnUpdate = first.OnUpdate,
                };
            })
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>One column of an index, as catalogs list them.</summary>
    protected readonly record struct IndexColumnRow(string Index, bool Unique, int Ordinal, string Column, bool Descending, bool Included);

    /// <summary>One column of a foreign key, as catalogs list them.</summary>
    protected readonly record struct ForeignKeyColumnRow(string Name, int Ordinal, string Column, string ReferencedTable, string? ReferencedSchema,
        string ReferencedColumn, ForeignKeyAction OnDelete, ForeignKeyAction OnUpdate);
}
