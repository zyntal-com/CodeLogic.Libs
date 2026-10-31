using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CL.Data.Mapping.Conversion;

namespace CL.Data.Mapping.Reflection;

/// <summary>Builds an <see cref="EntityMap"/> from a type's properties and attributes at runtime.</summary>
[RequiresUnreferencedCode("Reads the type's properties and attributes at runtime.")]
internal static class ReflectionMapBuilder
{
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
        var nullability = new NullabilityInfoContext();
        var columns = MappedProperties(type).Select(p => BuildColumn(p, nullability)).ToArray();
        return GeneratedMapping.Entity(type, Attribute.GetCustomAttributes(type, inherit: true), columns);
    }

    /// <summary>A property's column through <see cref="GeneratedMapping.Column"/>, the same logic generated mapping uses.</summary>
    private static ColumnMap BuildColumn(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var type = property.PropertyType;
        var nullable = Nullable.GetUnderlyingType(type) is not null ||
                       (!type.IsValueType && nullability.Create(property).ReadState != NullabilityState.NotNull);
        var attributes = Attribute.GetCustomAttributes(property, inherit: true);

        ValueConverter? converter = null;
        if (attributes.OfType<ColumnAttribute>().FirstOrDefault()?.Converter is { } converterType)
        {
            converter = Activator.CreateInstance(converterType) as ValueConverter
                ?? throw new InvalidOperationException(
                    $"[Column(Converter = typeof({converterType.Name}))] on '{property.DeclaringType?.Name}.{property.Name}' is not a ValueConverter.");
        }
        return GeneratedMapping.Column(property.Name, type, nullable, attributes, converter);
    }
}
