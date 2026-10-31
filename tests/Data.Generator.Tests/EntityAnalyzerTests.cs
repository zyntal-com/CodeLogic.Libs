using System.Collections.Immutable;
using CL.Data.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Data.Generator.Tests;

public sealed class EntityAnalyzerTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(CL.Data.TableAttribute).Assembly.Location),
    ];

    private static async Task<(string Id, string Symbol)[]> Analyze(string source)
    {
        var compilation = CSharpCompilation.Create("Analyzed", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var diagnostics = await compilation.WithAnalyzers([new EntityMappingAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .Select(d => (d.Id, Symbol: d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan) ?? ""))
            .OrderBy(d => d.Id, StringComparer.Ordinal).ThenBy(d => d.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public async Task Valid_entities_have_no_diagnostics()
    {
        var found = await Analyze("""
            using System; using CL.Data;
            [Table(Name = "orders")]
            [CompositeIndex("ix_customer", nameof(Order.CustomerId), nameof(Order.PlacedAt), Include = [nameof(Order.Total)])]
            public sealed class Order
            {
                [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
                public int CustomerId { get; set; }
                public DateTimeOffset PlacedAt { get; set; }
                public decimal Total { get; set; }
                [ForeignKey<Order>(nameof(Id), OnDelete = ForeignKeyAction.SetNull)] public long? ParentId { get; set; }
                [SoftDelete] public DateTimeOffset? DeletedAt { get; set; }
                [ConcurrencyToken] public int Version { get; set; }
            }
            [Table] public sealed record Line([property: Column(Primary = true, KeyOrder = 1)] long OrderId, [property: Column(Primary = true, KeyOrder = 2)] int No);
            """);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Mapping_mistakes_are_build_errors()
    {
        var found = await Analyze("""
            using System; using CL.Data;
            namespace Bad;
            public sealed class NotAConverter { }

            [Table(Name = "bad\"name")]
            public sealed class Broken
            {
                [Column(Primary = true)] public int? Id { get; set; }                             // CLD004
                [Column(AutoIncrement = true)] public string Code { get; set; } = "";              // CLD006
                [Column(Converter = typeof(NotAConverter))] public int Kind { get; set; }           // CLD003
                [SoftDelete] public DateTimeOffset Deleted { get; set; }                           // CLD006 (not nullable)
                [CreatedAt] public DateTime A { get; set; }
                [CreatedAt] public DateTime B { get; set; }                                        // CLD007
                [ConcurrencyToken] public string Token { get; set; } = "";                         // CLD006
                [Column(Name = "dup")] public int X { get; set; }
                [Column(Name = "DUP")] public int Y { get; set; }                                  // CLD009
                [Index(Include = ["Nope"])] public int Z { get; set; }                             // CLD008
                [ForeignKey<Broken>(nameof(X), OnDelete = ForeignKeyAction.SetNull)] public int Ref { get; set; }   // CLD010
            }

            [Table] public sealed class TwoKeys { [Column(Primary = true)] public int A { get; set; } [Column(Primary = true)] public int B { get; set; } }   // CLD005

            [Table] public sealed class NoDefault { public NoDefault(int other) { } public int Id { get; set; } }   // CLD002

            [Table] public sealed class Empty { }                                                   // CLD011

            public static class Outer { [Table] private sealed class Hidden { public int Id { get; set; } } }        // CLD001
            [Table] public sealed class Generic<T> { public int Id { get; set; } }                                  // CLD001
            [Table] public sealed class WriteOnly { public int Id { get; set; } public int Secret { private get; set; } } // CLD001
            """);

        var ids = found.Select(f => f.Id).ToArray();
        Assert.Equal(3, ids.Count(i => i == "CLD001"));
        Assert.Contains(found, f => f.Id == "CLD002" && f.Symbol == "NoDefault");
        Assert.Contains(found, f => f.Id == "CLD003");
        Assert.Contains(found, f => f.Id == "CLD004");
        Assert.Contains(found, f => f.Id == "CLD005" && f.Symbol == "TwoKeys");
        Assert.Equal(3, ids.Count(i => i == "CLD006"));
        Assert.Contains(found, f => f.Id == "CLD007");
        Assert.Contains(found, f => f.Id == "CLD008");
        Assert.Equal(2, ids.Count(i => i == "CLD009"));   // duplicate column name + unsafe table name
        Assert.Contains(found, f => f.Id == "CLD010");
        Assert.Contains(found, f => f.Id == "CLD011" && f.Symbol == "Empty");
    }
}
