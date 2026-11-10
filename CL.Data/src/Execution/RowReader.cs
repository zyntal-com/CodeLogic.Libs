using System.Data.Common;
using CL.Data.Mapping;
using CL.Data.Mapping.Conversion;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>Reads result rows as scalars (first column) or mapped types (columns matched by name).</summary>
internal static class RowReader
{
    /// <summary>True when <typeparamref name="T"/> is read from the first column rather than mapped.</summary>
    public static bool IsScalar<T>() => ScalarCache<T>.Value || ValueConverters.TryGet(typeof(T), out _);

    /// <summary>Reads every row of the current result.</summary>
    /// <param name="reader">The open reader.</param>
    /// <param name="accessor">The entity accessor, or null to read a scalar from the first column.</param>
    /// <param name="policy">Naming policy for matching columns by name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="columnOrder">
    /// True when the result's columns are known to be the map's columns in order (see <see cref="Ordinals(DbDataReader, EntityMap, NamingPolicy, bool)"/>).
    /// </param>
    public static async ValueTask<List<T>> ReadAllAsync<T>(DbDataReader reader, IEntityAccessor<T>? accessor, NamingPolicy policy,
        CancellationToken ct, bool columnOrder = false)
    {
        List<T> rows = [];
        if (accessor is null)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ValueReader<T>.Read(reader, 0));
            return rows;
        }

        var ordinals = Ordinals(reader, accessor.Map, policy, columnOrder);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(accessor.Materialize(reader, ordinals));
        return rows;
    }

    /// <summary>Reads the first row of the current result, or default when there is none.</summary>
    public static async ValueTask<T?> ReadFirstAsync<T>(DbDataReader reader, IEntityAccessor<T>? accessor, NamingPolicy policy,
        CancellationToken ct, bool columnOrder = false)
    {
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return default;
        return accessor is null
            ? ValueReader<T>.Read(reader, 0)
            : accessor.Materialize(reader, Ordinals(reader, accessor.Map, policy, columnOrder));
    }

    /// <summary>
    /// The reader ordinal of each mapped column. With <paramref name="columnOrder"/> (CL.Data wrote the SELECT list itself: every
    /// mapped column, in order, and nothing rewrote the SQL) the ordinals are known and no column name is read. That matters:
    /// asking a driver for a column name can make it decode all of the column's metadata (MySqlConnector decodes six strings per
    /// column), on every query. A replayed cached result and a result with another column count are matched by name.
    /// </summary>
    public static int[] Ordinals(DbDataReader reader, EntityMap map, NamingPolicy policy, bool columnOrder) =>
        columnOrder && reader.FieldCount == map.Columns.Count && !CL.Data.Caching.BufferedResultSet.IsReplay(reader)
            ? map.ColumnOrderOrdinals
            : Ordinals(reader, map, policy);

    /// <summary>
    /// Reader ordinal of each mapped column (-1 when absent). Result columns match the database name under
    /// <paramref name="policy"/> or the property name, case-insensitively.
    /// </summary>
    public static int[] Ordinals(DbDataReader reader, EntityMap map, NamingPolicy policy)
    {
        var names = map.GetNames(policy);
        var ordinals = new int[map.Columns.Count];
        Array.Fill(ordinals, -1);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var column = names.ByColumn.GetValueOrDefault(name) ?? FindByProperty(map, name);
            if (column is not null && ordinals[column.Ordinal] < 0) ordinals[column.Ordinal] = i;
        }
        return ordinals;
    }

    /// <summary>The accessor for a mapped <typeparamref name="T"/>, or null for scalars.</summary>
    public static Result<IEntityAccessor<T>?> AccessorFor<T>()
    {
        if (IsScalar<T>()) return Result<IEntityAccessor<T>?>.Success(null);
        var accessor = EntityAccessorRegistry.Get<T>();
        return accessor.IsSuccess
            ? Result<IEntityAccessor<T>?>.Success(accessor.Value)
            : Result<IEntityAccessor<T>?>.Failure(accessor.Error!);
    }

    private static ColumnMap? FindByProperty(EntityMap map, string name)
    {
        foreach (var column in map.Columns)
            if (string.Equals(column.PropertyName, name, StringComparison.OrdinalIgnoreCase)) return column;
        return null;
    }

    private static class ScalarCache<T>
    {
        public static readonly bool Value = DataTypeInference.IsBuiltIn(typeof(T)) || typeof(T) == typeof(object);
    }
}
