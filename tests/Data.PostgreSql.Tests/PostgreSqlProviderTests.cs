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

    [Theory]
    [InlineData(ConnectionResetMode.WhenDirty, false, true, true)]
    [InlineData(ConnectionResetMode.Always, false, false, false)]
    [InlineData(ConnectionResetMode.Never, false, true, false)]
    [InlineData(ConnectionResetMode.WhenDirty, true, false, false)]   // multiplexing: Npgsql's default, no tracking
    public void Connection_reset_mode_decides_who_resets(ConnectionResetMode mode, bool multiplexing, bool noResetOnClose, bool cldataResets)
    {
        var config = new DataConnectionConfig
        {
            Provider = "postgresql", ConnectionString = $"Host=db;No Reset On Close={!noResetOnClose}",
            PostgreSql = new PostgreSqlOptions { ConnectionReset = mode, Multiplexing = multiplexing },
        };
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.Equal(noResetOnClose, cs.NoResetOnClose);
        Assert.Equal(cldataResets, Provider.ResetsDirtySessions(config));
    }

    [Theory]
    [InlineData("SELECT * FROM t WHERE id = @p0", false)]
    [InlineData("SELECT data FROM t WHERE data @> '{\"a\":1}'", false)]     // @> is an operator, not a variable
    [InlineData("SELECT $$SET x$$", false)]                                // a dollar-quoted string
    [InlineData("INSERT INTO t (a) VALUES (1) ON CONFLICT DO NOTHING", false)]
    [InlineData("SELECT pg_notify('c', 'p')", false)]
    [InlineData("SET search_path TO x", true)]
    [InlineData("SELECT set_config('a', 'b', false)", true)]
    [InlineData("SELECT pg_advisory_lock(1)", true)]
    [InlineData("CREATE TEMP TABLE x (id int)", true)]
    [InlineData("BEGIN", true)]
    [InlineData("LISTEN c", true)]
    [InlineData("PREPARE s AS SELECT 1", true)]
    [InlineData("CREATE TABLE pg_temp.scratch (id int)", true)]
    [InlineData("SELECT pg_temp_count FROM stats", false)]
    [InlineData("DO $$BEGIN END$$", true)]
    [InlineData("SELECT 1; SELECT 2", true)]
    public void Raw_sql_session_rules(string sql, bool dirty) => Assert.Equal(dirty, Provider.MayChangeSession(sql));

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
