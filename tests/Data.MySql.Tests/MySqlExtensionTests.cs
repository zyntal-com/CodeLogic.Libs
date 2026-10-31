using CL.Data.MySql;
using CodeLogic.Core.Results;
using Data.Conformance;

namespace Data.MySql.Tests;

[Collection("mysql")]
public sealed class MySqlExtensionTests(MySqlFixture fixture)
{
    [SkippableFact]
    public async Task Optimize_and_analyze_report_per_table_messages()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var optimized = (await fixture.Db.MySql.OptimizeTableAsync<Product>()).ValueOrThrow();
        Assert.NotEmpty(optimized);
        Assert.All(optimized, m => Assert.Equal("optimize", m.Operation));
        var analyzed = (await fixture.Db.MySql.AnalyzeTableAsync<Product>()).ValueOrThrow();
        Assert.Contains(analyzed, m => m.Type == "status");
    }
}
