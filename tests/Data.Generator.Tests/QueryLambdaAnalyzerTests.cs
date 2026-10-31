using System.Collections.Immutable;
using CL.Data.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Data.Generator.Tests;

public sealed class QueryLambdaAnalyzerTests
{
    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
        MetadataReference.CreateFromFile(typeof(CL.Data.TableAttribute).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(CodeLogic.Core.Results.Result).Assembly.Location),
    ];

    private static async Task<(string Id, string Code)[]> Analyze(string body)
    {
        var source = """
            using System; using System.Linq; using System.Collections.Generic; using CL.Data; using CL.Data.Execution;
            [Table] public sealed class Item
            {
                [Column(Primary = true)] public long Id { get; set; }
                public string Name { get; set; } = "";
                public string? Nick { get; set; }
                public DateTimeOffset At { get; set; }
                public int? Parent { get; set; }
                public decimal Price { get; set; }
                [Ignore] public string Shout => Name + "!";
            }
            public static class Helper { public static bool IsGood(string s) => s.Length > 2; }
            public static class Queries
            {
                public static void Run(Database db, long[] ids, string term, int day)
                {
            """ + body + """
                }
            }
            """;
        var compilation = CSharpCompilation.Create("Q", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Empty(errors);
        var diagnostics = await compilation.WithAnalyzers([new QueryLambdaAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return diagnostics.Select(d => (d.Id, d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))).ToArray();
    }

    [Fact]
    public async Task Supported_lambdas_are_quiet()
    {
        var found = await Analyze("""
            db.Query<Item>().Where(i => i.Name.Contains(term) && i.Name.ToLower().StartsWith("a") && i.Name.Length > 3);
            db.Query<Item>().Where(i => ids.Contains(i.Id) || i.Parent.HasValue && i.Parent.Value > 2 || (i.Nick ?? "") == "x");
            db.Query<Item>().Where(i => i.At.Year == DateTime.UtcNow.Year && i.At > DateTimeOffset.UtcNow.AddDays(-day));
            db.Query<Item>().Where(i => Math.Round(i.Price, 2) > 10 && string.IsNullOrEmpty(i.Nick) && SqlFn.Lower(i.Name) == "a");
            db.Query<Item>().Where(i => Helper.IsGood(term) && i.Name.Equals(term)).OrderBy(i => i.Name.Substring(1, 2)).ThenBy(i => i.Id);
            """);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Untranslatable_lambdas_are_reported()
    {
        var found = await Analyze("""
            db.Query<Item>().Where(i => Helper.IsGood(i.Name));
            db.Query<Item>().Where(i => i.Name.GetHashCode() == 1);
            db.Query<Item>().Where(i => i.Name.PadLeft(5) == "x");
            db.Query<Item>().Where(i => i.At.AddDays(1) > DateTimeOffset.UtcNow);
            db.Query<Item>().Where(i => i.At.Offset == TimeSpan.Zero);
            db.Query<Item>().Where(i => i.Shout == "A!");
            db.Query<Item>().Where(i => new[] { i.Id }.Contains(5));
            """);
        Assert.Equal(["CLD101", "CLD101", "CLD101", "CLD101", "CLD101", "CLD102", "CLD103"], found.Select(f => f.Id).Order().ToArray());
        Assert.Contains(found, f => f.Id == "CLD103" && f.Code == "i.Shout");
        Assert.Contains(found, f => f.Id == "CLD102" && f.Code == "i.At.Offset");
    }
}
