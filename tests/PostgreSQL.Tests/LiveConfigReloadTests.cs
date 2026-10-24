using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeLogic;
using CL.PostgreSQL;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// Live config changes (IConfigReloadable): a change to one database is applied without a
// restart and without touching the other databases. Runs against the shared fixture, adds a
// temporary "Reload" database next to "Default", and restores the original config at the end.

[Collection("codelogic")]
public sealed class LiveConfigReloadTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly PostgreSQLRuntimeFixture _fx;

    public LiveConfigReloadTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private PostgreSQLLibrary Pg => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    private static DatabaseConfiguration CurrentConfig() =>
        CodeLogic.CodeLogic.GetLibraryManager()!.GetLoadedLibraries()
            .Single(l => l.Manifest.Id == "CL.PostgreSQL").Context!.Configuration.Get<DatabaseConfiguration>();

    private static async Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateAsync(JsonNode config) =>
        await Libraries.UpdateConfigAsync("CL.PostgreSQL", "postgresql", config.ToJsonString(), changedBy: "test");

    [FactRequiresEnv(Gate, Reason)]
    public async Task Changing_one_database_leaves_the_others_untouched()
    {
        var original = JsonSerializer.Serialize(CurrentConfig(), Json);
        var defaultBefore = Pg.ConnectionManager.GetConfiguration("Default");
        Assert.NotNull(defaultBefore);

        try
        {
            var config = JsonNode.Parse(original)!;
            var databases = config["databases"]!.AsObject();

            // Add a database → it connects; Default is not re-registered.
            databases["Reload"] = databases["Default"]!.DeepClone();
            var added = await UpdateAsync(config);
            Assert.True(added.Applied, added.ToString());
            Assert.Contains("Reload", Pg.ConnectionManager.GetConnectionIds());
            Assert.True((await Pg.TestConnectionAsync("Reload")).Value);
            Assert.Same(defaultBefore, Pg.ConnectionManager.GetConfiguration("Default"));

            // A runtime sync mode survives an unrelated change to the same database.
            Pg.SetSyncMode(SyncMode.Migration, "Reload");

            // Non-connection setting → updated in place, no reconnect.
            databases["Reload"]!["slowQueryThresholdMs"] = 4321;
            var tuned = await UpdateAsync(config);
            Assert.True(tuned.Applied, tuned.ToString());
            Assert.Equal(4321, Pg.ConnectionManager.GetConfiguration("Reload")!.SlowQueryThresholdMs);
            Assert.Equal(SyncMode.Migration, CurrentConfig().Databases["Reload"].SyncMode);
            Assert.Same(defaultBefore, Pg.ConnectionManager.GetConfiguration("Default"));

            // Unreachable connection settings → saved but reported as not applied; Default keeps working.
            databases["Reload"]!["port"] = 1;
            databases["Reload"]!["connectionTimeout"] = 2;
            var broken = await UpdateAsync(config);
            Assert.True(broken.IsValid);
            Assert.False(broken.Applied);
            Assert.NotNull(broken.ApplyError);
            Assert.Same(defaultBefore, Pg.ConnectionManager.GetConfiguration("Default"));
            Assert.True((await Pg.TestConnectionAsync("Default")).Value);

            // Remove it → disconnected and unregistered; Default still untouched.
            databases.Remove("Reload");
            var removed = await UpdateAsync(config);
            Assert.True(removed.Applied, removed.ToString());
            Assert.DoesNotContain("Reload", Pg.ConnectionManager.GetConnectionIds());
            Assert.Same(defaultBefore, Pg.ConnectionManager.GetConfiguration("Default"));
        }
        finally
        {
            await Libraries.UpdateConfigAsync("CL.PostgreSQL", "postgresql", original, changedBy: "test");
        }
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Cache_section_changes_apply_live()
    {
        var context = CodeLogic.CodeLogic.GetLibraryManager()!.GetLoadedLibraries()
            .Single(l => l.Manifest.Id == "CL.PostgreSQL").Context!;
        var original = JsonSerializer.Serialize(context.Configuration.Get<CacheConfiguration>(), Json);

        try
        {
            var cache = JsonNode.Parse(original)!;
            cache["maxEntries"] = 1234;
            var result = await Libraries.UpdateConfigAsync("CL.PostgreSQL", "postgresql.cache", cache.ToJsonString());

            Assert.True(result.Applied, result.ToString());
            Assert.Equal(1234, context.Configuration.Get<CacheConfiguration>().MaxEntries);
        }
        finally
        {
            await Libraries.UpdateConfigAsync("CL.PostgreSQL", "postgresql.cache", original);
        }
    }
}
