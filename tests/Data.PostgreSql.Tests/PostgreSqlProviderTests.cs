using CL.Data;
using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.PostgreSql;
using CL.Data.Providers;
using CodeLogic.Core.Results;
using Npgsql;
using NpgsqlTypes;

namespace Data.PostgreSql.Tests;

public sealed class PostgreSqlProviderTests
{
    private static readonly PostgreSqlDataProvider Provider = PostgreSqlDataProvider.Instance;

    [Fact]
    public void Connection_string_carries_settings_and_failover()
    {
        var config = new DataConnectionConfig
        {
            Provider = "postgresql", Host = "pg1", Port = 5433, Database = "app", Username = "u", Password = "p",
            FailoverHosts = ["pg2"], SslMode = DataSslMode.VerifyFull, PrepareStatements = false,
            PostgreSql = new PostgreSqlOptions { Multiplexing = true },
        };
        var cs = new NpgsqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.Equal("pg1,pg2", cs.Host);
        Assert.Equal("primary", cs.TargetSessionAttributes);
        Assert.Equal((5433, "app", "u"), (cs.Port, cs.Database, cs.Username));
        Assert.Equal(SslMode.VerifyFull, cs.SslMode);
        Assert.Equal(0, cs.MaxAutoPrepare);
        Assert.True(cs.Multiplexing);
        Assert.Equal("public", Provider.GetDefaultSchema(config));
    }

    [Table]
    private sealed class Typed
    {
        [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
        public bool Flag { get; set; }
        public Guid Ref { get; set; }
        public DateTimeOffset At { get; set; }
        [Column(DataType = DataType.Json)] public string? Doc { get; set; }
        [PostgresColumn(Type = "inet")] public string? Address { get; set; }
    }

    [Fact]
    public void Store_types_and_typed_parameters()
    {
        var map = EntityAccessorRegistry.Get<Typed>().ValueOrThrow().Map;
        var config = new DataConnectionConfig();
        string Type(string p) => Provider.Types.GetStoreType(map.Property(p), config);
        Assert.Equal("bigserial", Type(nameof(Typed.Id)));
        Assert.Equal("boolean", Type(nameof(Typed.Flag)));
        Assert.Equal("uuid", Type(nameof(Typed.Ref)));
        Assert.Equal("timestamptz(6)", Type(nameof(Typed.At)));
        Assert.Equal("jsonb", Type(nameof(Typed.Doc)));
        Assert.Equal("inet", Type(nameof(Typed.Address)));

        var json = (NpgsqlParameter)Provider.Types.CreateParameter("@p0", "{}", map.Property(nameof(Typed.Doc)));
        Assert.Equal(NpgsqlDbType.Jsonb, json.NpgsqlDbType);
        var at = (NpgsqlParameter<DateTimeOffset>)Provider.Types.CreateParameter("@p1", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), null);
        Assert.Equal(TimeSpan.Zero, at.TypedValue.Offset);
    }
}
