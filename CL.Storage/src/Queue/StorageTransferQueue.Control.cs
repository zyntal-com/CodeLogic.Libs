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
    /// Pauses, cancels, or removes a job. A request for a job this queue is running is handed to the attempt
    /// and answered when the attempt has stopped, so it is never acknowledged and then lost.
    /// </summary>
    private async Task<Result> ControlAsync(string jobId, ControlRequest request, CancellationToken cancellationToken, bool settling = false)
    {
        if (jobId is null) return Result.Failure(NoJobId());
        Entry? entry;
        Pending? pending = null;
        long revision = 0;
        lock (_gate)
        {
            if (_disposed) return Result.Failure(Disposed());
            if (!_jobs.TryGetValue(jobId, out entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Attempt is not null)
            {
                pending = new Pending(request);
                entry.Requests.Add(pending);
            }
            else if (entry.Record.State == StorageTransferState.Running)
            {
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is running in another process."));
            }
            else if (entry.HoldCount > 0 && !settling)
            {
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is being changed; try again."));
            }
            else if (request == ControlRequest.Remove && settling && entry.Record.State == StorageTransferState.NeedsReconciliation)
            {
                // Asked while its attempt ran, which ended needing a person's decision: kept, as above.
                return Result.Failure(StorageErrors.Conflict(
                    $"Job '{jobId}' changed its destination before it could be stopped; it needs reconciliation, so it was kept."));
            }
            else if (request == ControlRequest.Remove)
            {
                // Held, so it cannot start while the store removes it.
                entry.HoldCount++;
                revision = entry.Record.Revision;
            }
        }
        if (pending is not null)
        {
            try { entry.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            // A provider may ignore cancellation, so the wait is bounded; the request stays recorded either way.
            try { return await pending.Done.Task.WaitAsync(_options.ControlTimeout, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Result.Failure(StorageErrors.Cancelled($"Stopped waiting for job '{jobId}'; the request still applies when its attempt stops."));
            }
            catch (TimeoutException)
            {
                return Result.Failure(StorageErrors.Timeout($"Job '{jobId}' has not stopped yet; the request still applies when its attempt stops."));
            }
        }
        if (request == ControlRequest.Remove)
            return await RemoveHeldAsync(entry, revision).ConfigureAwait(false);

        var result = await ChangeAsync(jobId, cancellationToken, current => request == ControlRequest.Pause ? PauseChange(current) : CancelChange(current), settling).ConfigureAwait(false);
        // The job started while the change was being saved (its claim won): hand the request to the attempt.
        if (result.IsFailure && result.Error!.Code == StorageErrors.ConflictCode && !settling)
        {
            lock (_gate) if (!_jobs.TryGetValue(jobId, out var now) || now.Attempt is null) return result;
            return await ControlAsync(jobId, request, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static (Result, StorageTransferJobRecord?) PauseChange(Entry entry) => entry.Record.State switch
    {
        StorageTransferState.Paused => (Result.Success(), null),
        StorageTransferState.Queued => (Result.Success(), entry.Record with { State = StorageTransferState.Paused }),
        _ => (Result.Failure(StorageErrors.Conflict($"Job '{entry.Record.Id}' is {entry.Record.State} and cannot be paused.")), null)
    };

    private static (Result, StorageTransferJobRecord?) CancelChange(Entry entry) => entry.Record.IsFinished
        ? (Result.Failure(StorageErrors.Conflict($"Job '{entry.Record.Id}' has already finished.")), null)
        : (Result.Success(), entry.Record with { State = StorageTransferState.Cancelled, FinishedAt = DateTimeOffset.UtcNow });

    /// <summary>Removes a waiting job held by the caller, conditional on the revision the queue saw.</summary>
    private async Task<Result> RemoveHeldAsync(Entry entry, long revision)
    {
        var jobId = entry.Record.Id;
        bool removed;
        try
        {
            removed = await _store.RemoveAsync(jobId, revision, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The hold kept the job from starting; now that it is released, the job may start after all.
            lock (_gate) entry.HoldCount--;
            UpdateIdle();
            Pump();
            return Result.Failure(StoreError(error));
        }
        if (!removed)
        {
            lock (_gate) entry.HoldCount--;
            await ReloadAsync(jobId).ConfigureAwait(false);
            return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' was changed by another worker; its current state has been reloaded."));
        }
        lock (_gate)
        {
            entry.HoldCount--;
            Drop(entry);
        }
        Raise(JobRemoved, entry.Snapshot());
        UpdateIdle();
        return Result.Success();
    }

    /// <summary>Saves a change made by a person. The save is compare-and-swap, so a stale copy reloads instead.</summary>
    private async Task<Result> ChangeAsync(
        string jobId,
        CancellationToken cancellationToken,
        Func<Entry, (Result Result, StorageTransferJobRecord? Next)> change,
        bool settling = false)
    {
        if (jobId is null) return Result.Failure(NoJobId());
        Entry? entry;
        StorageTransferJobRecord next;
        lock (_gate)
        {
            if (_disposed) return Result.Failure(Disposed());
            if (!_jobs.TryGetValue(jobId, out entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State == StorageTransferState.Running && entry.Attempt is null)
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is running in another process."));
            if (entry.HoldCount > 0 && !settling)
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is being changed; try again."));
            var (result, proposed) = change(entry);
            if (result.IsFailure || proposed is null) return result;
            next = proposed;
        }
        var (stored, failure) = await SaveControlAsync(next, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            await ReloadAsync(jobId).ConfigureAwait(false);
            return Result.Failure(failure ?? StorageErrors.Conflict($"Job '{jobId}' was changed by another worker; its current state has been reloaded."));
        }
        lock (_gate)
        {
            // An attempt that started meanwhile claimed the old revision; its claim fails and it adopts this one.
            if (entry.Attempt is null && entry.Record.Revision < stored.Revision) entry.Record = stored;
            entry.StoreFailures = 0;
            if (stored.State == StorageTransferState.Queued) MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        if (stored.State == StorageTransferState.Cancelled)
            await PublishOutcomeAsync(stored, null).ConfigureAwait(false);
        UpdateIdle();
        if (stored.IsFinished) await PruneAsync().ConfigureAwait(false);
        Pump();
        return Result.Success();
    }

    /// <summary>
    /// Moves a job one place among waiting jobs of its priority. Queue order is by <see cref="StorageTransferJobRecord.Order"/>,
    /// then id, so the move is one save: of the job, to a position between its neighbour and the one beyond,
    /// or of the neighbour, to the job's other side. When neither has room (several jobs share an order), the
    /// positions around the job are first spread out, one save per job, from the last to the first: every
    /// intermediate state keeps the queue's order, so a save that fails part-way reorders nothing.
    /// </summary>
    private async Task<Result> SwapAsync(string jobId, bool earlier, CancellationToken cancellationToken)
    {
        if (jobId is null) return Result.Failure(NoJobId());
        for (var round = 0; ; round++)
        {
            (string Id, long From, long To)[] saves;
            bool isMove;
            lock (_gate)
            {
                if (_disposed) return Result.Failure(Disposed());
                if (!_jobs.TryGetValue(jobId, out var entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
                if (entry.Record.State is not (StorageTransferState.Queued or StorageTransferState.Paused))
                    return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is not waiting."));
                var peers = InQueueOrder(_jobs.Values.Where(candidate => candidate.Attempt is null && candidate.Record.Priority == entry.Record.Priority &&
                                                                         candidate.Record.State is StorageTransferState.Queued or StorageTransferState.Paused))
                    .Select(candidate => candidate.Record).ToList();
                var index = peers.FindIndex(record => record.Id == jobId);
                var neighbour = earlier ? index - 1 : index + 1;
                if (neighbour < 0 || neighbour >= peers.Count) return Result.Success();
                var move = PlanMove(peers, index, neighbour);
                if (move is { } single)
                {
                    saves = [single];
                    isMove = true;
                }
                else
                {
                    if (round > 0) return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' could not be moved; its neighbours changed meanwhile."));
                    saves = PlanRoom(peers, Math.Min(index, neighbour), Math.Max(index, neighbour));
                    if (saves.Length == 0) return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' could not be moved; there is no room in the queue order."));
                    isMove = false;
                }
            }
            foreach (var (id, from, to) in saves)
            {
                var saved = await ChangeAsync(id, cancellationToken, current =>
                    current.Record.State is StorageTransferState.Queued or StorageTransferState.Paused && current.Record.Order == from
                        ? (Result.Success(), current.Record with { Order = to })
                        : (Result.Failure(StorageErrors.Conflict($"Job '{id}' changed meanwhile; try again.")), null)).ConfigureAwait(false);
                if (saved.IsFailure) return saved;
            }
            // One save moved the job; otherwise room was made, and the next round moves it.
            if (isMove) return Result.Success();
        }
    }

    /// <summary>
    /// One save that swaps the peers at <paramref name="index"/> and <paramref name="neighbour"/>: the job moved
    /// past its neighbour, or the neighbour past the job. Null when neither fits between the orders around them.
    /// </summary>
    private static (string Id, long From, long To)? PlanMove(List<StorageTransferJobRecord> peers, int index, int neighbour)
    {
        var job = peers[index];
        var other = peers[neighbour];
        var first = Math.Min(index, neighbour);
        var last = Math.Max(index, neighbour);
        var before = first > 0 ? peers[first - 1] : null;
        var after = last + 1 < peers.Count ? peers[last + 1] : null;
        // The earlier of the two goes after the later one, or the later one before the earlier one.
        var (early, late) = index < neighbour ? (job, other) : (other, job);
        if (OrderBetween(late, after, early.Id) is { } afterLate) return (early.Id, early.Order, afterLate);
        if (OrderBetween(before, early, late.Id) is { } beforeEarly) return (late.Id, late.Order, beforeEarly);
        return null;
    }

    /// <summary>An order that puts job <paramref name="id"/> strictly between two records in queue order (order, then id), or null.</summary>
    private static long? OrderBetween(StorageTransferJobRecord? lower, StorageTransferJobRecord? upper, string id)
    {
        var candidates = new List<long>();
        if (lower is not null && upper is not null && (Int128)upper.Order - lower.Order >= 2)
            candidates.Add((long)(lower.Order + ((Int128)upper.Order - lower.Order) / 2));
        if (upper is not null)
        {
            if (upper.Order > long.MinValue) candidates.Add(upper.Order - 1);
            candidates.Add(upper.Order);
        }
        if (lower is not null)
        {
            if (lower.Order < long.MaxValue) candidates.Add(lower.Order + 1);
            candidates.Add(lower.Order);
        }
        foreach (var order in candidates)
            if ((lower is null || Before(lower.Order, lower.Id, order, id)) && (upper is null || Before(order, id, upper.Order, upper.Id)))
                return order;
        return null;
    }
}
