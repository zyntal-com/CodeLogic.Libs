using CL.Data;
using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.MySql;
using CL.Data.Providers;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace Data.MySql.Tests;

public sealed class MySqlProviderTests
{
    private static readonly MySqlDataProvider Provider = MySqlDataProvider.Instance;

    [Fact]
    public void Connection_string_carries_settings_and_fixed_options()
    {
        var config = new DataConnectionConfig
        {
            Provider = "mysql", Host = "db1", Port = 3307, Database = "app", Username = "u", Password = "p",
            FailoverHosts = ["db2"], SslMode = DataSslMode.Require, MaxPoolSize = 50, CommandTimeoutSeconds = 12,
            MySql = new MySqlOptions { AllowLoadLocalInfile = true },
        };
        var cs = new MySqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());

        Assert.Equal("db1,db2", cs.Server);
        Assert.Equal(MySqlLoadBalance.FailOver, cs.LoadBalance);
        Assert.Equal((3307u, "app", "u", "p"), (cs.Port, cs.Database, cs.UserID, cs.Password));
        Assert.Equal(MySqlSslMode.Required, cs.SslMode);
        Assert.Equal((50u, 12u), (cs.MaximumPoolSize, cs.DefaultCommandTimeout));
        Assert.True(cs.UseAffectedRows);
        Assert.True(cs.AllowLoadLocalInfile);
        Assert.Equal(MySqlGuidFormat.Binary16, cs.GuidFormat);
        Assert.Equal(MySqlDateTimeKind.Utc, cs.DateTimeKind);

        var replica = NodeEndpoint.Replica(config, new ReplicaConfig { Name = "r1", Host = "db9" });
        Assert.Equal("db9", new MySqlConnectionStringBuilder(Provider.BuildConnectionString(config, replica).ValueOrThrow()).Server);
    }

    [Theory]
    [InlineData(ConnectionResetMode.WhenDirty, false, true)]
    [InlineData(ConnectionResetMode.Always, true, false)]
    [InlineData(ConnectionResetMode.Never, false, false)]
    public void Connection_reset_mode_decides_who_resets(ConnectionResetMode mode, bool driverResets, bool cldataResets)
    {
        // The typed option wins over a ConnectionReset key in the raw connection string.
        var config = new DataConnectionConfig
        {
            Provider = "mysql", ConnectionString = $"Server=db;ConnectionReset={!driverResets}",
            MySql = new MySqlOptions { ConnectionReset = mode, ConnectionIdleTimeoutSeconds = 60 },
        };
        var cs = new MySqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.Equal(driverResets, cs.ConnectionReset);
        Assert.Equal(60u, cs.ConnectionIdleTimeout);
        Assert.Equal(cldataResets, Provider.ResetsDirtySessions(config));
    }

    [Fact]
    public void Defaults_reset_only_dirty_sessions_and_never_without_pooling()
    {
        var config = new DataConnectionConfig { Provider = "mysql", Host = "db" };
        var cs = new MySqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.False(cs.ConnectionReset);
        Assert.Equal(180u, cs.ConnectionIdleTimeout);
        Assert.True(Provider.ResetsDirtySessions(config));

        config.Pooling = false;   // every open is a new session: nothing to reset
        Assert.False(Provider.ResetsDirtySessions(config));
    }

    [Theory]
    [InlineData("SELECT * FROM `orders` WHERE id = @p0 AND b IN (@p1, @p12)", false)]   // CL.Data's parameters, not user variables
    [InlineData("SELECT @p0x", true)]
    [InlineData("SELECT @p", true)]
    [InlineData("SELECT @@session.sql_mode", true)]
    [InlineData("SELECT @pid := 1", true)]
    [InlineData("SELECT * FROM orders WHERE id = 1", false)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", false)]
    [InlineData("TABLE orders", false)]
    [InlineData("SHOW TABLES", false)]
    [InlineData("EXPLAIN SELECT 1", false)]
    [InlineData("INSERT INTO t (a) VALUES (1) ON DUPLICATE KEY UPDATE a = VALUES(a)", false)]
    [InlineData("REPLACE INTO t VALUES (1)", false)]
    [InlineData("UPDATE t SET a = 1", false)]
    [InlineData("DELETE FROM t LIMIT 10", false)]
    [InlineData("CREATE TABLE t (id INT)", false)]
    [InlineData("ALTER TABLE t ADD c INT", false)]
    [InlineData("DROP TABLE IF EXISTS t", false)]
    [InlineData("TRUNCATE TABLE t", false)]
    [InlineData("OPTIMIZE TABLE t", false)]
    [InlineData("SELECT * FROM t FOR UPDATE", false)]               // row locks end with the statement or its transaction
    [InlineData("SET NAMES latin1", true)]
    [InlineData("SET SESSION sql_mode = ''", true)]
    [InlineData("SET autocommit = 0", true)]
    [InlineData("START TRANSACTION", true)]
    [InlineData("BEGIN", true)]
    [InlineData("CALL p()", true)]
    [InlineData("DO GET_LOCK('a', 0)", true)]
    [InlineData("SELECT GET_LOCK('a', 0)", true)]
    [InlineData("SELECT `GET_LOCK`('a', 0)", true)]
    [InlineData("SELECT `get_lock` FROM t", false)]
    [InlineData("SELECT LAST_INSERT_ID(5)", true)]
    [InlineData("CREATE TEMPORARY TABLE x (id INT)", true)]
    [InlineData("LOCK TABLES t WRITE", true)]
    [InlineData("USE other", true)]
    [InlineData("PREPARE s FROM 'SELECT 1'", true)]
    [InlineData("HANDLER t OPEN", true)]
    [InlineData("LOAD DATA LOCAL INFILE 'x' INTO TABLE t", true)]
    [InlineData("SELECT /*!40101 SQL_NO_CACHE */ 1", true)]
    [InlineData("SELECT 1; SELECT 2", true)]
    [InlineData("SELECT 1--1, GET_LOCK('a', 0)", true)]          // --1 is arithmetic in MySQL, not a comment
    [InlineData("SELECT 1 -- GET_LOCK('a', 0)", false)]          // a real comment
    [InlineData("SELECT 1 # GET_LOCK('a', 0)", false)]           // a real MySQL comment
    [InlineData("SELECT 1 AS $x$, GET_LOCK('a', 0) AS $x$", true)]   // $x$ is an identifier in MySQL, not a dollar quote
    [InlineData(@"SELECT 'a\'', GET_LOCK('x', 0), ''", true)]    // backslash escapes depend on sql_mode: ambiguous
    [InlineData(@"SELECT 'C:\path'", true)]
    public void Raw_sql_session_rules(string sql, bool dirty) => Assert.Equal(dirty, Provider.MayChangeSession(sql));

    [Fact]
    public void Replicas_given_a_host_inherit_the_primary_connection_string()
    {
        var config = new DataConnectionConfig
        {
            Provider = "mysql", ConnectionString = "Server=db1;Port=3307;Database=app;User ID=u;Password=p",
            Replicas = [new ReplicaConfig { Name = "r1", Host = "db9" }],
        };
        var replica = NodeEndpoint.Replica(config, config.Replicas[0]);
        var cs = new MySqlConnectionStringBuilder(Provider.BuildConnectionString(config, replica).ValueOrThrow());
        Assert.Equal(("db9", 3307u, "app", "u", "p"), (cs.Server, cs.Port, cs.Database, cs.UserID, cs.Password));
        Assert.DoesNotContain("p", replica.ToString().Replace("replica", "").Replace("primary", ""), StringComparison.Ordinal);
    }

    [Table]
    private sealed class Typed
    {
        [Column(Primary = true, AutoIncrement = true)] [MySqlColumn(Unsigned = true)] public long Id { get; set; }
        [Column(Size = 40)] public string Name { get; set; } = "";
        public bool Flag { get; set; }
        [Column(Precision = 10, Scale = 4)] public decimal Amount { get; set; }
        public Guid Ref { get; set; }
        public DateTimeOffset At { get; set; }
        [Column(DataType = DataType.Json)] public string? Doc { get; set; }
        [MySqlColumn(Type = "MEDIUMTEXT")] public string? Body { get; set; }
    }

    [Fact]
    public void Store_types()
    {
        var map = EntityAccessorRegistry.Get<Typed>().ValueOrThrow().Map;
        var config = new DataConnectionConfig();
        string Type(string property) => Provider.Types.GetStoreType(map.Property(property), config);

        Assert.Equal("BIGINT UNSIGNED", Type(nameof(Typed.Id)));
        Assert.Equal("VARCHAR(40)", Type(nameof(Typed.Name)));
        Assert.Equal("TINYINT(1)", Type(nameof(Typed.Flag)));
        Assert.Equal("DECIMAL(10,4)", Type(nameof(Typed.Amount)));
        Assert.Equal("BINARY(16)", Type(nameof(Typed.Ref)));
        Assert.Equal("DATETIME(6)", Type(nameof(Typed.At)));
        Assert.Equal("JSON", Type(nameof(Typed.Doc)));
        Assert.Equal("MEDIUMTEXT", Type(nameof(Typed.Body)));
    }

    [Fact]
    public void Errors_are_classified()
    {
        var timeout = Provider.Errors.Classify(new TimeoutException("slow"));
        Assert.True(timeout.IsTransient);
        Assert.Equal(CL.Data.Errors.DataErrors.TimeoutCode, timeout.Error.Code);
    }
}
