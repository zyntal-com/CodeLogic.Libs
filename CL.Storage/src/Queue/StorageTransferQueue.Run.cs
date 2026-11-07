using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    private async Task RunAsync(Entry entry, StorageTransferJobRecord claimed)
    {
        StorageTransferLease? lease;
        try
        {
            lease = await _store.TryClaimAsync(claimed.Id, _options.WorkerId, claimed.Revision, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The store failed: wait before trying this job again instead of spinning on it, longer each time,
            // and fail it after MaxStoreFailures in a row like any other store failure.
            await StepAsideAsync(entry, claimed, storeFailed: true, saveFailed: true).ConfigureAwait(false);
            return;
        }
        if (lease is null)
        {
            await StepAsideAsync(entry, claimed, refused: true).ConfigureAwait(false);
            return;
        }
        if (_shutdown.IsCancellationRequested)
        {
            // The queue is being disposed: give the job back untouched.
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            await StepAsideAsync(entry, claimed).ConfigureAwait(false);
            return;
        }

        var spec = claimed.Spec;
        var startedAt = DateTimeOffset.UtcNow;
        // The recorded phase is kept from earlier attempts: a destination an earlier attempt changed stays
        // possibly changed, so a crash in this attempt is never misjudged as untouched.
        var running = claimed with
        {
            State = StorageTransferState.Running,
            Attempts = claimed.Attempts + 1,
            StartedAt = startedAt,
            BlockReason = null
        };
        var (saved, startFailed) = await SaveFencedAsync(running, lease, releaseLease: false).ConfigureAwait(false);
        if (saved is null)
        {
            await ReleaseLeaseAsync(lease).ConfigureAwait(false);
            await StepAsideAsync(entry, claimed, storeFailed: startFailed, saveFailed: startFailed).ConfigureAwait(false);
            return;
        }

        StorageTransferJobRecord? final = null;
        StorageTransferReport? report = null;
        var result = Result.Failure(StorageErrors.Unavailable("The transfer did not finish."));
        Error? outcomeError = null;
        var transient = false;
        var ours = false;
        var removed = false;
        Error? removeError = null;
        List<Pending> decided = [];
        var ended = false;
        var settle = false;
        try
        {
            lock (_gate) entry.Record = saved;
            Raise(JobChanged, entry.Snapshot());
            await PublishAsync(new StorageTransferStartedEvent(saved.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, startedAt)).ConfigureAwait(false);

            var holder = new LeaseHolder(lease);
            var progress = new Throttled(this, entry);
            var phase = saved.Checkpoint.Phase;
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _shutdown.Token))
            using (var recording = new SemaphoreSlim(1, 1))
            {
                var renewal = RenewAsync(holder, attempt);
                async Task RecordPhaseAsync(StorageTransferPhase next, CancellationToken token)
                {
                    await recording.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        // The recorded phase only moves forward: a directory's per-file phases, or a later
                        // attempt, must not make a restart believe a destination already changed is untouched.
                        if (next <= phase) return;
                        StorageTransferJobRecord current;
                        lock (_gate) current = entry.Record;
                        var (stored, failed) = await SaveFencedAsync(current with { Checkpoint = current.Checkpoint with { Phase = next } }, holder.Lease, releaseLease: false).ConfigureAwait(false);
                        if (stored is null)
                        {
                            // Without a recorded phase a crash could be misjudged, and a worker that lost its
                            // lease must not go on to change the destination: the attempt stops here.
                            if (failed) holder.StoreFailed = true;
                            else holder.Lost = true;
                            await attempt.CancelAsync().ConfigureAwait(false);
                            throw new OperationCanceledException("The job's phase could not be recorded.", attempt.Token);
                        }
                        phase = next;
                        lock (_gate) entry.Record = stored;
                    }
                    finally
                    {
                        recording.Release();
                    }
                }

                try
                {
                    (result, report) = await ExecuteAsync(spec, saved.Checkpoint.ResumeToken, progress, RecordPhaseAsync, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    result = Result.Failure(StorageErrors.Cancelled("The transfer was stopped."));
                }
                catch (Exception error)
                {
                    result = Result.Failure(StorageErrors.FromException(error, "Queued transfer"));
                }
                await attempt.CancelAsync().ConfigureAwait(false);
                try { await renewal.ConfigureAwait(false); } catch (Exception) { }
                await recording.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await progress.CloseAsync().ConfigureAwait(false);
            outcomeError = result.Error;

            // Requests made from here on are answered after the outcome is saved (see SettleAsync); those taken
            // here are answered in the finally below, whatever happens in between.
            lock (_gate)
            {
                decided = [.. entry.Requests];
                entry.Requests.Clear();
            }
            var request = decided.Count == 0 ? ControlRequest.None : decided.Max(pending => pending.Request);
            if (holder.Lost)
            {
                // Another worker owns the job now: the store is the truth.
                var (read, fresh) = await TryGetAsync(claimed.Id).ConfigureAwait(false);
                final = read ? Readable(fresh) : entry.Record with { State = StorageTransferState.Running };
                if (!read) lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
            }
            else
            {
                var fallback = request == ControlRequest.Remove ? ControlRequest.Cancel : request;
                var (next, isTransient, gaveUp) = Decide(entry, result, report, phase, holder, fallback);
                transient = isTransient;
                // Stopped by the job store (given up, or queued again): announced with the store's error, not
                // the cancellation that stopped the transfer.
                if (gaveUp || (holder.StoreFailed && next.State == StorageTransferState.Queued && next.Failure is not null))
                    outcomeError = next.Failure!.ToError();
                if (request == ControlRequest.Remove && next.State == StorageTransferState.NeedsReconciliation)
                {
                    // Removing it would hide that a person must decide (a move whose copy committed: both source
                    // and destination exist now), so it is kept and the caller told why.
                    removeError = StorageErrors.Conflict(
                        $"Job '{claimed.Id}' changed its destination before it could be stopped; it needs reconciliation, so it was kept.");
                }
                else if (request == ControlRequest.Remove)
                {
                    StorageTransferJobRecord current;
                    lock (_gate) current = entry.Record;
                    try { removed = await _store.RemoveAsync(claimed.Id, current.Revision, holder.Lease, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { removeError = StoreError(error); }
                    // Not removed: the job is recorded as if cancelled, and the caller told why.
                    if (!removed) removeError ??= StorageErrors.Conflict($"Job '{claimed.Id}' could not be removed; the job store refused.");
                }
                if (!removed)
                {
                    var (stored, storeFailed) = await SaveFencedAsync(next, holder.Lease, releaseLease: true).ConfigureAwait(false);
                    if (stored is not null)
                    {
                        final = stored;
                        ours = true;
                        // An attempt that ran to an outcome of its own ends a run of store failures.
                        if (!holder.StoreFailed) lock (_gate) entry.StoreFailures = 0;
                    }
                    else if (gaveUp && storeFailed)
                    {
                        // The store keeps failing and this queue has given up on the job: it stops here, in
                        // this queue's view, and its lease lapses in the store.
                        final = next;
                        ours = true;
                    }
                    else
                    {
                        var (read, fresh) = await TryGetAsync(next.Id).ConfigureAwait(false);
                        // The store refused (another worker owns the job now) or failed; its record, if readable, is the truth.
                        final = read ? Readable(fresh) : next with { State = StorageTransferState.Running };
                        if (!read) lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
                    }
                }
            }
            settle = EndAttempt(entry, final, report, (result.IsSuccess, transient));
            ended = true;
        }
        catch (Exception)
        {
            // Unexpected (a store that returned a malformed record, say): handled below like a lost outcome.
        }
        finally
        {
            // Whatever failed above, the slot is released, the job left in a state the queue can act on, the
            // requests taken are answered, and a hold raised for later requests is settled.
            if (!ended)
            {
                lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
                ours = false;
                final = removed ? null : entry.Record with { State = StorageTransferState.Running };
                settle = EndAttempt(entry, final, report, null);
            }
            if (ours) await PublishOutcomeAsync(final, outcomeError).ConfigureAwait(false);
            foreach (var pending in decided)
                pending.Done.TrySetResult(Answer(pending.Request, claimed.Id, final, removed, removeError));
            if (settle) await SettleAsync(entry).ConfigureAwait(false);
        }
        await PruneAsync().ConfigureAwait(false);
        UpdateIdle();
        Pump();
    }

    /// <summary>How a request the attempt acted on turned out.</summary>
    private static Result Answer(ControlRequest request, string jobId, StorageTransferJobRecord? final, bool removed, Error? removeError)
    {
        var wanted = request switch
        {
            ControlRequest.Pause => StorageTransferState.Paused,
            ControlRequest.Cancel => StorageTransferState.Cancelled,
            _ => (StorageTransferState?)null
        };
        if (request == ControlRequest.Remove)
            return removed ? Result.Success() : Result.Failure(removeError ?? StorageErrors.Conflict($"Job '{jobId}' could not be removed."));
        if (final?.State == wanted) return Result.Success();
        return Result.Failure(final?.State switch
        {
            StorageTransferState.NeedsReconciliation => StorageErrors.Conflict($"Job '{jobId}' changed its destination before it could be stopped; it needs reconciliation."),
            null => StorageErrors.NotFound($"Job '{jobId}' is gone."),
            StorageTransferState.Running => StorageErrors.Unavailable($"Job '{jobId}' stopped, but its state could not be recorded."),
            var state => StorageErrors.Conflict($"Job '{jobId}' is {state}; it finished before the request took effect.")
        });
    }
}
