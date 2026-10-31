using System.Data.Common;
using CL.Data.Errors;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace CL.Data.Routing;

/// <summary>
/// Measures how far a replica is behind its primary. Each server provider supplies one (<c>DataProvider.ReplicaProbe</c>);
/// SQLite has none.
/// </summary>
public abstract class ReplicaProbe
{
    /// <summary>The query run on the replica.</summary>
    public abstract string Sql { get; }

    /// <summary>
    /// Reads the lag in milliseconds from the query's result: 0 when caught up, <see cref="Broken"/> when replication is
    /// stopped, null when the server reports nothing (not set up as a replica; routing then treats it as usable).
    /// </summary>
    public abstract ValueTask<long?> ReadLagMsAsync(DbDataReader reader, CancellationToken ct);

    /// <summary>Lag reported when replication is configured but not running; always above any lag limit.</summary>
    public const long Broken = long.MaxValue;

    /// <summary>Reads column <paramref name="name"/> of the current row as a number, or null (missing column, NULL).</summary>
    protected static double? Number(DbDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) continue;
            return reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        }
        return null;
    }
}

/// <summary>Probes the replicas of a database, updating <see cref="DataNode.LagMs"/> and feeding their circuit breakers.</summary>
public static class ReplicaProbes
{
    /// <summary>
    /// Probes every replica once. A replica that cannot be reached counts as a node failure for its breaker; a successful
    /// probe counts as a success (so an open circuit closes again once the replica is back). Replicas whose circuit is
    /// open are skipped until it lets a trial call through. Returns the replicas probed successfully.
    /// </summary>
    public static async ValueTask<int> ProbeReplicasAsync(this Database db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Provider.ReplicaProbe is not { } probe) return 0;
        var probed = 0;
        foreach (var node in db.Replicas)
        {
            CL.Common.Resilience.CircuitLease lease = default;
            if (node.Breaker is { } breaker && !breaker.TryAcquire(out lease)) continue;

            var lag = await db.Executor.ExecuteAsync(node, new CommandSpec(probe.Sql, []), probe,
                static async (cmd, p, token) =>
                {
                    await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false) ? await p.ReadLagMsAsync(reader, token).ConfigureAwait(false) : null;
                }, null, null, ct).ConfigureAwait(false);

            if (lag.IsSuccess)
            {
                node.LagMs = lag.Value;
                lease.Success();
                probed++;
            }
            else if (lag.Error!.Code == DataErrors.UnavailableCode)
            {
                node.LagMs = null;
                lease.Failure();
            }
            else
            {
                // Reachable, but the probe itself failed (missing privilege, unsupported view): say nothing about the lag.
                node.LagMs = null;
                lease.Ignore();
            }
        }
        return probed;
    }

    /// <summary>Probes the replicas on <paramref name="scheduler"/> every <c>ReplicaProbeIntervalSeconds</c>; no-op without replicas.</summary>
    public static IDisposable? StartReplicaProbes(this Database db, DataScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scheduler);
        if (db.Replicas.Count == 0 || db.Provider.ReplicaProbe is null) return null;
        return scheduler.Every($"replica-probe-{db.Name}", TimeSpan.FromSeconds(db.Config.ReplicaProbeIntervalSeconds),
            async token => await db.ProbeReplicasAsync(token).ConfigureAwait(false));
    }
}
