using System.Data.Common;
using CL.Data.Mapping;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>Reads result rows as scalars (first column) or mapped types (columns matched by name).</summary>
internal static class RowReader
{
    /// <summary>True when <typeparamref name="T"/> is read from the first column rather than mapped.</summary>
    public static bool IsScalar<T>() => ScalarCache<T>.Value;

    /// <summary>Reads every row of the current result.</summary>
    public static async ValueTask<List<T>> ReadAllAsync<T>(DbDataReader reader, IEntityAccessor<T>? accessor, NamingPolicy policy,
        CancellationToken ct)
    {
        List<T> rows = [];
        if (accessor is null)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ValueReader<T>.Read(reader, 0));
            return rows;
        }

        var ordinals = Ordinals(reader, accessor.Map, policy);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(accessor.Materialize(reader, ordinals));
        return rows;
    }

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
