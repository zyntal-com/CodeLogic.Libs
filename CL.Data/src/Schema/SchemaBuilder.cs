using System.Text;
using CL.Common.Hashing;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Providers;
using CodeLogic.Core.Results;

namespace CL.Data.Schema;

/// <summary>Builds the desired <see cref="TableSchema"/> of an entity for a provider and connection.</summary>
public static class SchemaBuilder
{
    /// <summary>The table <paramref name="map"/> should have on <paramref name="provider"/>.</summary>
    public static Result<TableSchema> FromMap(EntityMap map, DataProvider provider, DataConnectionConfig config)
    {
        if (!map.IsTable) return DataErrors.Unsupported($"'{map.ClrType.Name}' is a [DataRow] type and has no table.");
        var names = map.GetNames(config.NamingPolicy);
        var schema = provider.Capabilities.Schemas ? map.Schema ?? provider.GetDefaultSchema(config) : null;
        var maxLength = provider.Capabilities.MaxIdentifierLength;

        var columns = map.Columns.Select(c => new ColumnSchema(
            names.Of(c),
            provider.NormalizeStoreType(provider.Types.GetStoreType(c, config)),
            c.IsNullable,
            c.Has(ColumnRoles.Identity),
            c.DefaultSql)
        {
            PreviousName = c.PreviousName,
            Comment = c.Comment,
            Json = c.DataType == DataType.Json,
            Collation = provider.GetColumnCollation(c),
        }).ToArray();

        var indexes = map.Indexes.Select(i => new IndexSchema(
                i.Name ?? Identifier($"{(i.Unique ? "ux" : "ix")}_{names.Table}_{string.Join("_", i.Columns.Select(c => names.Of(c.Column)))}", maxLength),
                i.Unique,
                i.Columns.Select(c => names.Of(c.Column)).ToArray())
            {
                Descending = i.Columns.Any(c => c.Direction == SortOrder.Descending) ? i.Columns.Select(c => c.Direction == SortOrder.Descending).ToArray() : [],
                Include = i.Include.Select(names.Of).ToArray(),
            })
            .ToArray();

        List<ForeignKeySchema> foreignKeys = [];
        foreach (var fk in map.ForeignKeys)
        {
            string table;
            string column;
            string? refSchema = null;
            if (fk.ReferencedEntity is { } entity)
            {
                var referenced = EntityAccessorRegistry.FindMap(entity);
                if (referenced is null)
                    return DataErrors.Validation(
                        $"Foreign key {map.ClrType.Name}.{fk.Column.PropertyName} references {entity.Name}, which is not mapped yet; sync {entity.Name} first.");
                var refNames = referenced.GetNames(config.NamingPolicy);
                table = refNames.Table;
                column = referenced.FindProperty(fk.ReferencedColumn) is { } refColumn ? refNames.Of(refColumn) : fk.ReferencedColumn;
                refSchema = provider.Capabilities.Schemas ? referenced.Schema ?? provider.GetDefaultSchema(config) : null;
            }
            else
            {
                table = fk.ReferencedTable!;
                column = fk.ReferencedColumn;
                refSchema = provider.Capabilities.Schemas ? provider.GetDefaultSchema(config) : null;
            }
            var name = fk.ConstraintName ?? Identifier($"fk_{names.Table}_{names.Of(fk.Column)}", maxLength);
            foreignKeys.Add(new ForeignKeySchema(name, [names.Of(fk.Column)], table, [column])
            {
                ReferencedSchema = refSchema,
                OnDelete = fk.OnDelete,
                OnUpdate = fk.OnUpdate,
            });
        }

        return new TableSchema(names.Table, schema, columns, map.Key.Columns.Select(names.Of).ToArray())
        {
            Indexes = indexes,
            ForeignKeys = foreignKeys,
            Options = provider.TableOptions(map, config),
            Comment = map.Comment,
        };
    }

    /// <summary>
    /// <paramref name="name"/> cut to <paramref name="maxLength"/>; a cut name ends in a short hash of the full name so
    /// different long names stay different.
    /// </summary>
    public static string Identifier(string name, int maxLength)
    {
        if (name.Length <= maxLength) return name;
        var hash = FastHash.ToHex(FastHash.XxHash128(name.AsSpan()))[..8];
        return name[..(maxLength - 9)] + "_" + hash;
    }

    /// <summary>
    /// A stable fingerprint of <paramref name="table"/> (columns, keys, indexes, foreign keys, options). Table sync stores
    /// it and skips tables whose model has not changed since the last sync.
    /// </summary>
    public static string Fingerprint(TableSchema table)
    {
        var sb = new StringBuilder();
        sb.Append(table.Schema).Append('.').Append(table.Name).Append('|');
        foreach (var c in table.Columns)
            sb.Append(c.Name).Append(':').Append(c.StoreType).Append(c.Nullable ? "?" : "!").Append(c.Identity ? "+" : "").Append('=').Append(c.DefaultSql).Append(';');
        sb.Append("|pk:").AppendJoin(',', table.PrimaryKey);
        foreach (var i in table.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal))
            sb.Append("|ix:").Append(i.Name).Append(i.Unique ? "!" : "").AppendJoin(',', i.Columns).Append('/').AppendJoin(',', i.Descending).Append('/').AppendJoin(',', i.Include);
        foreach (var f in table.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append("|fk:").Append(f.Name).AppendJoin(',', f.Columns).Append('>').Append(f.ReferencedTable).AppendJoin(',', f.ReferencedColumns).Append(f.OnDelete).Append(f.OnUpdate);
        foreach (var (key, value) in table.Options.OrderBy(o => o.Key, StringComparer.Ordinal))
            sb.Append("|opt:").Append(key).Append('=').Append(value);
        return FastHash.ToHex(FastHash.XxHash128(sb.ToString().AsSpan()));
    }
}
