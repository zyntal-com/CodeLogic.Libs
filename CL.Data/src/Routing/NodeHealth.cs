using CL.Common.Resilience;
using CL.Data.Execution;

namespace CL.Data.Routing;

/// <summary>The health of one node, as reported by <see cref="NodeHealthChecks.CheckHealthAsync"/>.</summary>
/// <param name="Node">Node name (<c>primary</c> or the replica name).</param>
/// <param name="IsPrimary">True for the primary.</param>
/// <param name="Reachable">True when the node answered the ping.</param>
/// <param name="Circuit">State of the node's circuit breaker (closed when it has none).</param>
/// <param name="LagMs">Replication lag from the last probe (replicas), null when unknown.</param>
/// <param name="Latency">Ping round trip.</param>
/// <param name="Error">Error code of a failed ping.</param>
public sealed record NodeHealth(string Node, bool IsPrimary, bool Reachable, CircuitState Circuit, long? LagMs, TimeSpan Latency, string? Error)
{
    /// <summary>True when the node answers, its circuit is not open and (for replicas) it is within <paramref name="maxLagMs"/>.</summary>
    public bool IsUsable(int maxLagMs) =>
        Reachable && Circuit != CircuitState.Open && (IsPrimary || maxLagMs <= 0 || LagMs is not { } lag || lag <= maxLagMs);
}

/// <summary>Per-node health checks.</summary>
public static class NodeHealthChecks
{
    /// <summary>
    /// Pings every node (bypassing routing and retries, so each node is seen as it is) and reports reachability, circuit
    /// state and lag. Run <see cref="ReplicaProbes.ProbeReplicasAsync"/> first for fresh lag values.
    /// </summary>
    public static async ValueTask<IReadOnlyList<NodeHealth>> CheckHealthAsync(this Database db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var nodes = db.Replicas.Prepend(db.Primary).ToArray();
        var checks = await Task.WhenAll(nodes.Select(async node =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var ping = await db.PingAsync(node, ct).ConfigureAwait(false);
            return new NodeHealth(node.Name, node.IsPrimary, ping.IsSuccess, node.Breaker?.State ?? CircuitState.Closed, node.LagMs,
                System.Diagnostics.Stopwatch.GetElapsedTime(started), ping.IsFailure ? ping.Error!.Code : null);
        })).ConfigureAwait(false);
        return checks;
    }
}
