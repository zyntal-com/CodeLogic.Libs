using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Routing;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>The replica probe runs on a real server ("replica" = the same server, which reports no lag or none at all).</summary>
public abstract class ReplicaConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    [SkippableFact]
    public async Task Probe_runs_on_the_server_and_routing_keeps_the_replica()
    {
        RequireProvider();
        Skip.If(Fixture.Provider.ReplicaProbe is null, $"{Fixture.Provider.Name} has no replicas.");
        var config = new DataConnectionConfig
        {
            Provider = Db.Config.Provider,
            ConnectionString = Db.Config.ConnectionString,
            MySql = Db.Config.MySql,
            PostgreSql = Db.Config.PostgreSql,
            SqlServer = Db.Config.SqlServer,
            ReadPreference = ReadPreference.ReplicaOnly,
            Replicas = [new ReplicaConfig { Name = "self", ConnectionString = Db.Config.ConnectionString }],
        };
        await using var db = Database.Create("replica-probe", Fixture.Provider, config).ValueOrThrow();

        Assert.Equal(1, await db.ProbeReplicasAsync());
        Assert.True(db.Replicas[0].LagMs is null or 0, $"lag {db.Replicas[0].LagMs}");   // not replicating: unknown or caught up
        Assert.True(db.Replicas[0].IsHealthy);
        Assert.Equal(1L, (await db.SqlScalarAsync<long>($"SELECT 1")).ValueOrThrow());   // ReplicaOnly read served by "self"
    }
}
