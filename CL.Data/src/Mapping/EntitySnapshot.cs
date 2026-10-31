namespace CL.Data.Mapping;

/// <summary>
/// The column values of an entity at one moment, for writing only what changed since. Take one with
/// <c>Repository&lt;T&gt;.Snapshot</c> (or let <c>UpdateAsync(entity, change)</c> take it); byte arrays are copied and
/// converter columns are kept in their stored form, so changes made in place are seen too.
/// </summary>
public abstract class EntitySnapshot<T>
{
    /// <summary>True when column <paramref name="column"/> (ordinal) of <paramref name="entity"/> differs from the snapshot.</summary>
    public abstract bool IsChanged(T entity, int column);

    /// <summary>The properties of <paramref name="entity"/> that differ from the snapshot, in column order.</summary>
    public IReadOnlyList<string> ChangedProperties(T entity, EntityMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        List<string>? changed = null;
        foreach (var column in map.Columns)
            if (IsChanged(entity, column.Ordinal)) (changed ??= []).Add(column.PropertyName);
        return changed ?? (IReadOnlyList<string>)[];
    }
}
