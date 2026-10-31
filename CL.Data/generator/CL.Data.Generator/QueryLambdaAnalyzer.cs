using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CL.Data.Generator;

/// <summary>
/// Reports query lambdas CL.Data cannot translate to SQL, at build time instead of as <c>data.translation_failed</c> at
/// runtime. CL.Data never evaluates row values on the client, so a method or member it does not know fails the query.
/// Checks the predicate and ordering lambdas of <c>QueryBuilder</c>; parts that do not read the row are computed before
/// the query and are always fine. Mirrors <c>SqlTranslator</c>'s supported surface.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueryLambdaAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "CL.Data.Query";

    internal static readonly DiagnosticDescriptor UnsupportedMethod = new("CLD101",
        "Method cannot be translated to SQL", "'{0}' cannot be translated to SQL: {1}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnsupportedMember = new("CLD102",
        "Member cannot be translated to SQL", "'{0}' cannot be translated to SQL: only mapped columns, string.Length, date parts and Nullable Value/HasValue are supported",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor UnmappedProperty = new("CLD103",
        "Property is not a mapped column", "'{0}.{1}' is not a mapped column and cannot be used in a query", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [UnsupportedMethod, UnsupportedMember, UnmappedProperty];

    private static readonly HashSet<string> CheckedMethods =
    [
        "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "FirstOrDefaultAsync", "AnyAsync", "WhereExists", "WhereNotExists",
    ];

    private static readonly HashSet<string> DateParts = ["Year", "Month", "Day", "Hour", "Minute", "Second", "DayOfWeek", "Date"];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (!CheckedMethods.Contains(method.Name)) return;
        if (method.ContainingType?.OriginalDefinition.ToDisplayString() != "CL.Data.Query.QueryBuilder<T>") return;

        foreach (var argument in invocation.Arguments)
        {
            if (Lambda(argument.Value) is not { } lambda) continue;
            var parameters = lambda.Symbol.Parameters;
            var body = lambda.Body.Operations.OfType<IReturnOperation>().FirstOrDefault()?.ReturnedValue;
            if (body is not null) new Walker(context, parameters).Visit(body);
        }
    }

    private static IAnonymousFunctionOperation? Lambda(IOperation operation) => operation switch
    {
        IAnonymousFunctionOperation lambda => lambda,
        IConversionOperation conversion => Lambda(conversion.Operand),
        IDelegateCreationOperation creation => Lambda(creation.Target),
        _ => null,
    };

    private sealed class Walker(OperationAnalysisContext context, ImmutableArray<IParameterSymbol> parameters)
    {
        public void Visit(IOperation? operation)
        {
            if (operation is null || !ReadsRow(operation)) return;   // computed before the query

            switch (operation)
            {
                case IPropertyReferenceOperation property:
                    Property(property);
                    return;
                case IFieldReferenceOperation field:
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, field.Syntax.GetLocation(), field.Field.Name));
                    return;
                case IInvocationOperation call:
                    Invocation(call);
                    return;
                default:
                    foreach (var child in operation.ChildOperations) Visit(child);
                    return;
            }
        }

        private void Property(IPropertyReferenceOperation reference)
        {
            var property = reference.Property;
            if (reference.Instance is IParameterReferenceOperation parameter && parameters.Contains(parameter.Parameter, SymbolEqualityComparer.Default))
            {
                if (parameter.Parameter.Type is INamedTypeSymbol rowType && IsMapped(rowType) && !Mapped(rowType).Contains(property.Name))
                    context.ReportDiagnostic(Diagnostic.Create(UnmappedProperty, reference.Syntax.GetLocation(), rowType.Name, property.Name));
                return;
            }

            var owner = reference.Instance?.Type;
            var ownerType = owner is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : owner;
            var supported =
                (owner is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } && property.Name is "Value" or "HasValue") ||
                (ownerType?.SpecialType == SpecialType.System_String && property.Name == "Length") ||
                (IsDate(ownerType) && DateParts.Contains(property.Name));
            if (!supported)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMember, reference.Syntax.GetLocation(), property.Name));
                return;
            }
            Visit(reference.Instance);
        }

        private void Invocation(IInvocationOperation call)
        {
            var method = call.TargetMethod;
            var problem = Supported(call);
            if (problem is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMethod, call.Syntax.GetLocation(),
                    (method.ContainingType?.Name is { } type ? type + "." : "") + method.Name, problem));
                return;
            }
            Visit(call.Instance);
            foreach (var argument in call.Arguments) Visit(argument.Value);
        }

        /// <summary>Null when <paramref name="call"/> translates; otherwise why not. Mirrors SqlTranslator.Method.</summary>
        private string? Supported(IInvocationOperation call)
        {
            var method = call.TargetMethod;
            var declaring = method.ContainingType?.ToDisplayString();
            var args = call.Arguments.Length;
            var instance = call.Instance;

            if (method.Name == "Contains")
            {
                IOperation? collection = null;
                if (instance is not null && instance.Type?.SpecialType != SpecialType.System_String && args == 1) collection = instance;
                else if (instance is null && declaring is "System.Linq.Enumerable" or "System.MemoryExtensions" or "System.Linq.Queryable" && args is 2 or 3)
                    collection = call.Arguments[0].Value;
                if (collection is not null)
                    return ReadsRow(collection) ? "Contains on a collection that depends on the row (use WhereIn with a subquery)" : null;
            }

            if (declaring is "CL.Data.SqlFn" or "CL.Data.SqlFn.Json" or "CL.Data.SqlFn.FullText") return null;

            if (declaring == "string")
            {
                var stringArgs = call.Arguments.All(a => a.Value.Type?.SpecialType == SpecialType.System_String);
                return method.Name switch
                {
                    "Contains" or "StartsWith" or "EndsWith" when instance is not null && args == 1 && stringArgs => null,
                    "IsNullOrEmpty" or "IsNullOrWhiteSpace" when args == 1 => null,
                    "ToLower" or "ToLowerInvariant" or "ToUpper" or "ToUpperInvariant" or "Trim" when instance is not null && args == 0 => null,
                    "Replace" when instance is not null && args == 2 && stringArgs => null,
                    "Substring" when instance is not null && args is 1 or 2 => null,
                    "Concat" when instance is null && stringArgs => null,
                    "Equals" when (instance is not null && args == 1) || (instance is null && args == 2) => null,
                    _ => "this string method or overload is not supported",
                };
            }

            if (declaring is "System.Math" or "System.MathF")
            {
                return method.Name switch
                {
                    "Abs" or "Floor" or "Ceiling" when args == 1 => null,
                    "Round" when args == 1 || (args == 2 && call.Arguments[1].Value.Type?.SpecialType == SpecialType.System_Int32) => null,
                    "Min" or "Max" when args == 2 => null,
                    _ => "this Math method or overload is not supported",
                };
            }

            if (method.Name == "Equals" && method.ReturnType.SpecialType == SpecialType.System_Boolean
                && ((instance is not null && args == 1) || (instance is null && args == 2)))
                return null;

            return "compute it before the query, or use a SqlFn function";
        }

        private bool ReadsRow(IOperation operation)
        {
            if (operation is IParameterReferenceOperation reference && parameters.Contains(reference.Parameter, SymbolEqualityComparer.Default)) return true;
            foreach (var child in operation.ChildOperations)
                if (ReadsRow(child)) return true;
            return false;
        }

        private static bool IsDate(ITypeSymbol? type) =>
            type?.SpecialType == SpecialType.System_DateTime || type?.ToDisplayString() is "System.DateTimeOffset" or "System.DateOnly";

        private static bool IsMapped(INamedTypeSymbol type) =>
            EntityShape.HasAttribute(type, DataGenerator.TableAttribute) || EntityShape.HasAttribute(type, DataGenerator.DataRowAttribute);

        private static HashSet<string> Mapped(INamedTypeSymbol type) => [.. EntityShape.Analyze(type).Mapped.Select(p => p.Name)];
    }
}
