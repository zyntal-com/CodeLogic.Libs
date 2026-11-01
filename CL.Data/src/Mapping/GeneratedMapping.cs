using System.ComponentModel;
using System.Data.Common;
using System.Runtime.CompilerServices;
using CL.Data.Mapping.Conversion;

namespace CL.Data.Mapping;

/// <summary>
/// Building blocks for mapping code: the source generator's accessors call these, and reflection mapping uses
/// the same <see cref="Column"/> / <see cref="Entity"/> logic, so both describe a type identically. Not meant
/// to be called by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedMapping
{
    /// <summary>
    /// The column of a property, from its attributes. <paramref name="nullable"/> is whether the property accepts
    /// null (a nullable value type, or a reference type not annotated as non-null).
    /// </summary>
    public static ColumnMap Column(string propertyName, Type clrType, bool nullable, IReadOnlyList<Attribute> attributes, ValueConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(attributes);

        ColumnAttribute? column = null;
        RetainDaysAttribute? retain = null;
        ForeignKeyAttribute? foreignKey = null;
        List<IndexAttribute> indexes = [];
        List<Attribute> hints = [];
        var roles = ColumnRoles.None;
        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                case ColumnAttribute c: column = c; break;
                case SoftDeleteAttribute: roles |= ColumnRoles.SoftDelete; break;
                case ConcurrencyTokenAttribute: roles |= ColumnRoles.ConcurrencyToken; break;
                case CreatedAtAttribute: roles |= ColumnRoles.CreatedAt; break;
                case UpdatedAtAttribute: roles |= ColumnRoles.UpdatedAt; break;
                case CreatedByAttribute: roles |= ColumnRoles.CreatedBy; break;
                case UpdatedByAttribute: roles |= ColumnRoles.UpdatedBy; break;
                case TenantKeyAttribute: roles |= ColumnRoles.TenantKey; break;
                case RetainDaysAttribute r: retain = r; roles |= ColumnRoles.Retention; break;
                case ForeignKeyAttribute f: foreignKey = f; break;
                case IndexAttribute i: indexes.Add(i); break;
                case MySqlColumnAttribute or PostgresColumnAttribute or SqlServerColumnAttribute or SqliteColumnAttribute: hints.Add(attribute); break;
            }
        }

        if (column?.NotNull == true) nullable = false;
        if (column?.Primary == true) roles |= ColumnRoles.Key;
        if (column?.AutoIncrement == true) roles |= ColumnRoles.Identity;

        var dataType = column?.DataType ?? DataType.Unspecified;
        if (dataType == DataType.Unspecified)
        {
            dataType = (converter ?? (ValueConverters.TryGet(clrType, out var registered) ? registered : null))?.StoreDataType
                       ?? DataTypeInference.Infer(clrType);
        }

        return new ColumnMap
        {
            PropertyName = propertyName,
            ClrType = clrType,
            ExplicitName = string.IsNullOrEmpty(column?.Name) ? null : column.Name,
            PreviousName = column?.PreviousName,
            IsNullable = nullable,
            DataType = dataType,
            Size = column?.Size ?? 0,
            Precision = column?.Precision ?? -1,
            Scale = column?.Scale ?? -1,
            KeyOrder = column?.KeyOrder ?? 0,
            Unique = column?.Unique ?? false,
            Indexed = column?.Index ?? false,
            DefaultSql = column?.DefaultSql,
            Comment = column?.Comment,
            NativeType = column?.NativeType,
            Roles = roles,
            RetainDays = retain?.Days ?? 0,
            RetentionBatchSize = retain?.BatchSize ?? 5000,
            ExplicitConverter = converter,
            ForeignKey = foreignKey,
            IndexAttributes = indexes,
            Hints = hints,
        };
    }

    /// <summary>The map of <paramref name="type"/> from its type-level attributes and its columns (in property order).</summary>
    public static EntityMap Entity(Type type, IReadOnlyList<Attribute> typeAttributes, IEnumerable<ColumnMap> columns)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(typeAttributes);
        TableAttribute? table = null;
        List<CompositeIndexDefinition> composites = [];
        List<Attribute> hints = [];
        foreach (var attribute in typeAttributes)
        {
            switch (attribute)
            {
                case TableAttribute t: table = t; break;
                case CompositeIndexAttribute c: composites.Add(new CompositeIndexDefinition(c.Name, c.Properties, c.Unique, c.Include)); break;
                case MySqlTableAttribute or PostgresTableAttribute or SqlServerTableAttribute or SqliteTableAttribute: hints.Add(attribute); break;
            }
        }
        return EntityMap.Create(type, table, columns, composites, hints);
    }

    /// <summary>Reads column <paramref name="ordinal"/> as <typeparamref name="T"/>, through the column's converter when it has one.</summary>
    public static T Read<T>(DbDataReader reader, int ordinal, ColumnMap column)
    {
        if (column.Converter is not { } converter) return ValueReader<T>.Read(reader, ordinal);
        if (reader.IsDBNull(ordinal)) return ValueReader<T>.Read(reader, ordinal);   // null handling and the not-nullable error
        return (T)converter.FromStoreObject(ValueConversion.FromDb(reader.GetValue(ordinal), converter.StoreType)!)!;
    }

    /// <summary>
    /// <paramref name="value"/> as the property type <typeparamref name="TProperty"/>: unchanged when it already
    /// is one, otherwise converted the way values read from the database are (generated returning/identity writes).
    /// </summary>
    public static TProperty Convert<TValue, TProperty>(TValue value, ColumnMap column)
    {
        if (typeof(TValue) == typeof(TProperty)) return Unsafe.As<TValue, TProperty>(ref value);
        object? boxed = value;
        if (boxed is null or TProperty) return (TProperty)boxed!;
        var converted = column.Converter is { } converter
            ? converter.FromStoreObject(ValueConversion.FromDb(boxed, converter.StoreType)!)
            : ValueConversion.FromDb(boxed, column.ValueType);
        return (TProperty)converted!;
    }

    /// <summary>Equality used by snapshots: byte arrays by content, everything else by <see cref="EqualityComparer{T}.Default"/>.</summary>
    public static bool Same<T>(T a, T b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : EqualityComparer<T>.Default.Equals(a, b);

    /// <summary>A snapshot copy of <paramref name="value"/>: byte arrays are cloned so changes made in place are seen.</summary>
    public static T Copy<T>(T value) => value is byte[] bytes ? (T)(object)bytes.ToArray() : value;

    /// <summary>The stored form of <paramref name="value"/> through the column's converter (snapshots compare converter columns this way).</summary>
    public static object? Store<T>(T value, ColumnMap column) =>
        value is null || column.Converter is not { } converter ? null : Copy(converter.ToStoreObject(value));

    /// <summary>Equality of two stored values (byte arrays by content).</summary>
    public static bool SameStore(object? a, object? b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    /// <summary>The exception generated accessors throw for a property they cannot set.</summary>
    public static NotSupportedException NotSettable(Type type, ColumnMap column) =>
        new($"'{type.Name}.{column.PropertyName}' has no public setter.");
}
