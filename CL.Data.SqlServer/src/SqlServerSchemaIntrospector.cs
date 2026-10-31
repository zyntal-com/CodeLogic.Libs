using System.Data.Common;
using System.Globalization;
using CL.Data.Execution;
using CL.Data.Schema;
using CodeLogic.Core.Results;

namespace CL.Data.SqlServer;

/// <summary>Reads SQL Server tables from the <c>sys.*</c> catalog views.</summary>
public sealed class SqlServerSchemaIntrospector : SchemaIntrospector
{
    /// <inheritdoc />
    public override async ValueTask<Result<TableSchema?>> ReadTableAsync(Database db, string table, string? schema = null, CancellationToken ct = default)
    {
        var owner = SchemaOf(db, schema) ?? "dbo";
        var qualified = Quote(owner) + "." + Quote(table);

        var columns = await db.SqlReadAsync(
            $"SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, OBJECT_DEFINITION(c.default_object_id) " +
            $"FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID({qualified}, 'U') ORDER BY c.column_id",
            r => new ColumnSchema(Text(r, 0)!, db.Provider.NormalizeStoreType(TypeOf(r)), Number(r, 5) != 0, Number(r, 6) != 0, Text(r, 7)), ct).ConfigureAwait(false);
        if (columns.IsFailure) return columns.Error!;
        if (columns.Value!.Count == 0) return (TableSchema?)null;

        var indexes = await db.SqlReadAsync(
            $"SELECT i.name, i.is_unique, i.is_primary_key, CASE WHEN ic.is_included_column = 1 THEN 1000 + ic.index_column_id ELSE ic.key_ordinal END, " +
            $"col.name, ic.is_descending_key, ic.is_included_column FROM sys.indexes i " +
            $"JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            $"JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id " +
            $"WHERE i.object_id = OBJECT_ID({qualified}, 'U') AND i.type > 0 AND i.is_hypothetical = 0",
            r => (Primary: Number(r, 2) != 0,
                Row: new IndexColumnRow(Text(r, 0)!, Number(r, 1) != 0, (int)Number(r, 3), Text(r, 4)!, Number(r, 5) != 0, Number(r, 6) != 0)), ct).ConfigureAwait(false);
        if (indexes.IsFailure) return indexes.Error!;

        var foreignKeys = await db.SqlReadAsync(
            $"SELECT fk.name, fkc.constraint_column_id, pc.name, rt.name, SCHEMA_NAME(rt.schema_id), rc.name, " +
            $"fk.delete_referential_action_desc, fk.update_referential_action_desc FROM sys.foreign_keys fk " +
            $"JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
            $"JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id " +
            $"JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id " +
            $"JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id " +
            $"WHERE fk.parent_object_id = OBJECT_ID({qualified}, 'U')",
            r => new ForeignKeyColumnRow(Text(r, 0)!, (int)Number(r, 1), Text(r, 2)!, Text(r, 3)!, Text(r, 4), Text(r, 5)!,
                ParseAction(Text(r, 6)), ParseAction(Text(r, 7))), ct).ConfigureAwait(false);
        if (foreignKeys.IsFailure) return foreignKeys.Error!;

        return new TableSchema(table, owner, columns.Value!,
            indexes.Value!.Where(i => i.Primary).OrderBy(i => i.Row.Ordinal).Select(i => i.Row.Column).ToArray())
        {
            Indexes = BuildIndexes(indexes.Value!.Where(i => !i.Primary).Select(i => i.Row)),
            ForeignKeys = BuildForeignKeys(foreignKeys.Value!),
        };
    }

    /// <inheritdoc />
    public override async ValueTask<Result<IReadOnlyList<string>>> ListTablesAsync(Database db, string? schema = null, CancellationToken ct = default)
    {
        var owner = SchemaOf(db, schema) ?? "dbo";
        var tables = await db.SqlQueryAsync<string>($"SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID({owner}) ORDER BY name", ct).ConfigureAwait(false);
        return tables.IsFailure ? tables.Error! : tables.Value!;
    }

    // The declared type from sys.types plus sys.columns sizes, written the way the type mapper writes it.
    private static string TypeOf(DbDataReader r)
    {
        var name = Text(r, 1)!.ToLowerInvariant();
        var maxLength = Number(r, 2);
        var precision = Number(r, 3);
        var scale = Number(r, 4);
        string Size(long length) => length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture);
        return name switch
        {
            "nvarchar" or "nchar" => $"{name}({(maxLength < 0 ? "max" : Size(maxLength / 2))})",
            "varchar" or "char" or "varbinary" or "binary" => $"{name}({Size(maxLength)})",
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{name}({precision},{scale})"),
            "datetime2" or "time" or "datetimeoffset" => string.Create(CultureInfo.InvariantCulture, $"{name}({scale})"),
            "timestamp" => "rowversion",
            _ => name,
        };
    }

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
