using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Data.Configuration;
using CL.Data.Routing;
using CL.Data.Schema;

namespace Data.Tests.Configuration;

public sealed class DataConfigTests
{
    // Same settings as CodeLogic's ConfigurationManager (camelCase, enums as camelCase strings).
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static DataConfig Valid() => new()
    {
        DefaultConnection = "main",
        Connections =
        {
            ["main"] = new() { Provider = "mysql", Host = "db", Database = "app", Username = "u", Password = "p" },
            ["local"] = new() { Provider = "sqlite", Database = ":memory:" },
        },
    };

    private static IReadOnlyList<string> PathsOf(DataConfig config) =>
        config.Validate().FieldErrors.Select(e => e.Path).ToList();

    [Fact]
    public void Valid_configuration_passes()
    {
        var result = Valid().Validate();
        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Default_connection_must_exist()
    {
        var config = Valid();
        config.DefaultConnection = "nope";
        Assert.Contains("defaultConnection", PathsOf(config));
    }

    [Fact]
    public void Provider_and_location_are_required()
    {
        var config = Valid();
        config.Connections["bad"] = new();
        var paths = PathsOf(config);
        Assert.Contains("connections.bad.provider", paths);
        Assert.Contains("connections.bad.database", paths);
        Assert.Contains("connections.bad.host", paths);
    }

    [Fact]
    public void Sqlite_needs_no_host_and_a_connection_string_replaces_the_fields()
    {
        var config = Valid();
        config.Connections["cs"] = new() { Provider = "postgresql", ConnectionString = "Host=x;Database=y" };
        Assert.True(config.Validate().IsValid);
    }

    [Fact]
    public void Disabled_connections_are_not_validated()
    {
        var config = Valid();
        config.Connections["off"] = new() { Enabled = false };
        Assert.True(config.Validate().IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Port_out_of_range_is_reported(int port)
    {
        var config = Valid();
        config.Connections["main"].Port = port;
        Assert.Contains("connections.main.port", PathsOf(config));
    }

    [Fact]
    public void Pool_sizes_are_checked()
    {
        var config = Valid();
        config.Connections["main"].MinPoolSize = 10;
        config.Connections["main"].MaxPoolSize = 5;
        Assert.Contains("connections.main.maxPoolSize", PathsOf(config));
    }

    [Fact]
    public void Replicas_are_checked()
    {
        var config = Valid();
        config.Connections["main"].Replicas =
        [
            new() { Name = "r1", Host = "h1" },
            new() { Name = "R1", Host = "h2" },          // duplicate (case-insensitive)
            new() { Name = "primary", Host = "h3" },     // reserved
            new() { Name = "r4" },                       // no host
            new() { Name = "r5", Host = "h5", Weight = 0 },
        ];
        var paths = PathsOf(config);
        Assert.Contains("connections.main.replicas[1].name", paths);
        Assert.Contains("connections.main.replicas[2].name", paths);
        Assert.Contains("connections.main.replicas[3].host", paths);
        Assert.Contains("connections.main.replicas[4].weight", paths);
    }

    [Fact]
    public void Sqlite_cannot_have_replicas_and_replica_only_needs_a_replica()
    {
        var config = Valid();
        config.Connections["local"].Replicas = [new() { Name = "r", Host = "h" }];
        config.Connections["main"].ReadPreference = ReadPreference.ReplicaOnly;
        var paths = PathsOf(config);
        Assert.Contains("connections.local.replicas", paths);
        Assert.Contains("connections.main.readPreference", paths);
    }

    [Fact]
    public void Sync_level_follows_the_mode_unless_overridden()
    {
        var c = new DataConnectionConfig { SyncMode = SyncMode.Developer };
        Assert.Equal(SchemaSyncLevel.Full, c.EffectiveSyncLevel);
        c.SyncMode = SyncMode.Production;
        Assert.Equal(SchemaSyncLevel.Safe, c.EffectiveSyncLevel);
        c.SyncMode = SyncMode.Migration;
        Assert.Equal(SchemaSyncLevel.None, c.EffectiveSyncLevel);
        c.SchemaSyncLevel = SchemaSyncLevel.Additive;
        Assert.Equal(SchemaSyncLevel.Additive, c.EffectiveSyncLevel);
    }

    [Fact]
    public void Json_round_trip_keeps_values_and_case_insensitive_names()
    {
        var config = Valid();
        config.Connections["main"].Replicas = [new() { Name = "r1", Host = "db2", Weight = 3 }];
        config.Connections["main"].MySql = new() { AllowLoadLocalInfile = true };
        config.Connections["main"].SyncMode = SyncMode.Migration;
        config.Connections["main"].Options["custom"] = "1";

        var json = JsonSerializer.Serialize(config, Json);
        Assert.Contains("\"syncMode\": \"migration\"", json);

        var back = JsonSerializer.Deserialize<DataConfig>(json, Json)!;
        Assert.True(back.Validate().IsValid, back.Validate().ToString());
        Assert.True(back.Connections.ContainsKey("MAIN"));                    // comparer survives deserialization
        Assert.Equal("1", back.Connections["main"].Options["CUSTOM"]);
        Assert.Equal(3, back.Connections["main"].Replicas[0].Weight);
        Assert.True(back.Connections["main"].MySql!.AllowLoadLocalInfile);
        Assert.Equal(SyncMode.Migration, back.Connections["main"].SyncMode);
    }

    [Fact]
    public void Cache_config_validation()
    {
        Assert.True(new DataCacheConfig().Validate().IsValid);
        var bad = new DataCacheConfig { DefaultTtlSeconds = 0, BusPollIntervalMs = 10, MaxEntries = -1 };
        var paths = bad.Validate().FieldErrors.Select(e => e.Path).ToList();
        Assert.Contains("defaultTtlSeconds", paths);
        Assert.Contains("busPollIntervalMs", paths);
        Assert.Contains("maxEntries", paths);
    }

    [Fact]
    public void Enabled_connections_respect_the_master_switch()
    {
        var config = Valid();
        Assert.Equal(2, config.EnabledConnections.Count());
        config.Enabled = false;
        Assert.Empty(config.EnabledConnections);
    }
}
