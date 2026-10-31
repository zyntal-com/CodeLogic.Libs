using Microsoft.CodeAnalysis;

namespace CL.Data.Generator;

/// <summary>
/// Entry point of the CL.Data source generator. It emits entity metadata, materializers and
/// parameter binders for <c>[Table]</c> / <c>[DataRow]</c> types (WP-40/41) and compile-time
/// query plans through interceptors (WP-45).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DataGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Pipelines are added by the generator work packages.
    }
}
