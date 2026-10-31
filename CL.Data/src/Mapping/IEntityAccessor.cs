using System.Data.Common;

namespace CL.Data.Mapping;

/// <summary>Receives column values from an accessor without boxing value types (for parameter binding).</summary>
public interface IColumnValueWriter
{
    /// <summary>Writes the value of <paramref name="column"/>.</summary>
    void Write<TValue>(ColumnMap column, TValue value);
}

/// <summary>
/// Reads and writes instances of <typeparamref name="T"/> for CL.Data. The source generator emits one per
/// <c>[Table]</c>/<c>[DataRow]</c> type with typed, reflection-free code; types the generator cannot see use
/// a reflection-based fallback.
/// </summary>
/// <typeparam name="T">The mapped type.</typeparam>
public interface IEntityAccessor<T>
{
    /// <summary>The type's mapping.</summary>
    EntityMap Map { get; }

    /// <summary>
    /// Builds an instance from the current row. <paramref name="ordinals"/>[i] is the reader ordinal of
    /// <c>Map.Columns[i]</c>, or -1 when the result has no such column (the property keeps its default).
    /// </summary>
    T Materialize(DbDataReader reader, ReadOnlySpan<int> ordinals);

    /// <summary>Writes the value of column <paramref name="column"/> (an index into <c>Map.Columns</c>) to <paramref name="writer"/>.</summary>
    void WriteColumn(T entity, int column, IColumnValueWriter writer);

    /// <summary>Gets the value of column <paramref name="column"/> as an object.</summary>
    object? GetValue(T entity, int column);

    /// <summary>Sets column <paramref name="column"/> (identity write-back, audit stamps, concurrency tokens).</summary>
    void SetValue<TValue>(T entity, int column, TValue value);

    /// <summary>The entity's primary-key value.</summary>
    EntityKey GetKey(T entity);

    /// <summary>The current column values of <paramref name="entity"/>, to find out later which columns changed.</summary>
    EntitySnapshot<T> Snapshot(T entity);
}
