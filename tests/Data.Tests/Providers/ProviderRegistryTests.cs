using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Tests.Providers;

internal sealed class FakeDialect : SqlDialect;

internal sealed class FakeProvider(string name = "fake", int spiVersion = CL.Data.DataPackage.SpiVersion) : DataProvider
{
    public override string Name => name;
    public override int SpiVersion => spiVersion;
    public override ProviderCapabilities Capabilities { get; } = new();
    public override SqlDialect Dialect { get; } = new FakeDialect();
    public override ITypeMapper Types => throw new NotSupportedException();
    public override IErrorClassifier Errors => throw new NotSupportedException();
    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node) => $"Host={node.Host}";
    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) => throw new NotSupportedException();
}

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Registered_provider_resolves_case_insensitively()
    {
        Assert.True(DataProviderRegistry.Register(new FakeProvider("fake-a")).IsSuccess);
        try
        {
            Assert.True(DataProviderRegistry.IsRegistered("FAKE-A"));
            Assert.Equal("fake-a", DataProviderRegistry.Get(" Fake-A ").ValueOrThrow().Name);
            Assert.Contains("fake-a", DataProviderRegistry.Names);
        }
        finally
        {
            DataProviderRegistry.Unregister("fake-a");
        }
        Assert.False(DataProviderRegistry.IsRegistered("fake-a"));
    }

    [Fact]
    public void Missing_provider_names_the_package_to_install()
    {
        var result = DataProviderRegistry.Get("nope");
        Assert.Equal(DataErrors.ProviderMissingCode, result.Error!.Code);
        Assert.Contains("CodeLogic.Data", result.Error.Message);
    }

    [Fact]
    public void Provider_for_another_contract_version_is_rejected()
    {
        var result = DataProviderRegistry.Register(new FakeProvider("fake-old", spiVersion: 999));
        Assert.Equal(DataErrors.ProviderIncompatibleCode, result.Error!.Code);
        Assert.False(DataProviderRegistry.IsRegistered("fake-old"));
    }

    [Fact]
    public void Replica_endpoints_fall_back_to_primary_settings()
    {
        var config = new DataConnectionConfig { Provider = "fake", Host = "db1", Port = 3307, Username = "u", Password = "p" };
        var primary = NodeEndpoint.Primary(config);
        Assert.True(primary.IsPrimary);
        Assert.Equal("primary", primary.Name);

        var replica = NodeEndpoint.Replica(config, new ReplicaConfig { Name = "r1", Host = "db2", Weight = 4 });
        Assert.False(replica.IsPrimary);
        Assert.Equal(("db2", 3307, "u", "p", 4), (replica.Host, replica.Port, replica.Username, replica.Password, replica.Weight));
    }

    [Fact]
    public void Endpoints_never_print_secrets()
    {
        var config = new DataConnectionConfig { Provider = "fake", ConnectionString = "Server=db1;Password=cs-secret", Host = "db1", Port = 3307, Username = "u", Password = "pw-secret" };
        foreach (var endpoint in new[] { NodeEndpoint.Primary(config), NodeEndpoint.Replica(config, new ReplicaConfig { Name = "r1", Host = "db2" }) })
        {
            Assert.DoesNotContain("secret", endpoint.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret", $"{endpoint}", StringComparison.Ordinal);
        }
        Assert.Equal("primary (primary, db1:3307)", NodeEndpoint.Primary(config).ToString());
    }

    [Fact]
    public void Replica_endpoints_given_a_host_keep_the_primary_connection_string()
    {
        var config = new DataConnectionConfig { Provider = "fake", ConnectionString = "Server=db1;Database=app" };
        var replica = NodeEndpoint.Replica(config, new ReplicaConfig { Name = "r1", Host = "db2" });
        Assert.Equal(("Server=db1;Database=app", "db2"), (replica.ConnectionString, replica.Host));
    }

    [Fact]
    public void Dialect_quotes_identifiers_and_escapes_like()
    {
        var dialect = new FakeDialect();
        Assert.Equal("\"users\"", dialect.QuoteIdentifier("users"));
        Assert.Equal("\"a\"\"b\"", dialect.QuoteIdentifier("a\"b"));
        Assert.Equal("\"app\".\"users\"", dialect.QuoteTable("users", "app"));
        Assert.Equal("@p12", dialect.ParameterName(12));
        Assert.Equal("50\\% off\\_now", dialect.EscapeLike("50% off_now"));
        Assert.Equal("plain", dialect.EscapeLike("plain"));
    }
}
