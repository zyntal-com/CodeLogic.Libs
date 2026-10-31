using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Providers;
using CL.Data.Routing;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>
/// Routing and lag probes against a real primary/replica pair (tests/replicas/docker-compose.yml). Runs only with
/// <c>CL_DATA_REPLICAS=1</c>; replication is paused and resumed by the tests, so they run one at a time.
/// </summary>
public abstract class LiveReplicaSuite
{
    /// <summary>The provider under test.</summary>
    protected abstract DataProvider Provider { get; }

    /// <summary>Primary connection settings (host, port, database, user).</summary>
    protected abstract DataConnectionConfig Primary();

    /// <summary>Replica host and port.</summary>
    protected abstract (string Host, int Port) Replica { get; }

    /// <summary>A port nothing listens on, for a dead replica.</summary>
    protected abstract int DeadPort { get; }

    /// <summary>Returns 1 on a replica, 0 on the primary.</summary>
    protected abstract string IsReplicaSql { get; }

    /// <summary>Pauses applying changes on the replica (it keeps receiving them).</summary>
    protected abstract string PauseSql { get; }

    /// <summary>Resumes applying changes.</summary>
    protected abstract string ResumeSql { get; }

    private static void Require() => Skip.IfNot(Environment.GetEnvironmentVariable("CL_DATA_REPLICAS") == "1",
        "CL_DATA_REPLICAS is not 1 (start tests/replicas/docker-compose.yml first).");

    private Database Create(ReadPreference preference, int maxLagMs = 5000, int replicaPort = 0)
    {
        var config = Primary();
        config.ReadPreference = preference;
        config.StickyAfterWriteMs = 0;
        config.MaxReplicaLagMs = maxLagMs;
        config.RetryCount = 0;
        config.CircuitBreakerThreshold = 1;
        config.ConnectTimeoutSeconds = 2;
        config.Replicas = [new ReplicaConfig { Name = "replica", Host = Replica.Host, Port = replicaPort == 0 ? Replica.Port : replicaPort }];
        return Database.Create("live-replica", Provider, config).ValueOrThrow();
    }

    /// <summary>The replica itself as a plain connection, for pausing and resuming replication.</summary>
    private Database ReplicaAdmin()
    {
        var config = Primary();
        config.Host = Replica.Host;
        config.Port = Replica.Port;
        return Database.Create("replica-admin", Provider, config).ValueOrThrow();
    }

    private async Task<bool> OnReplica(Database db) => (await db.SqlScalarAsync<long>(SqlText.Raw(IsReplicaSql))).ValueOrThrow() == 1;

    private static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private static SqlText CreateTable =>
        SqlText.Raw("CREATE TABLE IF NOT EXISTS live_repl (id INT NOT NULL PRIMARY KEY, v VARCHAR(40) NOT NULL)");

    [SkippableFact]
    public async Task Replica_reads_see_primary_writes_and_report_live_lag()
    {
        Require();
        await using var db = Create(ReadPreference.ReplicaOnly);
        Assert.Equal(1, await db.ProbeReplicasAsync());
        Assert.InRange(db.Replicas[0].LagMs ?? -1, 0, 5000);

        (await db.ExecuteSqlAsync(CreateTable)).ValueOrThrow();
        var id = Random.Shared.Next(1, int.MaxValue);
        (await db.ExecuteSqlAsync($"INSERT INTO live_repl (id, v) VALUES ({id}, {"from-primary"})")).ValueOrThrow();

        Assert.True(await OnReplica(db));                                    // ReplicaOnly reads are served by the replica
        await Eventually(async () => (await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM live_repl WHERE id = {id}")).ValueOrThrow() == 1,
            "the write to replicate");
        Assert.False(await OnReplica(db.ReadFrom(ReadPreference.Primary)));
    }

    [SkippableFact]
    public async Task A_lagging_replica_is_skipped_until_it_catches_up()
    {
        Require();
        await using var db = Create(ReadPreference.PreferReplica, maxLagMs: 1000);
        await using var admin = ReplicaAdmin();
        (await db.ExecuteSqlAsync(CreateTable)).ValueOrThrow();
        try
        {
            (await admin.ExecuteSqlAsync(SqlText.Raw(PauseSql))).ValueOrThrow();
            (await db.ExecuteSqlAsync($"INSERT INTO live_repl (id, v) VALUES ({Random.Shared.Next(1, int.MaxValue)}, {"while-paused"})")).ValueOrThrow();
            await Task.Delay(1500);

            await db.ProbeReplicasAsync();
            Assert.True(db.Replicas[0].LagMs > 1000, $"lag {db.Replicas[0].LagMs}");
            Assert.False(await OnReplica(db));                               // PreferReplica: served by the primary
            var only = await db.ReadFrom(ReadPreference.ReplicaOnly).SqlScalarAsync<long>(SqlText.Raw(IsReplicaSql));
            Assert.Equal(DataErrors.NoHealthyNodeCode, only.Error!.Code);
        }
        finally
        {
            (await admin.ExecuteSqlAsync(SqlText.Raw(ResumeSql))).ValueOrThrow();
        }

        await Eventually(async () => { await db.ProbeReplicasAsync(); return db.Replicas[0].LagMs is <= 1000; }, "the replica to catch up");
        Assert.True(await OnReplica(db));
    }

    [SkippableFact]
    public async Task Reads_fall_back_to_the_primary_when_the_replica_is_down()
    {
        Require();
        await using var db = Create(ReadPreference.PreferReplica, replicaPort: DeadPort);
        Assert.False(await OnReplica(db));                                   // replica unreachable: the read runs on the primary
        Assert.False(db.Replicas[0].IsHealthy);
        Assert.Equal(0, await db.ProbeReplicasAsync());                      // circuit open: probes wait for the break to end
        var health = (await db.CheckHealthAsync()).ToDictionary(h => h.Node);
        Assert.True(health["primary"].Reachable);
        Assert.False(health["replica"].Reachable);
    }
}
