using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Routing;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

/// <summary>Read routing over a primary and two replicas, each its own SQLite file holding its own node name.</summary>
public sealed class RoutingTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cl-data-route-{Guid.NewGuid():N}.db");
    private readonly List<Database> _created = [];

    private (Database Db, FaultyProvider Provider, ManualClock Clock) Create(ReadPreference preference = ReadPreference.PreferReplica,
        int stickyMs = 0, int maxLagMs = 5000, int r1Weight = 1, int r2Weight = 1, int breaker = 0)
    {
        var clock = new ManualClock();
        var provider = new FaultyProvider();
        var db = Database.Create("route", provider, new DataConnectionConfig
        {
            Provider = "faulty",
            Host = "localhost",
            Database = _file,
            ReadPreference = preference,
            StickyAfterWriteMs = stickyMs,
            MaxReplicaLagMs = maxLagMs,
            RetryCount = 0,
            CircuitBreakerThreshold = breaker,
            Replicas =
            [
                new ReplicaConfig { Name = "r1", Host = "r1", Weight = r1Weight },
                new ReplicaConfig { Name = "r2", Host = "r2", Weight = r2Weight },
            ],
        }, new DatabaseOptions { Time = clock }).ValueOrThrow();
        _created.Add(db);
        return (db, provider, clock);
    }

    public async Task InitializeAsync()
    {
        // Seed every node directly: each file says which node it is.
        var (db, _, _) = Create(ReadPreference.Primary);
        foreach (var node in new[] { db.Primary }.Concat(db.Replicas))
        {
            await using var connection = await node.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE who (node TEXT NOT NULL); INSERT INTO who VALUES ('{node.Name}'); CREATE TABLE lag (ms INTEGER NULL); INSERT INTO lag VALUES (0);";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _file, _file + ".r1", _file + ".r2" })
            try { File.Delete(file); } catch (IOException) { }
    }

    private static async Task<string> Who(Database db) => (await db.SqlScalarAsync<string>($"SELECT node FROM who")).ValueOrThrow()!;

    [Fact]
    public async Task Reads_spread_over_replicas_by_weight_and_writes_use_the_primary()
    {
        var (db, provider, _) = Create(r1Weight: 3, r2Weight: 1);
        Dictionary<string, int> seen = [];
        for (var i = 0; i < 400; i++)
        {
            var node = await Who(db);
            seen[node] = seen.GetValueOrDefault(node) + 1;
        }
        Assert.False(seen.ContainsKey("primary"));
        Assert.InRange(seen["r1"], 240, 360);                                // ~300 of 400 at weight 3:1

        var primaryOpens = provider.Source.Opens;
        (await db.ExecuteSqlAsync($"INSERT INTO who VALUES ({"written"})")).ValueOrThrow();
        Assert.Equal(primaryOpens + 1, provider.Source.Opens);
        Assert.Equal(1L, (await db.ReadFrom(ReadPreference.Primary).SqlScalarAsync<long>($"SELECT COUNT(*) FROM who WHERE node = {"written"}")).ValueOrThrow());
    }

    [Fact]
    public async Task Preferences_per_connection_view_and_transaction()
    {
        var (db, _, _) = Create(ReadPreference.Primary);
        Assert.Equal("primary", await Who(db));
        Assert.StartsWith("r", await Who(db.ReadFrom(ReadPreference.ReplicaOnly)));

        var (replicaDb, _, _) = Create();
        Assert.Equal("primary", await Who(replicaDb.ReadFrom(ReadPreference.Primary)));
        var tx = (await replicaDb.BeginTransactionAsync()).ValueOrThrow();
        await using (tx) Assert.Equal("primary", await Who(tx.Db));        // transactions always run on the primary
    }

    [Fact]
    public async Task Reads_stick_to_the_primary_right_after_a_write()
    {
        var (db, _, clock) = Create(stickyMs: 2000);
        Assert.StartsWith("r", await Who(db));
        (await db.ExecuteSqlAsync($"UPDATE who SET node = node")).ValueOrThrow();
        Assert.Equal("primary", await Who(db));                              // reads-your-writes window
        clock.Now = clock.Now.AddSeconds(3);
        Assert.StartsWith("r", await Who(db));
    }

    [Fact]
    public async Task Lagging_and_failed_replicas_are_skipped()
    {
        var (db, provider, _) = Create(breaker: 1);
        db.Replicas[0].LagMs = 60_000;                                       // r1 far behind
        for (var i = 0; i < 20; i++) Assert.Equal("r2", await Who(db));

        provider.Nodes["r2"].OpenFaults = 1;                                 // r2 goes down: this read falls back to the primary
        Assert.Equal("primary", await Who(db));
        Assert.False(db.Replicas[1].IsHealthy);                              // and its circuit is now open
        Assert.Equal("primary", await Who(db));                              // no usable replica: PreferReplica uses the primary

        var only = await db.ReadFrom(ReadPreference.ReplicaOnly).SqlScalarAsync<string>($"SELECT node FROM who");
        Assert.Equal(DataErrors.NoHealthyNodeCode, only.Error!.Code);

        db.Replicas[0].LagMs = 10;                                           // r1 caught up
        Assert.Equal("r1", await Who(db));
    }

    private static async Task SetLag(Database db, int replica, long? ms)
    {
        await using var connection = await db.Replicas[replica].DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ms is null ? "UPDATE lag SET ms = NULL" : $"UPDATE lag SET ms = {ms}";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Probes_measure_lag_and_bring_replicas_back()
    {
        var (db, provider, clock) = Create(breaker: 1);
        await SetLag(db, 0, 60_000);                                         // r1 far behind
        await SetLag(db, 1, null);                                           // r2 replication stopped
        Assert.Equal(2, await db.ProbeReplicasAsync());
        Assert.Equal((60_000L, CL.Data.Routing.ReplicaProbe.Broken), (db.Replicas[0].LagMs!.Value, db.Replicas[1].LagMs!.Value));
        Assert.Equal("primary", await Who(db));                              // both skipped

        await SetLag(db, 0, 100);
        provider.Nodes["r2"].OpenFaults = 1;                                 // r2 unreachable: its circuit opens
        Assert.Equal(1, await db.ProbeReplicasAsync());
        Assert.False(db.Replicas[1].IsHealthy);
        Assert.Equal("r1", await Who(db));

        await SetLag(db, 1, 0);
        clock.Now = clock.Now.AddSeconds(31);                                // break over: the next probe is the trial call
        Assert.Equal(2, await db.ProbeReplicasAsync());
        Assert.True(db.Replicas[1].IsHealthy);
    }
}
