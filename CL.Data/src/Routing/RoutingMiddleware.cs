using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CodeLogic.Core.Results;

namespace CL.Data.Routing;

/// <summary>Routing state shared by a database and its views: when the last write happened (reads-your-writes).</summary>
internal sealed class RoutingState
{
    public long LastWriteTicks;
}

/// <summary>
/// Picks the node for each command, just outside the resilience step (so retries stay on the chosen node).
/// <list type="bullet">
/// <item>Writes, transactions and anything a middleware already routed (<see cref="CommandContext.Node"/>) are left alone:
/// writes and transactions run on the primary.</item>
/// <item>Reads follow the read preference: per query (<c>ReadFrom</c>), per view (<see cref="Database.ReadFrom"/>), then
/// the connection's <c>ReadPreference</c>. For <c>StickyAfterWriteMs</c> after a write, reads stay on the primary so
/// callers see their own writes.</item>
/// <item>A replica is chosen at random by weight among the healthy ones (circuit not open) within <c>MaxReplicaLagMs</c>.
/// None available: <c>PreferReplica</c> falls back to the primary, <c>ReplicaOnly</c> fails with <c>data.no_healthy_node</c>.</item>
/// <item>A read that fails because its replica is down (unreachable, circuit open) runs once more on the primary under
/// <c>PreferReplica</c>.</item>
/// </list>
/// Only added to the pipeline when the connection has replicas.
/// </summary>
internal sealed class RoutingMiddleware(RoutingState state, TimeProvider time) : ICommandMiddleware
{
    public async ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next)
    {
        var db = context.Database;
        if (context.IsWrite || context.InTransaction)
        {
            var written = await next.InvokeAsync(context).ConfigureAwait(false);
            if (context.IsWrite) Interlocked.Exchange(ref state.LastWriteTicks, time.GetUtcNow().UtcTicks);
            return written;
        }
        if (context.Node is not null) return await next.InvokeAsync(context).ConfigureAwait(false);

        var preference = context.ReadFrom ?? db.ReadPreferenceOverride ?? db.Config.ReadPreference;
        if (preference == ReadPreference.Primary || (preference == ReadPreference.PreferReplica && Sticky(db)))
            return await next.InvokeAsync(context).ConfigureAwait(false);

        var replica = Pick(db);
        if (replica is null)
            return preference == ReadPreference.ReplicaOnly
                ? DataErrors.NoHealthyNode(db.Name, "No replica is healthy and within the lag limit.")
                : await next.InvokeAsync(context).ConfigureAwait(false);

        context.Node = replica;
        var result = await next.InvokeAsync(context).ConfigureAwait(false);
        if (result.IsSuccess || preference != ReadPreference.PreferReplica || !NodeDown(context, result.Error!)) return result;

        context.Node = null;   // the replica is down: read from the primary instead
        return await next.InvokeAsync(context).ConfigureAwait(false);
    }

    private bool Sticky(Database db)
    {
        var window = db.Config.StickyAfterWriteMs;
        if (window <= 0) return false;
        var last = Interlocked.Read(ref state.LastWriteTicks);
        return last != 0 && time.GetUtcNow().UtcTicks - last < TimeSpan.TicksPerMillisecond * window;
    }

    private static DataNode? Pick(Database db)
    {
        var replicas = db.Replicas;
        var maxLag = db.Config.MaxReplicaLagMs;
        var total = 0;
        foreach (var node in replicas)
            if (Usable(node, maxLag)) total += node.Endpoint.Weight;
        if (total == 0) return null;

        var ticket = Random.Shared.Next(total);
        foreach (var node in replicas)
        {
            if (!Usable(node, maxLag)) continue;
            ticket -= node.Endpoint.Weight;
            if (ticket < 0) return node;
        }
        return null;
    }

    private static bool Usable(DataNode node, int maxLagMs) =>
        node.IsHealthy && (maxLagMs <= 0 || node.LagMs is not { } lag || lag <= maxLagMs);

    private static bool NodeDown(CommandContext context, CodeLogic.Core.Results.Error error) =>
        context.FailedOpening || error.Code is DataErrors.UnavailableCode or DataErrors.CircuitOpenCode;
}
