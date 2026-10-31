using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CL.Data.Generator;

internal enum SetterKind { None, Set, Init }

/// <summary>A mapped property as the generator and the analyzer see it.</summary>
internal sealed class ShapeProperty
{
    public IPropertySymbol Symbol = null!;
    public string Name = "";
    public ITypeSymbol Type = null!;
    public string TypeName = "";
    public bool Nullable;
    public SetterKind Setter;
    public bool Required;
    public List<AttributeData> Attributes = [];
    public INamedTypeSymbol? Converter;
    public int CtorParameter = -1;
    public string? CtorParameterType;

    public AttributeData? Attribute(string name) => Attributes.FirstOrDefault(a => a.AttributeClass?.Name == name || a.AttributeClass?.OriginalDefinition.Name == name);
    public bool Has(string name) => Attribute(name) is not null;
    public object? Named(string attribute, string argument) =>
        Attribute(attribute)?.NamedArguments.FirstOrDefault(n => n.Key == argument).Value.Value;
}

/// <summary>
/// How CL.Data maps a <c>[Table]</c>/<c>[DataRow]</c> type: the same rules reflection mapping applies (public
/// readable properties, settable or constructor-bound, minus <c>[Ignore]</c>; parameterless constructor first).
/// The generator emits code from it; the analyzer reports from it.
/// </summary>
internal sealed class EntityShape
{
    internal static readonly SymbolDisplayFormat Fqn = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private EntityShape(INamedTypeSymbol type) => Type = type;

    public INamedTypeSymbol Type { get; }

    /// <summary>Why generated mapping cannot serve this type (it falls back to reflection), or null.</summary>
    public string? Fallback { get; private set; }

    /// <summary>A problem reflection mapping would also fail on at runtime (the constructor cannot bind), or null.</summary>
    public string? ConstructorError { get; private set; }

    /// <summary>Properties whose converter is not usable (not a ValueConverter, or no public parameterless constructor).</summary>
    public List<(ShapeProperty Property, string Problem)> ConverterErrors { get; } = [];

    /// <summary>Mapped properties in column order.</summary>
    public List<ShapeProperty> Mapped { get; } = [];

    /// <summary>The binding constructor.</summary>
    public IMethodSymbol? Constructor { get; private set; }

    /// <summary>True when the constructor sets every required member itself.</summary>
    public bool SetsRequiredMembers { get; private set; }

    /// <summary>True when code can be generated.</summary>
    public bool CanGenerate => Fallback is null && ConstructorError is null && ConverterErrors.Count == 0;

    public bool IsTable => HasAttribute(Type, "CL.Data.TableAttribute");

    public static EntityShape Analyze(INamedTypeSymbol type)
    {
        var shape = new EntityShape(type);
        if (type.TypeKind == TypeKind.Struct) shape.Fallback = "it is a struct";
        else if (type.TypeKind != TypeKind.Class) shape.Fallback = "it is not a class";
        else if (type.IsAbstract) shape.Fallback = "it is abstract";
        else if (type.IsGenericType) shape.Fallback = "it is generic";
        else if (type.IsFileLocal) shape.Fallback = "it is file-local";
        else
        {
            for (var t = type; t is not null; t = t.ContainingType)
            {
                if (t.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal) continue;
                shape.Fallback = t.Equals(type, SymbolEqualityComparer.Default) ? "it is not public or internal" : $"its containing type '{t.Name}' is not public or internal";
                break;
            }
        }

        var properties = new List<ShapeProperty>();
        var seen = new HashSet<string>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member is not IPropertySymbol p || p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
                if (!seen.Add(p.Name)) continue;
                var attributes = p.GetAttributes();
                if (attributes.Any(a => a.AttributeClass?.ToDisplayString() == "CL.Data.IgnoreAttribute")) continue;
                if (p.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                {
                    shape.Fallback ??= $"property '{p.Name}' has no public getter (mark it [Ignore] or make the getter public)";
                    continue;
                }

                var propertyType = p.Type;
                var property = new ShapeProperty
                {
                    Symbol = p,
                    Name = p.Name,
                    Type = propertyType,
                    TypeName = propertyType.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(Fqn),
                    Nullable = propertyType.IsValueType
                        ? propertyType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                        : p.NullableAnnotation != NullableAnnotation.NotAnnotated,
                    Setter = p.SetMethod is { DeclaredAccessibility: Accessibility.Public } set ? set.IsInitOnly ? SetterKind.Init : SetterKind.Set : SetterKind.None,
                    Required = p.IsRequired,
                    Attributes = attributes.Where(a => a.AttributeClass?.ContainingNamespace?.ToDisplayString() == "CL.Data").ToList(),
                };

                if (property.Named("ColumnAttribute", "Converter") is INamedTypeSymbol converter)
                {
                    if (!DerivesFrom(converter, "CL.Data.Mapping.Conversion.ValueConverter"))
                        shape.ConverterErrors.Add((property, $"'{converter.Name}' is not a ValueConverter"));
                    else if (converter.IsAbstract || !converter.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                        shape.ConverterErrors.Add((property, $"'{converter.Name}' has no public parameterless constructor"));
                    else property.Converter = converter;
                }
                properties.Add(property);
            }
        }

        // Constructor: public parameterless when present, else the public one with the most parameters.
        var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
        var ctor = ctors.FirstOrDefault(c => c.Parameters.Length == 0) ?? ctors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault();
        shape.Constructor = ctor;
        if (ctor is null)
        {
            if (shape.Fallback is null && type.TypeKind == TypeKind.Class && !type.IsAbstract) shape.ConstructorError = "it has no public constructor";
        }
        else
        {
            shape.SetsRequiredMembers = ctor.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
            for (var i = 0; i < ctor.Parameters.Length; i++)
            {
                var parameter = ctor.Parameters[i];
                var match = properties.FirstOrDefault(p => string.Equals(p.Name, parameter.Name, System.StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    shape.ConstructorError = $"constructor parameter '{parameter.Name}' does not match a mapped property (add a parameterless constructor, or name the parameter after its property)";
                    break;
                }
                match.CtorParameter = i;
                match.CtorParameterType = parameter.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(Fqn);
            }
        }

        shape.Mapped.AddRange(properties.Where(p => p.Setter != SetterKind.None || p.CtorParameter >= 0));
        if (!shape.SetsRequiredMembers && shape.Fallback is null)
        {
            var unmapped = AllMembers(type).Where(m => m is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
                .FirstOrDefault(m => shape.Mapped.All(p => p.Name != m.Name));
            if (unmapped is not null) shape.Fallback = $"required member '{unmapped.Name}' is not a mapped property";
        }
        return shape;
    }

    internal static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    internal static bool DerivesFrom(INamedTypeSymbol type, string fullName)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == fullName) return true;
        return false;
    }

    private static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var member in t.GetMembers()) yield return member;
    }
}
