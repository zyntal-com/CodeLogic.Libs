using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CL.Data.Mapping.Conversion;

namespace CL.Data.Mapping.Reflection;

/// <summary>Builds an <see cref="EntityMap"/> from a type's properties and attributes at runtime.</summary>
[RequiresUnreferencedCode("Reads the type's properties and attributes at runtime.")]
internal static class ReflectionMapBuilder
{
    private static readonly Type[] HintTypes =
    [
        typeof(MySqlColumnAttribute), typeof(PostgresColumnAttribute), typeof(SqlServerColumnAttribute), typeof(SqliteColumnAttribute),
    ];

    private static readonly Type[] TableHintTypes =
    [
        typeof(MySqlTableAttribute), typeof(PostgresTableAttribute), typeof(SqlServerTableAttribute), typeof(SqliteTableAttribute),
    ];

    /// <summary>Mapped properties: public instance, readable, settable (set or init) or constructor-bound, not <c>[Ignore]</c>.</summary>
    public static PropertyInfo[] MappedProperties(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<IgnoreAttribute>() is null)
            .ToArray();
        var ctorNames = BindingConstructor(type)?.GetParameters().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        return properties.Where(p => p.SetMethod is { IsPublic: true } || ctorNames.Contains(p.Name)).ToArray();
    }

    /// <summary>
    /// The constructor used to create instances: the public parameterless one when it exists, otherwise the
    /// public constructor with the most parameters (records with a primary constructor).
    /// </summary>
    public static ConstructorInfo? BindingConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        return ctors.FirstOrDefault(c => c.GetParameters().Length == 0)
               ?? ctors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
    }

    public static EntityMap Build(Type type)
    {
        var table = type.GetCustomAttribute<TableAttribute>();
        var nullability = new NullabilityInfoContext();
        var columns = MappedProperties(type).Select(p => BuildColumn(p, nullability)).ToArray();

        var composite = type.GetCustomAttributes<CompositeIndexAttribute>()
            .Select(a => new CompositeIndexDefinition(a.Name, a.Properties, a.Unique, a.Include));
        var tableHints = TableHintTypes.Select(t => type.GetCustomAttribute(t)).OfType<Attribute>();

        return EntityMap.Create(type, table, columns, composite, tableHints);
    }

    private static ColumnMap BuildColumn(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var column = property.GetCustomAttribute<ColumnAttribute>();
        var type = property.PropertyType;

        var nullable = Nullable.GetUnderlyingType(type) is not null ||
                       (!type.IsValueType && nullability.Create(property).ReadState != NullabilityState.NotNull);
        if (column?.NotNull == true) nullable = false;

        var roles = ColumnRoles.None;
        if (column?.Primary == true) roles |= ColumnRoles.Key;
        if (column?.AutoIncrement == true) roles |= ColumnRoles.Identity;
        if (property.IsDefined(typeof(SoftDeleteAttribute))) roles |= ColumnRoles.SoftDelete;
        if (property.IsDefined(typeof(ConcurrencyTokenAttribute))) roles |= ColumnRoles.ConcurrencyToken;
        if (property.IsDefined(typeof(CreatedAtAttribute))) roles |= ColumnRoles.CreatedAt;
        if (property.IsDefined(typeof(UpdatedAtAttribute))) roles |= ColumnRoles.UpdatedAt;
        if (property.IsDefined(typeof(CreatedByAttribute))) roles |= ColumnRoles.CreatedBy;
        if (property.IsDefined(typeof(UpdatedByAttribute))) roles |= ColumnRoles.UpdatedBy;
        if (property.IsDefined(typeof(TenantKeyAttribute))) roles |= ColumnRoles.TenantKey;
        var retain = property.GetCustomAttribute<RetainDaysAttribute>();
        if (retain is not null) roles |= ColumnRoles.Retention;

        ValueConverter? converter = null;
        if (column?.Converter is { } converterType)
        {
            converter = Activator.CreateInstance(converterType) as ValueConverter
                ?? throw new InvalidOperationException(
                    $"[Column(Converter = typeof({converterType.Name}))] on '{property.DeclaringType?.Name}.{property.Name}' is not a ValueConverter.");
        }

        var dataType = column?.DataType ?? DataType.Unspecified;
        if (dataType == DataType.Unspecified)
        {
            dataType = (converter ?? (ValueConverters.TryGet(type, out var registered) ? registered : null))?.StoreDataType
                       ?? DataTypeInference.Infer(type);
        }

        return new ColumnMap
        {
            PropertyName = property.Name,
            ClrType = type,
            ExplicitName = string.IsNullOrEmpty(column?.Name) ? null : column.Name,
            PreviousName = column?.PreviousName,
            IsNullable = nullable,
            DataType = dataType,
            Size = column?.Size ?? 0,
            Precision = column?.Precision ?? 0,
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
            ForeignKey = property.GetCustomAttribute<ForeignKeyAttribute>(),
            IndexAttributes = property.GetCustomAttributes<IndexAttribute>().ToArray(),
            Hints = HintTypes.Select(t => property.GetCustomAttribute(t)).OfType<Attribute>().ToArray(),
        };
    }
}
