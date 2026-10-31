using CL.Data.SqlServer;
using CodeLogic.Core.Results;
using Data.Conformance;

namespace Data.SqlServer.Tests;

[Collection("sqlserver")]
public sealed class SqlServerExtensionTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Update_statistics_runs_on_the_entity_table()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        (await fixture.Db.SqlServer.UpdateStatisticsAsync<Product>()).ThrowIfFailure();
        (await fixture.Db.SqlServer.UpdateStatisticsAsync<Product>(fullScan: true)).ThrowIfFailure();
    }
}
