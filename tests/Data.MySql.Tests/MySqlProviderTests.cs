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
