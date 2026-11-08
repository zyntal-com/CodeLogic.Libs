using System.Data.Common;
using CL.Data.Providers;

namespace CL.Data.Execution;

/// <summary>One server of a connection (the primary or a replica) with its pooled data source.</summary>
public sealed class DataNode : IAsyncDisposable
{
    internal DataNode(NodeEndpoint endpoint, DbDataSource dataSource, DataProvider? provider = null,
        CL.Data.Configuration.DataConnectionConfig? config = null, string? database = null)
    {
        Endpoint = endpoint;
        DataSource = dataSource;
        if (provider is not null && config is not null && provider.ResetsDirtySessions(config))
        {
            _provider = provider;
            _config = config;
            _tags = [new("db.namespace", database), new("cl.data.node", endpoint.Name)];
        }
    }

    private readonly KeyValuePair<string, object?>[]? _tags;

    // Set only when CL.Data resets dirty sessions itself (MySQL ConnectionReset = WhenDirty).
    private readonly DataProvider? _provider;
    private readonly CL.Data.Configuration.DataConnectionConfig? _config;

    /// <summary>True when CL.Data tracks dirty sessions on this node and resets them before they return to the pool.</summary>
    internal bool TracksSessions => _provider is not null;

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

    /// <summary>
    /// Replication lag in milliseconds (replicas); null when unknown. Set by the replica probe; hosts with their own
    /// monitoring may set it too. Routing skips replicas lagging more than <c>MaxReplicaLagMs</c>.
    /// </summary>
    public long? LagMs { get; set; }

    /// <summary>True unless the node's circuit is open.</summary>
    public bool IsHealthy => Breaker is null || Breaker.State != CL.Common.Resilience.CircuitState.Open;

    /// <summary>
    /// Returns a pooled connection opened from <see cref="DataSource"/>. When the node tracks sessions and the connection is
    /// <paramref name="dirty"/> (or was marked through <see cref="SessionTracker"/>), its session is reset first; after a
    /// failed reset the provider makes sure the session is not reused as it is. Never throws.
    /// </summary>
    internal ValueTask ReturnAsync(DbConnection connection, bool dirty = false, bool handedToUser = false)
    {
        if (_provider is null) return connection.DisposeAsync();
        var mark = SessionTracker.Take(connection);
        dirty |= mark != SessionMark.None;
        handedToUser |= mark == SessionMark.HandedToUser;
        return dirty && connection.State == System.Data.ConnectionState.Open
            ? ResetAndReturnAsync(connection, handedToUser)
            : connection.DisposeAsync();   // clean, or broken (the driver drops a broken session)
    }

    private async ValueTask ResetAndReturnAsync(DbConnection connection, bool handedToUser)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_config!.CommandTimeoutSeconds, 1, 30));
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _provider!.ResetSessionAsync(connection, _config, handedToUser, cts.Token).ConfigureAwait(false);
            CL.Data.Telemetry.DataTelemetry.SessionResets.Add(1, _tags!);
        }
        catch
        {
            CL.Data.Telemetry.DataTelemetry.SessionResetFailures.Add(1, _tags!);
            if (connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    await _provider!.DiscardSessionAsync(connection, _config, cts.Token).ConfigureAwait(false);
                }
                catch { /* a session that cannot be talked to is dropped by the driver */ }
            }
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch { /* returning a connection must not throw */ }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => DataSource.DisposeAsync();

    /// <inheritdoc />
    public override string ToString() => Name;
}
