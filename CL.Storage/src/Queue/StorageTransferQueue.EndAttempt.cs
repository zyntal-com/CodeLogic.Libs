using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    /// <summary>
    /// Ends an attempt: frees its slot and takes <paramref name="next"/> as the job's record (null: the job is
    /// gone). Returns whether requests are waiting; the job is then held until <see cref="SettleAsync"/> ran.
    /// </summary>
    private bool EndAttempt(Entry entry, StorageTransferJobRecord? next, StorageTransferReport? report, (bool Success, bool Transient)? adapt)
    {
        bool settle;
        lock (_gate)
        {
            _running--;
            foreach (var id in entry.Record.Spec.Connections)
                _activeByConnection[id] = Math.Max(0, _activeByConnection.GetValueOrDefault(id) - 1);
            if (entry.Attempt is not null) _attempts.Remove(entry.Attempt);
            entry.Attempt = null;
            entry.Progress = null;
            entry.LastReport = report ?? entry.LastReport;
            if (_options.AdaptiveConcurrency && adapt is var (success, transient))
            {
                if (success) _adaptiveLimit = Math.Min(_options.MaxConcurrentTransfers, _adaptiveLimit + 1);
                else if (transient) _adaptiveLimit = Math.Max(1, _adaptiveLimit / 2);
            }
            if (next is null) Drop(entry);
            else entry.Record = next;
            settle = entry.Requests.Count > 0;
            if (settle) entry.HoldCount++;
        }
        Raise(next is null ? JobRemoved : JobChanged, entry.Snapshot());
        return settle;
    }

    /// <summary>
    /// Renews the lease every third of its duration. A store that fails is tried again while the lease still
    /// holds; only a refused renewal, or a lease about to lapse, stops the attempt.
    /// </summary>
    private async Task RenewAsync(LeaseHolder holder, CancellationTokenSource attempt)
    {
        var interval = _options.LeaseDuration / 3;
        while (!attempt.IsCancellationRequested)
        {
            try { await Task.Delay(interval, attempt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            StorageTransferLease? renewed;
            try
            {
                renewed = await _store.RenewAsync(holder.Lease, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (DateTimeOffset.UtcNow + interval < holder.Lease.ExpiresAt) continue;
                holder.Lost = true;
                await attempt.CancelAsync().ConfigureAwait(false);
                return;
            }
            if (renewed is null)
            {
                holder.Lost = true;
                await attempt.CancelAsync().ConfigureAwait(false);
                return;
            }
            holder.Lease = renewed;
        }
    }

    /// <summary>
    /// Removes the oldest finished jobs beyond <see cref="StorageTransferQueueOptions.MaxFinishedJobs"/>. Each
    /// removal is conditional on the revision seen here, so a job retried elsewhere meanwhile is kept (and
    /// reloaded); a job the store could not remove stays and the next pass tries again.
    /// </summary>
    private async Task PruneAsync()
    {
        if (_options.MaxFinishedJobs is not { } keep) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_pruning)
            {
                _pruneAgain = true;
                return;
            }
            _pruning = true;
        }
        try
        {
            while (true)
            {
                (Entry Entry, long Revision)[] excess;
                lock (_gate)
                {
                    _pruneAgain = false;
                    var finished = _jobs.Values.Where(entry => entry.Record.IsFinished && entry.Attempt is null)
                        .OrderByDescending(entry => entry.Record.FinishedAt).ThenByDescending(entry => entry.Record.Order).ToList();
                    excess = [.. finished.Skip(keep).Where(entry => entry.HoldCount == 0).Select(entry => (entry, entry.Record.Revision))];
                    foreach (var (entry, _) in excess) entry.HoldCount++;
                }
                foreach (var (entry, revision) in excess)
                {
                    bool removed;
                    try { removed = await _store.RemoveAsync(entry.Record.Id, revision, null, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { removed = false; }
                    if (removed)
                    {
                        lock (_gate)
                        {
                            entry.HoldCount--;
                            Drop(entry);
                        }
                        Raise(JobRemoved, entry.Snapshot());
                        continue;
                    }
                    lock (_gate) entry.HoldCount--;
                    await ReloadAsync(entry.Record.Id).ConfigureAwait(false);
                }
                lock (_gate)
                {
                    if (!_pruneAgain || _disposed) return;
                }
            }
        }
        finally
        {
            lock (_gate) _pruning = false;
        }
    }

    private async Task PublishOutcomeAsync(StorageTransferJobRecord? record, Error? error)
    {
        if (record is null) return;
        var spec = record.Spec;
        var now = DateTimeOffset.UtcNow;
        switch (record.State)
        {
            case StorageTransferState.Completed:
                await PublishAsync(new StorageTransferCompletedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Failed:
                await PublishAsync(new StorageTransferFailedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Cancelled:
                await PublishAsync(new StorageTransferCancelledEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Queued when record.NextAttemptAt is { } due:
                await PublishAsync(new StorageTransferRetryingEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, due - now, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Blocked:
                await PublishAsync(new StorageTransferBlockedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.BlockReason!.Value, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.NeedsReconciliation:
                await PublishAsync(new StorageTransferNeedsReconciliationEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, error?.Code ?? StorageErrors.PartialFailureCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Interrupted:
                await PublishAsync(new StorageTransferInterruptedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Checkpoint.Phase, now)).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Re-arms the idle signal when work is added or re-queued; call under the gate.</summary>
    private void MarkBusy()
    {
        if (!_disposed && _idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Signals idle when nothing is running here and nothing waits that this queue could start.</summary>
    private void UpdateIdle()
    {
        lock (_gate)
        {
            if (_disposed || !_jobs.Values.Any(entry => entry.Attempt is not null ||
                                                        (entry.Record.State is StorageTransferState.Queued or StorageTransferState.Running && !LeasedElsewhere(entry.Record))))
                _idle.TrySetResult();
        }
    }

    private void Raise(Action<StorageTransferJob>? handler, StorageTransferJob snapshot)
    {
        if (handler is null) return;
        void Invoke()
        {
            try { handler(snapshot); }
            catch { /* A faulty subscriber must not break the queue. */ }
        }
        if (_options.EventContext is { } context)
        {
            try { context.Post(_ => Invoke(), null); }
            catch { /* A context that refuses work (a UI that shut down) loses the event, not the queue. */ }
        }
        else
        {
            Invoke();
        }
    }

    private async Task PublishAsync<TEvent>(TEvent @event) where TEvent : CodeLogic.Core.Events.IEvent
    {
        try { await _library.CaptureEventPublisher().PublishAsync(@event).ConfigureAwait(false); }
        catch { /* The library may be stopping; queue events are best-effort. */ }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    private enum ControlRequest
    {
        None = 0,
        Pause = 1,
        Cancel = 2,
        Remove = 3
    }

    /// <summary>A pause, cancel, or remove handed to a running attempt, answered when it has taken effect (or not).</summary>
    private sealed class Pending(ControlRequest request)
    {
        public ControlRequest Request { get; } = request;
        public TaskCompletionSource<Result> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
