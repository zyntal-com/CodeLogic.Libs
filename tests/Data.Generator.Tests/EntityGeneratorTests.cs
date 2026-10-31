using System.Collections.Immutable;
using CL.Data.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Data.Generator.Tests;

public sealed class EntityGeneratorTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(CL.Data.TableAttribute).Assembly.Location),
    ];

    private static (GeneratorDriver Driver, Compilation Output, ImmutableArray<Diagnostic> Errors) Run(string source, GeneratorDriver? driver = null, Compilation? previous = null)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = previous ?? CSharpCompilation.Create("Gen", [CSharpSyntaxTree.ParseText(source, parse)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        driver ??= CSharpGeneratorDriver.Create([new DataGenerator().AsSourceGenerator()], parseOptions: parse,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return (driver, output, errors);
    }

    private static string[] Generated(GeneratorDriver driver) =>
        driver.GetRunResult().GeneratedTrees.Select(t => Path.GetFileName(t.FilePath)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private const string Entities = """
        using System;
        using CL.Data;
        namespace Shop;

        [Table(Name = "products")]
        [CompositeIndex("ix_sku_name", nameof(Sku), nameof(Name), Unique = true)]
        public sealed class Product
        {
            [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
            [Column(Size = 40, Comment = "a \"quoted\" \\ comment")] public string Sku { get; set; } = "";
            public string? Name { get; set; }
            [ForeignKey<Category>(nameof(Category.Id), OnDelete = ForeignKeyAction.Cascade)] public int CategoryId { get; set; }
            [Ignore] public string Display => Sku;
        }

        [Table] public sealed class Category { [Column(Primary = true)] public int Id { get; init; } public required string Title { get; set; } }

        [Table] public sealed record Line([property: Column(Primary = true)] long Id, decimal Amount);

        [DataRow] public sealed class Summary { public string Name { get; set; } = ""; public long Count { get; set; } }

        [Table] public abstract class AbstractEntity { public int Id { get; set; } }
        [Table] public sealed class Generic<T> { public int Id { get; set; } }
        public static class Outer { [Table] private sealed class Hidden { public int Id { get; set; } } }
        """;

    [Fact]
    public void Generates_accessors_that_compile()
    {
        var (driver, _, errors) = Run(Entities);
        Assert.Empty(errors);
        Assert.Equal(
        [
            "CLDataAccessorRegistration.g.cs",
            "Shop_Category_DataAccessor.g.cs",
            "Shop_Line_DataAccessor.g.cs",
            "Shop_Product_DataAccessor.g.cs",
            "Shop_Summary_DataAccessor.g.cs",
        ], Generated(driver));   // abstract, generic and private nested types are left to reflection
    }

    [Fact]
    public void Attribute_values_are_emitted_exactly()
    {
        var (driver, _, _) = Run(Entities);
        var product = driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.EndsWith("Shop_Product_DataAccessor.g.cs", StringComparison.Ordinal)).ToString();
        Assert.Contains("Comment = \"a \\\"quoted\\\" \\\\ comment\"", product);
        Assert.Contains("new global::CL.Data.ForeignKeyAttribute<global::Shop.Category>(\"Id\") { OnDelete = (global::CL.Data.ForeignKeyAction)(", product);
        Assert.Contains("new global::CL.Data.CompositeIndexAttribute(\"ix_sku_name\", new string[] { \"Sku\", \"Name\" }) { Unique = true }", product);
        Assert.DoesNotContain("Display", product);

        var category = driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.EndsWith("Shop_Category_DataAccessor.g.cs", StringComparison.Ordinal)).ToString();
        Assert.Contains("new global::Shop.Category() { Title = ", category);           // required member in the initializer
        Assert.Contains("UnsafeAccessorKind.Method, Name = \"set_Id\"", category);      // init-only setter
    }

    [Fact]
    public void Unrelated_edits_reuse_the_generated_output()
    {
        var (driver, output, _) = Run(Entities);
        var edited = output.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Shop; public static class Helper { public static int Two => 2; }",
            new CSharpParseOptions(LanguageVersion.Latest)));
        driver = driver.RunGenerators(edited);
        var outputs = driver.GetRunResult().Results.Single().TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(v => v.Outputs);
        Assert.All(outputs, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, o.Reason.ToString()));
    }
}
