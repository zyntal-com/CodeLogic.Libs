using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CL.Data.Generator;

/// <summary>
/// Reports mapping mistakes in <c>[Table]</c>/<c>[DataRow]</c> types at build time: the problems
/// <c>EntityMap.Create</c> would otherwise throw at runtime, and types that cannot use generated mapping.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EntityMappingAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "CL.Data.Mapping";

    internal static readonly DiagnosticDescriptor NoGeneratedMapping = new("CLD001",
        "Type cannot use generated mapping", "'{0}' cannot use generated mapping because {1}; it is mapped by reflection, which Native AOT and trimmed apps do not support",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor ConstructorCannotBind = new("CLD002",
        "Constructor cannot be bound", "'{0}' cannot be created when reading rows: {1}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadConverter = new("CLD003",
        "Invalid column converter", "Converter of '{0}' is invalid: {1}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor NullableKey = new("CLD004",
        "Key column cannot be nullable", "Key column '{0}' cannot be nullable", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor CompositeKeyOrder = new("CLD005",
        "Composite key needs KeyOrder", "Composite key of '{0}' {1}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor RoleType = new("CLD006",
        "Column type does not fit its role", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateRole = new("CLD007",
        "Role used more than once", "'{0}' has more than one {1} column ({2})", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnknownProperty = new("CLD008",
        "Unknown property in index", "'{0}' in {1} is not a mapped property", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadName = new("CLD009",
        "Invalid or duplicate column name", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor SetNullOnRequired = new("CLD010",
        "SET NULL on a non-nullable column", "Foreign key on '{0}' uses SET NULL but the column is not nullable", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor NoColumns = new("CLD011",
        "Table maps no columns", "'{0}' maps no columns", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        NoGeneratedMapping, ConstructorCannotBind, BadConverter, NullableKey, CompositeKeyOrder, RoleType, DuplicateRole,
        UnknownProperty, BadName, SetNullOnRequired, NoColumns,
    ];

    private static readonly (string Attribute, string Role)[] SingleRoles =
    [
        ("SoftDeleteAttribute", "SoftDelete"), ("ConcurrencyTokenAttribute", "ConcurrencyToken"), ("CreatedAtAttribute", "CreatedAt"),
        ("UpdatedAtAttribute", "UpdatedAt"), ("CreatedByAttribute", "CreatedBy"), ("UpdatedByAttribute", "UpdatedBy"),
        ("TenantKeyAttribute", "TenantKey"), ("RetainDaysAttribute", "Retention"),
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!EntityShape.HasAttribute(type, DataGenerator.TableAttribute) && !EntityShape.HasAttribute(type, DataGenerator.DataRowAttribute)) return;
        var shape = EntityShape.Analyze(type);
        var typeLocation = type.Locations.FirstOrDefault();

        if (shape.Fallback is { } reason) context.ReportDiagnostic(Diagnostic.Create(NoGeneratedMapping, typeLocation, type.Name, reason));
        if (shape.ConstructorError is { } ctorError) context.ReportDiagnostic(Diagnostic.Create(ConstructorCannotBind, typeLocation, type.Name, ctorError));
        foreach (var (property, problem) in shape.ConverterErrors)
            context.ReportDiagnostic(Diagnostic.Create(BadConverter, Where(property, "ColumnAttribute"), property.Name, problem));

        var columns = shape.Mapped;
        if (shape.IsTable && columns.Count == 0) context.ReportDiagnostic(Diagnostic.Create(NoColumns, typeLocation, type.Name));

        // Keys.
        var keys = columns.Where(c => c.Named("ColumnAttribute", "Primary") is true).ToList();
        foreach (var key in keys.Where(k => IsNullable(k)))
            context.ReportDiagnostic(Diagnostic.Create(NullableKey, Where(key, "ColumnAttribute"), key.Name));
        if (keys.Count > 1)
        {
            var orders = keys.Select(k => k.Named("ColumnAttribute", "KeyOrder") as int? ?? 0).ToList();
            if (orders.Any(o => o <= 0))
                context.ReportDiagnostic(Diagnostic.Create(CompositeKeyOrder, typeLocation, type.Name, $"needs KeyOrder on every key column ({string.Join(", ", keys.Select(k => k.Name))})"));
            else if (orders.Distinct().Count() != orders.Count)
                context.ReportDiagnostic(Diagnostic.Create(CompositeKeyOrder, typeLocation, type.Name, "repeats a KeyOrder"));
        }

        // Roles.
        foreach (var column in columns.Where(c => c.Named("ColumnAttribute", "AutoIncrement") is true && !IsInteger(ValueType(c))))
            context.ReportDiagnostic(Diagnostic.Create(RoleType, Where(column, "ColumnAttribute"), $"AutoIncrement column '{column.Name}' must be an integer type"));
        foreach (var (attribute, role) in SingleRoles)
        {
            var withRole = columns.Where(c => c.Has(attribute)).ToList();
            if (withRole.Count > 1)
                context.ReportDiagnostic(Diagnostic.Create(DuplicateRole, typeLocation, type.Name, role, string.Join(", ", withRole.Select(c => c.Name))));
            foreach (var column in withRole)
                if (RoleProblem(role, column) is { } problem)
                    context.ReportDiagnostic(Diagnostic.Create(RoleType, Where(column, attribute), problem));
        }

        // Names: explicit column names unique (case-insensitive) and safe.
        var named = columns.Select(c => (Column: c, Name: c.Named("ColumnAttribute", "Name") as string)).Where(x => !string.IsNullOrEmpty(x.Name)).ToList();
        foreach (var group in named.GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            context.ReportDiagnostic(Diagnostic.Create(BadName, typeLocation, $"Column name '{group.Key}' is used by {string.Join(", ", group.Select(x => x.Column.Name))}"));
        var tableName = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DataGenerator.TableAttribute)?
            .NamedArguments.FirstOrDefault(n => n.Key == "Name").Value.Value as string;
        foreach (var name in named.Select(x => x.Name!).Append(tableName).Where(n => n is not null))
            if (!IsSafeIdentifier(name!))
                context.ReportDiagnostic(Diagnostic.Create(BadName, typeLocation, $"Name '{name}' is empty or contains a quote, NUL or line break"));

        // Index and composite-index property lists.
        bool Known(string n) => columns.Any(c => c.Name == n) || named.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
        foreach (var column in columns)
            foreach (var index in column.Attributes.Where(a => a.AttributeClass?.Name == "IndexAttribute"))
                foreach (var include in Strings(index.NamedArguments.FirstOrDefault(n => n.Key == "Include").Value))
                    if (!Known(include))
                        context.ReportDiagnostic(Diagnostic.Create(UnknownProperty, Location(index) ?? typeLocation, include, $"index on {column.Name}"));
        foreach (var composite in type.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == "CL.Data.CompositeIndexAttribute"))
        {
            var name = composite.ConstructorArguments.FirstOrDefault().Value as string;
            var properties = composite.ConstructorArguments.Skip(1).SelectMany(Strings);
            var include = Strings(composite.NamedArguments.FirstOrDefault(n => n.Key == "Include").Value);
            foreach (var property in properties.Concat(include).Where(p => !Known(p)))
                context.ReportDiagnostic(Diagnostic.Create(UnknownProperty, Location(composite) ?? typeLocation, property, $"composite index '{name}'"));
        }

        // Foreign keys with SET NULL need a nullable column.
        foreach (var column in columns)
        {
            var fk = column.Attributes.FirstOrDefault(a => a.AttributeClass?.Name == "ForeignKeyAttribute");
            if (fk is null || IsNullable(column)) continue;
            var setNull = fk.NamedArguments.Any(n => n.Key is "OnDelete" or "OnUpdate" && EnumMemberName(n.Value) == "SetNull");
            if (setNull) context.ReportDiagnostic(Diagnostic.Create(SetNullOnRequired, Location(fk) ?? typeLocation, column.Name));
        }
    }

    /// <summary>The name of the enum member an enum-typed attribute argument holds.</summary>
    private static string? EnumMemberName(TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Enum && constant.Type is INamedTypeSymbol enumType
            ? enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, constant.Value))?.Name
            : null;

    private static bool IsNullable(ShapeProperty column) => column.Named("ColumnAttribute", "NotNull") is not true && column.Nullable;

    private static ITypeSymbol ValueType(ShapeProperty column) =>
        column.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : column.Type;

    private static bool IsInteger(ITypeSymbol type) => type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
        or SpecialType.System_Byte or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_UInt16 or SpecialType.System_SByte;

    private static bool IsTime(ITypeSymbol type) => type.SpecialType == SpecialType.System_DateTime || type.ToDisplayString() == "System.DateTimeOffset";

    private static string? RoleProblem(string role, ShapeProperty column)
    {
        var type = ValueType(column);
        return role switch
        {
            "SoftDelete" when !IsTime(type) || !IsNullable(column) => $"[SoftDelete] column '{column.Name}' must be DateTimeOffset? or DateTime?",
            "CreatedAt" or "UpdatedAt" or "Retention" when !IsTime(type) => $"{role} column '{column.Name}' must be DateTimeOffset or DateTime",
            "ConcurrencyToken" when type.SpecialType is not (SpecialType.System_Int32 or SpecialType.System_Int64)
                                    && !(type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) =>
                $"[ConcurrencyToken] column '{column.Name}' must be int, long or byte[]",
            "Retention" when (column.Attribute("RetainDaysAttribute")?.ConstructorArguments.FirstOrDefault().Value as int? ?? 0) <= 0 =>
                $"[RetainDays] on '{column.Name}' must keep at least one day",
            _ => null,
        };
    }

    private static bool IsSafeIdentifier(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(['`', '"', '[', ']', '\0', '\r', '\n']) < 0;

    private static IEnumerable<string> Strings(TypedConstant constant) =>
        constant.Kind == TypedConstantKind.Array ? constant.Values.Select(v => v.Value as string).OfType<string>() :
        constant.Value is string s ? [s] : [];

    private static Location? Location(AttributeData attribute) => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();

    private static Location? Where(ShapeProperty property, string attribute) =>
        (property.Attribute(attribute) is { } a ? Location(a) : null) ?? property.Symbol.Locations.FirstOrDefault();
}
