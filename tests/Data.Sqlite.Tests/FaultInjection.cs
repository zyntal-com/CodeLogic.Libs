using System.Collections.Concurrent;
using System.Data.Common;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Providers;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace Data.Sqlite.Tests;

/// <summary>A failure the faulty classifier reports as transient.</summary>
internal sealed class FaultException(string message) : DbException(message);

/// <summary>The real SQLite data source of one node, failing the next <see cref="OpenFaults"/> connection opens.</summary>
internal sealed class FaultySource(DbDataSource inner) : DbDataSource
{
    public int OpenFaults;
    public int Opens;

    public override string ConnectionString => inner.ConnectionString;
    protected override DbConnection CreateDbConnection() => inner.CreateConnection();

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Opens);
        if (Interlocked.Decrement(ref OpenFaults) >= 0) throw new FaultException("cl_fault_open: server unreachable");
        return await inner.OpenConnectionAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>SQLite's classifier plus the injected faults: unreachable (open), deadlock and an unknown transient error.</summary>
internal sealed class FaultyClassifier : IErrorClassifier
{
    public ErrorClassification Classify(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("cl_fault_open", StringComparison.Ordinal))
            return new(DataErrors.Unavailable("unreachable", message), true);
        if (message.Contains("cl_fault_deadlock", StringComparison.Ordinal))
            return new(DataErrors.Deadlock(message), true);
        if (message.Contains("cl_fault_transient", StringComparison.Ordinal))
            return new(DataErrors.ProviderError("connection reset", message), true);
        return SqliteDataProvider.Instance.Errors.Classify(exception);
    }
}

/// <summary>
/// SQLite posing as a server provider for resilience and routing tests: every node (primary, replicas) is its own database
/// file (<c>{Database}</c> for the primary, <c>{Database}.{replica}</c> for replicas) behind a <see cref="FaultySource"/>.
/// </summary>
internal sealed class FaultyProvider : DataProvider
{
    private static readonly SqliteDataProvider Inner = SqliteDataProvider.Instance;
    public ConcurrentDictionary<string, FaultySource> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FaultySource Source => Nodes[NodeEndpoint.PrimaryName];

    public override string Name => "faulty";
    public override ProviderCapabilities Capabilities => Inner.Capabilities;
    public override SqlDialect Dialect => Inner.Dialect;
    public override ITypeMapper Types => Inner.Types;
    public override IErrorClassifier Errors { get; } = new FaultyClassifier();
    public override object? ReadLastInsertId(DbCommand command) => Inner.ReadLastInsertId(command);
    public override CL.Data.Routing.ReplicaProbe ReplicaProbe { get; } = new TableProbe();

    public override Result<string> BuildConnectionString(DataConnectionConfig config, NodeEndpoint node) =>
        new SqliteConnectionStringBuilder { DataSource = node.IsPrimary ? config.Database : $"{config.Database}.{node.Name}" }.ToString();

    public override DbDataSource CreateDataSource(DataConnectionConfig config, NodeEndpoint node, string connectionString) =>
        Nodes[node.Name] = new FaultySource(Inner.CreateDataSource(config, node, connectionString));
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Reads a node's "lag" from its own <c>lag</c> table, so tests set each replica's lag with an UPDATE.</summary>
internal sealed class TableProbe : CL.Data.Routing.ReplicaProbe
{
    public override string Sql => "SELECT ms FROM lag";

    public override ValueTask<long?> ReadLagMsAsync(DbDataReader reader, CancellationToken ct) =>
        ValueTask.FromResult<long?>(reader.IsDBNull(0) ? Broken : reader.GetInt64(0));
}
