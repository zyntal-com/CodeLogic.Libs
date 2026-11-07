using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    /// <summary>Moves a waiting job one place earlier among jobs of the same priority.</summary>
    public Task<Result> MoveUpAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: true, cancellationToken);

    /// <summary>Moves a waiting job one place later among jobs of the same priority.</summary>
    public Task<Result> MoveDownAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: false, cancellationToken);

    /// <summary>
    /// Removes a job from the store and raises <see cref="JobRemoved"/>. The removal is conditional on the
    /// job's revision, so a job another process changed meanwhile is reloaded instead (<c>storage.conflict</c>).
    /// A running job is cancelled first and removed when its attempt stops; the call returns then, with the
    /// store's answer, or with <c>storage.timeout</c> after <see cref="StorageTransferQueueOptions.ControlTimeout"/>
    /// (the removal still happens when the attempt stops). An attempt that ends
    /// <see cref="StorageTransferState.NeedsReconciliation"/> keeps the job, and the call fails with
    /// <c>storage.conflict</c>.
    /// </summary>
    public Task<Result> RemoveAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, ControlRequest.Remove, cancellationToken);

    /// <summary>Removes jobs in the given states — by default every finished one (completed, failed, cancelled).</summary>
    /// <returns>The number of jobs removed.</returns>
    public Task<int> ClearAsync(params StorageTransferState[] states) => ClearAsync(states, CancellationToken.None);

    /// <summary>Removes jobs in the given states — by default (null or empty) every finished one (completed, failed, cancelled).</summary>
    /// <param name="states">The states to clear; running jobs are never cleared.</param>
    /// <param name="cancellationToken">Stops clearing further jobs.</param>
    /// <returns>The number of jobs removed.</returns>
    public async Task<int> ClearAsync(IEnumerable<StorageTransferState>? states, CancellationToken cancellationToken)
    {
        var chosen = states?.ToArray() ?? [];
        var targets = chosen.Length == 0
            ? [StorageTransferState.Completed, StorageTransferState.Failed, StorageTransferState.Cancelled]
            : chosen.Where(state => state != StorageTransferState.Running).ToArray();
        string[] ids;
        lock (_gate)
            ids = [.. _jobs.Values.Where(entry => entry.Attempt is null && targets.Contains(entry.Record.State)).Select(entry => entry.Record.Id)];
        var removed = 0;
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if ((await RemoveAsync(id, cancellationToken).ConfigureAwait(false)).IsSuccess) removed++;
        }
        return removed;
    }

    /// <summary>
    /// Reads the store again to pick up jobs added, changed, or removed by other processes. A job removed here
    /// while the store was being read does not come back.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, long> known;
        lock (_gate)
        {
            if (_disposed) return;
            _refreshing++;
            known = _jobs.Values.Where(entry => entry.Attempt is null).ToDictionary(entry => entry.Record.Id, entry => entry.Record.Revision, StringComparer.Ordinal);
        }
        try
        {
            IReadOnlyList<StorageTransferJobRecord> records;
            try { records = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { return; }
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                present.Add(record.Id);
                // Checked as each record is taken in, so a job removed while earlier ones were (by a handler,
                // say) does not come back.
                Adopt(record, fromRefresh: true);
            }
            // A job the store no longer has is forgotten, unless it changed here since the read began.
            foreach (var (id, revision) in known)
                if (!present.Contains(id)) Forget(id, revision);
        }
        finally
        {
            lock (_gate)
            {
                if (--_refreshing == 0) _removedWhileRefreshing.Clear();
            }
        }
        await PruneAsync().ConfigureAwait(false);
        Pump();
    }

    /// <summary>
    /// Waits until no job is queued or running here (jobs leased by other workers do not count), or until the
    /// queue is disposed. A paused queue with waiting jobs is not idle.
    /// </summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops running jobs and waits (up to <see cref="StorageTransferQueueOptions.ShutdownTimeout"/>) for them
    /// to record where they stopped, and for the queue's background work and store calls to end. Queued jobs stay
    /// queued in the store; a job stopped before it touched its destination returns to the queue, any other
    /// becomes <see cref="StorageTransferState.Interrupted"/>. A job that finished as it was stopped keeps its
    /// result. Once this returns the queue makes no new call to the store, so the store may be closed: an
    /// attempt that outlived the timeout records nothing more, and its job is recovered by the restart rules
    /// when its lease lapses (a store call still in flight then is the store's to finish). Afterwards every
    /// method that changes a job fails with <c>storage.unavailable</c>, and <see cref="WaitForIdleAsync"/> returns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _library.UntrackQueue(this);
        if (_refresh is not null) await _refresh.DisposeAsync().ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var stopped = false;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = [.. _attempts, .. _background, .. _storeCalls > 0 ? [_storeQuiet.Task] : Array.Empty<Task>()];
                if (pending.Length == 0)
                {
                    // Nothing runs and nothing can start: the store is closed to this queue from here on.
                    _storeClosed = true;
                    stopped = true;
                    break;
                }
            }
            var left = _options.ShutdownTimeout - waited.Elapsed;
            if (left <= TimeSpan.Zero) break;
            try { await Task.WhenAll(pending).WaitAsync(left).ConfigureAwait(false); }
            catch (TimeoutException) { break; }
            catch (Exception) { /* A failed attempt or refresh has still ended. */ }
        }
        lock (_gate)
        {
            // Work that outlived the timeout finds the store closed: it records nothing more, and a job it held
            // is recovered by the restart rules once its lease lapses.
            _storeClosed = true;
            _idle.TrySetResult();
            // Attempts that outlived the timeout still use their tokens; they are left to the collector.
            if (stopped)
                foreach (var entry in _jobs.Values) entry.Cancellation.Dispose();
        }
        if (stopped) _shutdown.Dispose();
    }

    /// <summary>Keeps background work where <see cref="DisposeAsync"/> waits for it.</summary>
    private void Track(Task work)
    {
        lock (_gate)
        {
            if (work.IsCompleted) return;
            _background.Add(work);
        }
        work.ContinueWith(done =>
        {
            lock (_gate) _background.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Starts a store call, or throws when the queue has closed the store (after it was disposed).</summary>
    private void EnterStore()
    {
        lock (_gate)
        {
            if (_storeClosed) throw new ObjectDisposedException(nameof(StorageTransferQueue), "The transfer queue has been disposed.");
            if (_storeCalls++ == 0) _storeQuiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void LeaveStore()
    {
        lock (_gate)
        {
            if (--_storeCalls == 0) _storeQuiet.TrySetResult();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            // Records written by a newer version of the library are left for it.
            if (!record.IsReadable) continue;
            AdvanceOrder(record.Order);
            var current = record;
            // A job left running by an earlier process, including one that used this worker id, is recovered.
            if (current.State == StorageTransferState.Running && !LeasedElsewhere(current))
                current = await TryRecoverAsync(current, cancellationToken).ConfigureAwait(false) ?? current;
            lock (_gate)
            {
                _jobs[current.Id] = new Entry(current);
                if (!current.IsFinished) MarkBusy();
            }
        }
        UpdateIdle();
    }

    /// <summary>Restart rule: back to the queue when the destination was never touched, otherwise Interrupted.</summary>
    private StorageTransferJobRecord Recover(StorageTransferJobRecord record)
    {
        var untouched = record.Checkpoint.Phase is StorageTransferPhase.NotStarted or StorageTransferPhase.Transferring;
        var state = untouched && _options.RequeueInterruptedWhenSafe ? StorageTransferState.Queued : StorageTransferState.Interrupted;
        return record with { State = state, NextAttemptAt = null };
    }

    /// <summary>Claims a job left running and records its restart state; null when that did not succeed.</summary>
    private async Task<StorageTransferJobRecord?> TryRecoverAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
    {
        StorageTransferLease? lease;
        try
        {
            lease = await _store.TryClaimAsync(record.Id, _options.WorkerId, record.Revision, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        if (lease is null) return null;
        var (recovered, _) = await SaveFencedAsync(Recover(record), lease, releaseLease: true).ConfigureAwait(false);
        if (recovered is null)
        {
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            return null;
        }
        if (recovered.State == StorageTransferState.Interrupted)
            await PublishOutcomeAsync(recovered, null).ConfigureAwait(false);
        return recovered;
    }

    private bool LeasedElsewhere(StorageTransferJobRecord record) =>
        record.LeaseOwner is not null && record.LeaseOwner != _options.WorkerId && record.LeaseExpiresAt > DateTimeOffset.UtcNow;
}
