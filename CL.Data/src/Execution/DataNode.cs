using System.Data.Common;
using CL.Data.Providers;

namespace CL.Data.Execution;

/// <summary>One server of a connection (the primary or a replica) with its pooled data source.</summary>
public sealed class DataNode : IAsyncDisposable
{
    internal DataNode(NodeEndpoint endpoint, DbDataSource dataSource)
    {
        Endpoint = endpoint;
        DataSource = dataSource;
    }

    /// <summary><c>primary</c> or the replica name.</summary>
    public string Name => Endpoint.Name;

    /// <summary>True for the primary.</summary>
    public bool IsPrimary => Endpoint.IsPrimary;

    /// <summary>Endpoint settings.</summary>
    public NodeEndpoint Endpoint { get; }

    /// <summary>The driver data source (connection pool) of this node.</summary>
    public DbDataSource DataSource { get; }

    /// <summary>
    /// The node's circuit breaker (null when <c>CircuitBreakerThreshold</c> is 0). It opens after repeated node failures
    /// (unreachable, connection lost); commands then fail fast with <c>data.circuit_open</c> until a trial call succeeds.
    /// </summary>
    public CL.Common.Resilience.CircuitBreaker? Breaker { get; internal set; }

    /// <summary>True unless the node's circuit is open.</summary>
    public bool IsHealthy => Breaker is null || Breaker.State != CL.Common.Resilience.CircuitState.Open;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    /// <inheritdoc />
    public override string ToString() => Name;
}
