using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CodeLogic;
using CL.MSSQL;
using CL.MSSQL.Configuration;
using CL.MSSQL.Models;
using Xunit;

namespace MSSQL.Tests;

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

    private readonly MSSQLRuntimeFixture _fx;

    public LiveConfigReloadTests(MSSQLRuntimeFixture fx) => _fx = fx;

    private MSSQLLibrary Sql => _fx.Mysql;

    private static DatabaseConfiguration CurrentConfig() =>
        CodeLogic.CodeLogic.GetLibraryManager()!.GetLoadedLibraries()
            .Single(l => l.Manifest.Id == "CL.MSSQL").Context!.Configuration.Get<DatabaseConfiguration>();

    private static async Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateAsync(JsonNode config) =>
        await Libraries.UpdateConfigAsync("CL.MSSQL", "mssql", config.ToJsonString(), changedBy: "test");

    [DbFact]
    public async Task Changing_one_database_leaves_the_others_untouched()
    {
        var original = JsonSerializer.Serialize(CurrentConfig(), Json);
        var defaultBefore = Sql.ConnectionManager.GetConfiguration("Default");
        Assert.NotNull(defaultBefore);

        try
        {
            var config = JsonNode.Parse(original)!;
            var databases = config["databases"]!.AsObject();

            // Add a database → it connects; Default is not re-registered.
            databases["Reload"] = databases["Default"]!.DeepClone();
            var added = await UpdateAsync(config);
            Assert.True(added.Applied, added.ToString());
            Assert.Contains("Reload", Sql.ConnectionManager.GetConnectionIds());
            Assert.True((await Sql.TestConnectionAsync("Reload")).Value);
            Assert.Same(defaultBefore, Sql.ConnectionManager.GetConfiguration("Default"));

            // A runtime sync mode survives an unrelated change to the same database.
            Sql.SetSyncMode(SyncMode.Migration, "Reload");

            // Non-connection setting → updated in place, no reconnect.
            databases["Reload"]!["slowQueryThresholdMs"] = 4321;
            var tuned = await UpdateAsync(config);
            Assert.True(tuned.Applied, tuned.ToString());
            Assert.Equal(4321, Sql.ConnectionManager.GetConfiguration("Reload")!.SlowQueryThresholdMs);
            Assert.Equal(SyncMode.Migration, CurrentConfig().Databases["Reload"].SyncMode);
            Assert.Same(defaultBefore, Sql.ConnectionManager.GetConfiguration("Default"));

            // Unreachable connection settings → saved but reported as not applied; Default keeps working.
            // The fixture uses a raw connectionString (it wins over host/port), so break that.
            databases["Reload"]!["connectionString"] = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(DbEnv.ConnectionString)
            {
                DataSource = "127.0.0.1,1",
                ConnectTimeout = 2
            }.ConnectionString;
            var broken = await UpdateAsync(config);
            Assert.True(broken.IsValid);
            Assert.False(broken.Applied);
            Assert.NotNull(broken.ApplyError);
            Assert.Same(defaultBefore, Sql.ConnectionManager.GetConfiguration("Default"));
            Assert.True((await Sql.TestConnectionAsync("Default")).Value);

            // Remove it → disconnected and unregistered; Default still untouched.
            databases.Remove("Reload");
            var removed = await UpdateAsync(config);
            Assert.True(removed.Applied, removed.ToString());
            Assert.DoesNotContain("Reload", Sql.ConnectionManager.GetConnectionIds());
            Assert.Same(defaultBefore, Sql.ConnectionManager.GetConfiguration("Default"));
        }
        finally
        {
            await Libraries.UpdateConfigAsync("CL.MSSQL", "mssql", original, changedBy: "test");
        }
    }

    [DbFact]
    public async Task Cache_section_changes_apply_live()
    {
        var context = CodeLogic.CodeLogic.GetLibraryManager()!.GetLoadedLibraries()
            .Single(l => l.Manifest.Id == "CL.MSSQL").Context!;
        var original = JsonSerializer.Serialize(context.Configuration.Get<CacheConfiguration>(), Json);

        try
        {
            var cache = JsonNode.Parse(original)!;
            cache["maxEntries"] = 1234;
            var result = await Libraries.UpdateConfigAsync("CL.MSSQL", "mssql.cache", cache.ToJsonString());

            Assert.True(result.Applied, result.ToString());
            Assert.Equal(1234, context.Configuration.Get<CacheConfiguration>().MaxEntries);
        }
        finally
        {
            await Libraries.UpdateConfigAsync("CL.MSSQL", "mssql.cache", original);
        }
    }
}
