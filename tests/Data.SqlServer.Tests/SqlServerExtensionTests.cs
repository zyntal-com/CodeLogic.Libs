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

/// <summary>A table with a JSON column for the ISJSON check.</summary>
[CL.Data.Table(Name = "conf_json_docs")]
public sealed class JsonDoc
{
    /// <summary>Identity.</summary>
    [CL.Data.Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>JSON text.</summary>
    [CL.Data.Column(DataType = CL.Data.DataType.Json)] public string Body { get; set; } = "{}";
}

[Collection("sqlserver")]
public sealed class SqlServerJsonCheckTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task Json_columns_reject_invalid_json()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var db = fixture.Db;
        (await db.Admin.DropTableAsync<JsonDoc>()).ThrowIfFailure();
        (await db.Schema.SyncAsync<JsonDoc>()).ValueOrThrow();
        (await db.Repository<JsonDoc>().InsertAsync(new JsonDoc { Body = """{"a":1}""" })).ValueOrThrow();
        var bad = await db.Repository<JsonDoc>().InsertAsync(new JsonDoc { Body = "not json" });
        Assert.True(bad.IsFailure);
        Assert.Equal(CL.Data.Errors.DataErrors.ConstraintViolationCode, bad.Error!.Code);
        var again = (await db.Schema.SyncAsync<JsonDoc>()).ValueOrThrow();   // the check does not show up as drift
        (await db.Admin.DropTableAsync<JsonDoc>()).ThrowIfFailure();
    }
}
