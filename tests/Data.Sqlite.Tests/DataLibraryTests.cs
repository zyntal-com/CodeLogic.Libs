using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Hosting;
using CL.Data.Sqlite;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace Data.Sqlite.Tests;

/// <summary>CL.Data hosted by a real <see cref="LibraryManager"/> with the SQLite provider library.</summary>
public sealed class DataLibraryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_data_host_" + Guid.NewGuid().ToString("N"));
    private readonly EventBus _events = new();
    private LibraryManager _manager = null!;
    private DataLibrary _data = null!;
    private string _dataConfig = null!;

    private string File(string name) => Path.Combine(_root, name + ".db");

    private JsonObject Connection(string file) => new() { ["provider"] = "sqlite", ["database"] = File(file) };

    public async Task InitializeAsync()
    {
        var dir = Path.Combine(_root, "Libraries", "CL.Data");
        Directory.CreateDirectory(dir);
        _dataConfig = Path.Combine(dir, "config.data.json");
        var config = new JsonObject
        {
            ["enabled"] = true,
            ["defaultConnection"] = "Main",
            ["connections"] = new JsonObject
            {
                ["Main"] = Connection("main"),
                ["Reports"] = Connection("reports"),
                ["Fresh"] = new JsonObject { ["provider"] = "sqlite", ["database"] = File("fresh"), ["createDatabaseIfMissing"] = true },
                ["Uncached"] = new JsonObject { ["provider"] = "sqlite", ["database"] = File("uncached"), ["cacheEnabled"] = false },
                ["Broken"] = new JsonObject { ["provider"] = "oracle", ["host"] = "x", ["database"] = "y" },
            },
        };
        await System.IO.File.WriteAllTextAsync(_dataConfig, config.ToJsonString());

        _manager = new LibraryManager(_events) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<SqliteDataLibrary>();   // loaded first on purpose: the dependency puts CL.Data first
        await _manager.LoadLibraryAsync<DataLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _data = _manager.GetLibrary<DataLibrary>()!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* the logger may still hold its file */ }
    }

    private Task<ConfigApplyResult> UpdateAsync(string section, JsonNode config) =>
        _manager.UpdateConfigAsync("CL.Data", section, config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Connections_open_and_a_broken_one_is_reported_not_fatal()
    {
        var main = _data.Db().ValueOrThrow();
        Assert.Equal("Main", main.Name);
        Assert.Equal(1L, (await main.SqlScalarAsync<long>($"SELECT 1")).ValueOrThrow());
        Assert.Equal("Reports", _data.Db("reports").ValueOrThrow().Name);   // names are case-insensitive

        Assert.Equal(DataErrors.ProviderMissingCode, _data.Db("Broken").Error!.Code);
        Assert.Same(_data.Cache, main.Options.Cache);
        Assert.Null(_data.Db("Uncached").ValueOrThrow().Options.Cache);      // CacheEnabled = false opts out
        Assert.True(System.IO.File.Exists(File("fresh")));                   // CreateDatabaseIfMissing ran at Initialize
        Assert.Equal(DataErrors.ConnectionUnknownCode, _data.Db("Nope").Error!.Code);

        var health = await _data.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Degraded, health.Status);
        Assert.Contains("Broken", health.Message);
    }

    [Fact]
    public async Task Events_reach_the_CodeLogic_bus_under_their_own_type()
    {
        ConcurrentQueue<BulkCopyCompletedEvent> seen = new();
        using var subscription = _events.Subscribe<BulkCopyCompletedEvent>(seen.Enqueue);
        var db = _data.Db().ValueOrThrow();
        (await db.ExecuteSqlAsync($"CREATE TABLE IF NOT EXISTS host_items (id INTEGER PRIMARY KEY, v TEXT)")).ValueOrThrow();
        (await db.Schema.SyncAsync<HostItem>()).ValueOrThrow();
        (await db.Bulk<HostItem>().InsertAsync([new HostItem { Name = "a" }, new HostItem { Name = "b" }])).ValueOrThrow();
        var e = Assert.Single(seen);
        Assert.Equal(("Main", 2L), (e.ConnectionId, e.Rows));
    }

    [Fact]
    public async Task Live_config_changes_touch_only_the_changed_connections()
    {
        var main = _data.Db("Main").ValueOrThrow();
        var reports = _data.Db("Reports").ValueOrThrow();

        var config = JsonNode.Parse(System.IO.File.ReadAllText(_dataConfig))!;
        config["connections"]!["Reports"]!["commandTimeoutSeconds"] = 5;    // changed
        config["connections"]!["Archive"] = Connection("archive");           // added
        config["connections"]!.AsObject().Remove("Broken");                  // removed
        var result = await UpdateAsync("data", config);

        Assert.True(result.Applied, result.ToString());
        Assert.Same(main, _data.Db("Main").ValueOrThrow());                  // untouched: same pools
        var newReports = _data.Db("Reports").ValueOrThrow();
        Assert.NotSame(reports, newReports);
        Assert.Equal(5, newReports.Config.CommandTimeoutSeconds);
        Assert.Equal(1L, (await _data.Db("Archive").ValueOrThrow().SqlScalarAsync<long>($"SELECT 1")).ValueOrThrow());
        Assert.Equal(DataErrors.ConnectionUnknownCode, _data.Db("Broken").Error!.Code);
        Assert.Equal(HealthStatusLevel.Healthy, (await _data.HealthCheckAsync()).Status);
    }

    [Fact]
    public async Task A_cache_change_rebuilds_the_cache_and_links_the_event_bus()
    {
        var cache = _data.Cache;
        Assert.NotNull(cache);
        var result = await UpdateAsync("data.cache", new JsonObject { ["enabled"] = true, ["invalidationBus"] = "Events", ["defaultTtlSeconds"] = 30 });
        Assert.True(result.Applied, result.ToString());
        Assert.NotSame(cache, _data.Cache);

        ConcurrentQueue<CacheInvalidatedEvent> published = new();
        using var subscription = _events.Subscribe<CacheInvalidatedEvent>(published.Enqueue);
        var db = _data.Db().ValueOrThrow();
        Assert.Same(_data.Cache, db.Options.Cache);
        (await db.ExecuteSqlAsync($"CREATE TABLE IF NOT EXISTS bus_probe (id INTEGER)")).ValueOrThrow();
        await Task.Delay(100);
        Assert.Contains(published, e => e.Tags.Contains("Main|*"));          // raw SQL drops the whole connection on every node
    }
}

/// <summary>A table for the hosting tests.</summary>
[CL.Data.Table(Name = "host_bulk")]
public sealed class HostItem
{
    /// <summary>Identity.</summary>
    [CL.Data.Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Name.</summary>
    [CL.Data.Column(Size = 40)] public string Name { get; set; } = "";
}
