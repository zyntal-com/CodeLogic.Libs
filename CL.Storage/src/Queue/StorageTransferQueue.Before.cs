using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    private static bool Before(long order, string id, long otherOrder, string otherId) =>
        order < otherOrder || (order == otherOrder && string.CompareOrdinal(id, otherId) < 0);

    /// <summary>
    /// Saves that spread the orders from the peer before <paramref name="first"/> onwards two apart, keeping
    /// every later order that is already far enough, so a move around <paramref name="first"/>..<paramref name="last"/>
    /// fits in one save afterwards. Each new order is at least the old one and below the next peer's, so saving
    /// them last to first keeps the queue order at every step. Empty when the orders would overflow.
    /// </summary>
    private static (string Id, long From, long To)[] PlanRoom(List<StorageTransferJobRecord> peers, int first, int last)
    {
        var start = Math.Max(0, first - 1);
        var saves = new List<(string Id, long From, long To)>();
        var previous = peers[start].Order;
        for (var i = start + 1; i < peers.Count; i++)
        {
            var old = peers[i].Order;
            if (previous > long.MaxValue - 2) return [];
            var wanted = Math.Max(old, previous + 2);
            if (wanted == old && i > last + 1) break;
            if (wanted != old) saves.Add((peers[i].Id, old, wanted));
            previous = wanted;
        }
        saves.Reverse();
        return [.. saves];
    }

    /// <summary>Takes in the store's current records of some jobs, then starts whatever became eligible.</summary>
    private async Task ReloadAsync(params string[] jobIds)
    {
        foreach (var id in jobIds)
        {
            var (read, fresh) = await TryGetAsync(id).ConfigureAwait(false);
            if (read && fresh is null) Forget(id);
            else if (fresh is not null) Adopt(fresh);
        }
        // A reloaded job may be queued again, and a caller's hold on it has been released.
        UpdateIdle();
        Pump();
    }

    /// <summary>Starts as many eligible jobs as the limits allow: highest priority first, then queue order.</summary>
    private void Pump()
    {
        DateTimeOffset? wakeAt;
        var recover = false;
        lock (_gate)
        {
            if (_paused || _disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in InQueueOrder(_jobs.Values
                .Where(entry => entry.Attempt is null && entry.HoldCount == 0 && entry.Record.State == StorageTransferState.Queued &&
                                (entry.Record.NextAttemptAt ?? DateTimeOffset.MinValue) <= now && entry.NotBefore <= now && !LeasedElsewhere(entry.Record)))
                .ToList())
            {
                if (_running >= _adaptiveLimit) break;
                if (entry.Record.Spec.Connections.Any(id => _activeByConnection.GetValueOrDefault(id) >= _options.MaxTransfersPerConnection))
                    continue;
                _running++;
                foreach (var id in entry.Record.Spec.Connections)
                    _activeByConnection[id] = _activeByConnection.GetValueOrDefault(id) + 1;
                var claimed = entry.Record;
                entry.Record = entry.Record with { State = StorageTransferState.Running };
                if (entry.Cancellation.IsCancellationRequested)
                {
                    entry.Cancellation.Dispose();
                    entry.Cancellation = new CancellationTokenSource();
                }
                MarkBusy();
                // Started under the gate, so the attempt cannot finish before it is recorded here.
                var run = entry;
                entry.Attempt = Task.Run(() => RunAsync(run, claimed));
                _attempts.Add(entry.Attempt);
            }
            // Timers can fire slightly early, so the wake-up comes from the earliest due time.
            var waiting = _jobs.Values
                .Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Queued)
                .Select(entry => Later(entry.Record.NextAttemptAt, entry.NotBefore))
                .Where(due => due > now)
                .Min();
            // Jobs another process left running are recovered once their lease has lapsed, one pass at a time.
            var lapsed = _jobs.Values.Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Running && !LeasedElsewhere(entry.Record)).ToList();
            if (!_recovering && lapsed.Any(entry => entry.RecoverAfter <= now))
            {
                _recovering = true;
                recover = true;
            }
            var retryRecovery = lapsed.Where(entry => entry.RecoverAfter > now).Select(entry => (DateTimeOffset?)entry.RecoverAfter).Min();
            // Queued or running jobs another worker holds are looked at again when their lease ends.
            var leaseEnds = _jobs.Values.Where(entry => entry.Attempt is null && entry.Record.State is StorageTransferState.Running or StorageTransferState.Queued && LeasedElsewhere(entry.Record))
                .Select(entry => entry.Record.LeaseExpiresAt).Min();
            wakeAt = new[] { waiting, retryRecovery, leaseEnds }.Where(time => time is not null).Min();
        }
        if (recover)
            Track(Task.Run(RecoverLapsedAsync));
        if (wakeAt is { } due)
            ScheduleWake(due);
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    /// <summary>Arms one timer for the earliest time something becomes due; later requests reuse it.</summary>
    private void ScheduleWake(DateTimeOffset due)
    {
        lock (_gate)
        {
            if (_disposed || (_wakeAt is { } armed && armed <= due)) return;
            _wakeAt = due;
        }
        Track(WakeLaterAsync(due));
    }

    private async Task WakeLaterAsync(DateTimeOffset due)
    {
        try
        {
            // Task.Delay cannot wait longer than about 49 days, so long waits are taken in steps.
            for (var left = due - DateTimeOffset.UtcNow; left > TimeSpan.Zero; left = due - DateTimeOffset.UtcNow)
                await Task.Delay((left > LongestTimer ? LongestTimer : left) + TimeSpan.FromMilliseconds(5), _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        lock (_gate)
        {
            if (_wakeAt != due) return;
            _wakeAt = null;
        }
        Pump();
    }

    private async Task RecoverLapsedAsync()
    {
        var changed = false;
        try
        {
            Entry[] lapsed;
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                if (_disposed) return;
                lapsed = [.. _jobs.Values.Where(entry => entry.Attempt is null && entry.HoldCount == 0 && entry.Record.State == StorageTransferState.Running &&
                                                         !LeasedElsewhere(entry.Record) && entry.RecoverAfter <= now)];
            }
            foreach (var entry in lapsed)
            {
                lock (_gate) if (_disposed) return;
                var (read, fresh) = await TryGetAsync(entry.Record.Id).ConfigureAwait(false);
                StorageTransferJobRecord? recovered = null;
                if (read && fresh is null)
                {
                    Forget(entry.Record.Id);
                    continue;
                }
                if (fresh is not null && (!fresh.IsReadable || fresh.State != StorageTransferState.Running || LeasedElsewhere(fresh)))
                {
                    Adopt(fresh);
                    changed = true;
                    continue;
                }
                if (fresh is not null)
                    recovered = await TryRecoverAsync(fresh, CancellationToken.None).ConfigureAwait(false);
                if (recovered is not null)
                {
                    Adopt(recovered);
                    changed = true;
                }
                else
                {
                    // The store failed or another worker got there first: try again later, not in a loop.
                    lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
                }
            }
        }
        finally
        {
            lock (_gate) _recovering = false;
        }
        Pump();
        if (changed) UpdateIdle();
    }
}
