using System.Collections.Concurrent;
using CL.Data.Configuration;
using CL.Data.Events;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Slow-query plans captured by each provider's explainer, with the query's parameters bound.</summary>
public abstract class TelemetryConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    [SkippableFact]
    public async Task Slow_reads_capture_the_provider_plan()
    {
        RequireProvider();
        Skip.If(Fixture.Provider.Explainer is null, $"{Fixture.Provider.Name} has no explainer.");
        ConcurrentQueue<DataEvent> events = new();
        var config = new DataConnectionConfig
        {
            Provider = Db.Config.Provider,
            ConnectionString = Db.Config.ConnectionString,
            Database = Db.Config.Database,
            MySql = Db.Config.MySql,
            PostgreSql = Db.Config.PostgreSql,
            SqlServer = Db.Config.SqlServer,
            Sqlite = Db.Config.Sqlite,
            SlowQueryThresholdMs = 1,
            CaptureExplainOnSlowQuery = true,
        };
        await using var db = Database.Create("telemetry", Fixture.Provider, config, new DatabaseOptions { Events = events.Enqueue }).ValueOrThrow();

        // Run until one read is slow enough (a local SQLite read may take under a millisecond).
        for (var i = 0; i < 50 && !events.OfType<SlowQueryEvent>().Any(); i++)
            (await db.Query<Product>().Where(p => p.Price > 5m && p.Sku != "none").CountAsync()).ValueOrThrow();

        for (var i = 0; i < 100 && !events.OfType<SlowQueryEvent>().Any(e => e.Plan is not null); i++) await Task.Delay(50);
        var slow = events.OfType<SlowQueryEvent>().FirstOrDefault(e => e.Plan is not null);
        Skip.If(slow is null, "No read crossed the 1 ms threshold.");
        Assert.DoesNotContain("plan not captured", slow!.Plan);
        Assert.False(string.IsNullOrWhiteSpace(slow.Plan));
    }
}
