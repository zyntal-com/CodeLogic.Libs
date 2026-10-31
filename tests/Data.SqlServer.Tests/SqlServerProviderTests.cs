using System.Data;
using CL.Data;
using CL.Data.Configuration;
using CL.Data.Mapping;
using CL.Data.Providers;
using CL.Data.SqlServer;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;

namespace Data.SqlServer.Tests;

public sealed class SqlServerProviderTests
{
    private static readonly SqlServerDataProvider Provider = SqlServerDataProvider.Instance;

    [Fact]
    public void Connection_string_carries_settings_and_replica_intent()
    {
        var config = new DataConnectionConfig
        {
            Provider = "sqlserver", Host = "sql1", Port = 1444, Database = "app", Username = "u", Password = "p",
            SqlServer = new SqlServerOptions { MultiSubnetFailover = true, ApplicationName = "cl-data" },
        };
        var primary = new SqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.Equal("sql1,1444", primary.DataSource);
        Assert.Equal(("app", "u"), (primary.InitialCatalog, primary.UserID));
        Assert.True(primary.MultiSubnetFailover);
        Assert.Equal(ApplicationIntent.ReadWrite, primary.ApplicationIntent);

        var replica = new SqlConnectionStringBuilder(Provider.BuildConnectionString(config,
            NodeEndpoint.Replica(config, new ReplicaConfig { Name = "r", Host = "listener" })).ValueOrThrow());
        Assert.Equal(ApplicationIntent.ReadOnly, replica.ApplicationIntent);

        config.SqlServer!.Authentication = SqlServerAuthentication.EntraManagedIdentity;
        var entra = new SqlConnectionStringBuilder(Provider.BuildConnectionString(config, NodeEndpoint.Primary(config)).ValueOrThrow());
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity, entra.Authentication);
    }

    [Table]
    private sealed class Typed
    {
        [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
        [Column(Size = 50)] public string Name { get; set; } = "";
        public string? Notes { get; set; }
        public bool Flag { get; set; }
        public DateTime When { get; set; }
        public DateTimeOffset At { get; set; }
        [ConcurrencyToken] [Column(DataType = DataType.RowVersion)] public byte[] Stamp { get; set; } = [];
    }

    [Fact]
    public void Store_types_and_parameters()
    {
        var map = EntityAccessorRegistry.Get<Typed>().ValueOrThrow().Map;
        var config = new DataConnectionConfig();
        string Type(string p) => Provider.Types.GetStoreType(map.Property(p), config);
        Assert.Equal("nvarchar(50)", Type(nameof(Typed.Name)));
        Assert.Equal("bit", Type(nameof(Typed.Flag)));
        Assert.Equal("datetime2(7)", Type(nameof(Typed.When)));
        Assert.Equal("datetimeoffset(7)", Type(nameof(Typed.At)));
        Assert.Equal("rowversion", Type(nameof(Typed.Stamp)));

        var when = (SqlParameter)Provider.Types.CreateParameter("@p0", DateTime.UtcNow, null);
        Assert.Equal(SqlDbType.DateTime2, when.SqlDbType);
        var text = (SqlParameter)Provider.Types.CreateParameter("@p1", "short", null);
        Assert.Equal(4000, text.Size);                                           // stable plan-cache bucket
    }

    [Fact]
    public void Failover_hosts_are_rejected_in_favour_of_listeners()
    {
        var config = new DataConnectionConfig { Provider = "sqlserver", Host = "a", Database = "d", FailoverHosts = ["b"] };
        List<CodeLogic.Core.Configuration.ConfigFieldError> errors = [];
        Provider.Validate(config, "c", errors);
        Assert.Contains(errors, e => e.Path == "c.failoverHosts");
    }
}
