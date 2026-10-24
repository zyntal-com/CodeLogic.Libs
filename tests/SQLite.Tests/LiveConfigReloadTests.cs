using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeLogic;
using CL.SQLite;
using CL.SQLite.Models;
using CL.SQLite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SQLite.Tests;

/// <summary>
/// Pool behaviour behind live config changes, on a standalone <see cref="ConnectionManager"/>
/// (no runtime): a connection in use while its ID is replaced goes back to the pool it came
/// from, and swapping non-connection settings keeps the pooled connections.
/// </summary>
public sealed class ConnectionManagerReplaceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_replace_" + Guid.NewGuid().ToString("N"));

    public ConnectionManagerReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* Windows may hold the file briefly */ }
    }

    private SQLiteDatabaseConfig Config(string file, int maxPool = 2) =>
        new() { DatabasePath = Path.Combine(_dir, file), MaxPoolSize = maxPool };

    [Fact]
    public async Task Connection_in_use_during_replace_returns_to_its_own_pool()
    {
        using var manager = new ConnectionManager();
        manager.RegisterConfiguration("Default", Config("a.db", maxPool: 1));

        var held = await manager.GetConnectionAsync("Default");
        Assert.True(await manager.ReplaceConfigurationAsync("Default", Config("b.db", maxPool: 1)));

        // The new pool has its own slot; the old connection must not occupy or corrupt it.
        await manager.ReleaseConnectionAsync(held, "Default");
        Assert.Equal(0, manager.GetActiveConnectionCount("Default"));
        Assert.Equal(0, manager.GetPooledConnectionCount("Default"));

        var fresh = await manager.GetConnectionAsync("Default");
        Assert.EndsWith("b.db", fresh.DataSource);
        await manager.ReleaseConnectionAsync(fresh, "Default");
        Assert.Equal(1, manager.GetPooledConnectionCount("Default"));
    }

    [Fact]
    public async Task Non_connection_settings_swap_in_place_and_keep_the_pool()
    {
        using var manager = new ConnectionManager();
        manager.RegisterConfiguration("Default", Config("a.db"));
        await manager.ReleaseConnectionAsync(await manager.GetConnectionAsync("Default"), "Default");
        Assert.Equal(1, manager.GetPooledConnectionCount("Default"));

        var tuned = Config("a.db");
        tuned.SlowQueryThresholdMs = 1234;
        Assert.False(await manager.ReplaceConfigurationAsync("Default", tuned));

        Assert.Equal(1, manager.GetPooledConnectionCount("Default"));
        Assert.Same(tuned, manager.GetConfiguration("Default"));
    }

    [Fact]
    public async Task Unregister_releases_the_id_and_late_releases_are_harmless()
    {
        using var manager = new ConnectionManager();
        manager.RegisterConfiguration("Default", Config("a.db"));
        manager.RegisterConfiguration("Other", Config("o.db"));
        var held = await manager.GetConnectionAsync("Other");

        Assert.True(await manager.UnregisterConfigurationAsync("Other"));
        Assert.DoesNotContain("Other", manager.GetConnectionIds());
        Assert.Contains("Default", manager.GetConnectionIds());

        await manager.ReleaseConnectionAsync(held, "Other"); // no "not registered" error
        Assert.False(await manager.UnregisterConfigurationAsync("Other"));
    }
}

/// <summary>Live config changes through the booted runtime (shared fixture).</summary>
[Collection("codelogic")]
public sealed class LiveConfigReloadTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SQLiteRuntimeFixture _fx;

    public LiveConfigReloadTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private SQLiteLibrary Sqlite => _fx.Library;

    private static SQLiteConfig CurrentConfig() =>
        CodeLogic.CodeLogic.GetLibraryManager()!.GetLoadedLibraries()
            .Single(l => l.Manifest.Id == "CL.SQLite").Context!.Configuration.Get<SQLiteConfig>();

    private static async Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateAsync(JsonNode config) =>
        await Libraries.UpdateConfigAsync("CL.SQLite", "sqlite", config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Changing_one_database_leaves_the_others_untouched()
    {
        var original = JsonSerializer.Serialize(CurrentConfig(), Json);
        var defaultBefore = Sqlite.ConnectionManager.GetConfiguration("Default");
        var defaultPath = Sqlite.ConnectionManager.GetDatabasePath("Default");
        var extraPath = Path.Combine(Path.GetDirectoryName(defaultPath)!, "reload_extra.db");

        try
        {
            var config = JsonNode.Parse(original)!;
            var databases = config["databases"]!.AsObject();

            // Add a database → it opens; Default is not re-registered.
            databases["Reload"] = new JsonObject { ["enabled"] = true, ["databasePath"] = extraPath };
            var added = await UpdateAsync(config);
            Assert.True(added.Applied, added.ToString());
            Assert.Equal(extraPath, Sqlite.ConnectionManager.GetDatabasePath("Reload"));
            Assert.Same(defaultBefore, Sqlite.ConnectionManager.GetConfiguration("Default"));

            // Non-connection setting → updated in place.
            databases["Reload"]!["slowQueryThresholdMs"] = 4321;
            var tuned = await UpdateAsync(config);
            Assert.True(tuned.Applied, tuned.ToString());
            Assert.Equal(4321, Sqlite.ConnectionManager.GetConfiguration("Reload").SlowQueryThresholdMs);
            Assert.Same(defaultBefore, Sqlite.ConnectionManager.GetConfiguration("Default"));

            // Path change → new pool on the new file.
            var movedPath = Path.Combine(Path.GetDirectoryName(defaultPath)!, "reload_moved.db");
            databases["Reload"]!["databasePath"] = movedPath;
            var moved = await UpdateAsync(config);
            Assert.True(moved.Applied, moved.ToString());
            Assert.Equal(movedPath, Sqlite.ConnectionManager.GetDatabasePath("Reload"));

            // Remove it → released; Default still untouched and working.
            databases.Remove("Reload");
            var removed = await UpdateAsync(config);
            Assert.True(removed.Applied, removed.ToString());
            Assert.DoesNotContain("Reload", Sqlite.ConnectionManager.GetConnectionIds());
            Assert.Same(defaultBefore, Sqlite.ConnectionManager.GetConfiguration("Default"));
            Assert.True(await Sqlite.ConnectionManager.TestConnectionAsync("Default"));
        }
        finally
        {
            await Libraries.UpdateConfigAsync("CL.SQLite", "sqlite", original, changedBy: "test");
        }
    }
}
