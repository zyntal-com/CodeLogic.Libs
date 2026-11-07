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
    /// Takes in a record from the store unless this process is running or changing the job, or already has a
    /// newer one. A record written by a newer schema is not taken in: the job leaves this queue's view instead.
    /// </summary>
    private void Adopt(StorageTransferJobRecord record, bool fromRefresh = false)
    {
        if (!record.IsReadable)
        {
            ForgetUnreadable(record);
            return;
        }
        AdvanceOrder(record.Order);
        Entry entry;
        lock (_gate)
        {
            if (_disposed) return;
            // A refresh read the store before this job was removed here: its copy is stale.
            if (fromRefresh && _removedWhileRefreshing.Contains(record.Id)) return;
            if (_jobs.TryGetValue(record.Id, out entry!))
            {
                if (entry.Attempt is not null || entry.HoldCount > 0 || record.Revision < entry.Record.Revision || entry.Record == record) return;
                if (record.Revision == entry.Record.Revision && record.LeaseOwner == entry.Record.LeaseOwner && record.LeaseExpiresAt == entry.Record.LeaseExpiresAt) return;
                entry.Record = record;
            }
            else
            {
                entry = new Entry(record);
                _jobs[record.Id] = entry;
            }
            if (!record.IsFinished) MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        UpdateIdle();
    }

    /// <summary>Removes a job the store no longer has, unless it is busy here or changed since <paramref name="revision"/>.</summary>
    private void Forget(string jobId, long? revision = null)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry) || entry.Attempt is not null || entry.HoldCount > 0) return;
            if (revision is not null && entry.Record.Revision != revision) return;
            Drop(entry);
        }
        Raise(JobRemoved, entry.Snapshot());
        UpdateIdle();
    }

    /// <summary>
    /// Leaves a job that a newer version of the library rewrote to that version, unless it is busy here or this
    /// queue has a later revision; the store keeps it for the version that can read it.
    /// </summary>
    private void ForgetUnreadable(StorageTransferJobRecord record)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(record.Id, out entry) || entry.Attempt is not null || entry.HoldCount > 0 || entry.Record.Revision > record.Revision) return;
            Drop(entry);
        }
        Raise(JobRemoved, entry.Snapshot());
        UpdateIdle();
    }

    /// <summary>A record this version can run, or null (the job is gone from this queue's view) when a newer schema wrote it.</summary>
    private static StorageTransferJobRecord? Readable(StorageTransferJobRecord? record) => record is { IsReadable: false } ? null : record;

    /// <summary>Takes a job out of the local view; call under the gate.</summary>
    private void Drop(Entry entry)
    {
        if (_jobs.TryGetValue(entry.Record.Id, out var current) && ReferenceEquals(current, entry))
            _jobs.Remove(entry.Record.Id);
        if (_refreshing > 0) _removedWhileRefreshing.Add(entry.Record.Id);
    }

    private static Result<StorageTransferJob> SameOrConflict(StorageTransferJobRecord existing, StorageTransferJobSpec spec)
    {
        if (!existing.IsReadable)
            return Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{existing.Id}' was written by a newer version of the library."));
        return existing.Spec.SameWorkAs(spec)
            ? Result<StorageTransferJob>.Success(new StorageTransferJob { Record = existing })
            : Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{existing.Id}' already exists with different work."));
    }

    private static Error StoreError(Exception error) =>
        StorageErrors.Unavailable($"The transfer job store failed: {error.GetType().Name}.");

    private static Error Disposed() => StorageErrors.Unavailable("The transfer queue has been disposed.");

    private static Error NoJobId() => StorageErrors.InvalidContent("A job id is required.");

    /// <summary>Queue order: highest priority first, then lowest order, then id, so equal orders stay stable.</summary>
    private static IEnumerable<Entry> InQueueOrder(IEnumerable<Entry> entries) =>
        entries.OrderByDescending(entry => entry.Record.Priority).ThenBy(entry => entry.Record.Order).ThenBy(entry => entry.Record.Id, StringComparer.Ordinal);

    /// <summary>
    /// The next queue position: later than any seen, and at least the current time in ticks, so queues on one
    /// store that do not see each other's jobs still order them by when they were added.
    /// </summary>
    private long NextOrder()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _order);
            var next = Math.Max(current + 1, DateTimeOffset.UtcNow.UtcTicks);
            if (Interlocked.CompareExchange(ref _order, next, current) == current) return next;
        }
    }

    private void AdvanceOrder(long seen)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _order);
            if (seen <= current || Interlocked.CompareExchange(ref _order, seen, current) == current) return;
        }
    }

    /// <summary>Reads one job from the store: (false, null) when the store failed, (true, null) when it is gone.</summary>
    private async Task<(bool Read, StorageTransferJobRecord? Record)> TryGetAsync(string jobId)
    {
        try { return (true, await _store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false)); }
        catch (Exception) { return (false, null); }
    }

    /// <summary>
    /// Whether <paramref name="fresh"/> is the record a save of <paramref name="sent"/> produced: after a store
    /// call that committed and then failed, the queue adopts it instead of treating its own write as a conflict.
    /// </summary>
    private static bool IsOurWrite(StorageTransferJobRecord? fresh, StorageTransferJobRecord sent, StorageTransferLease? lease) =>
        fresh is not null &&
        fresh.IsReadable &&
        fresh.Revision == sent.Revision + 1 &&
        fresh.State == sent.State &&
        fresh.Priority == sent.Priority &&
        fresh.Order == sent.Order &&
        fresh.Attempts == sent.Attempts &&
        fresh.RetriesLeft == sent.RetriesLeft &&
        fresh.Checkpoint.Phase == sent.Checkpoint.Phase &&
        (lease is null || fresh.FencingToken == lease.FencingToken);

    /// <summary>
    /// Saves under a lease, trying again while the lease still holds when the store fails, and adopting a
    /// write that committed before the store failed. Returns the stored record, or null with whether the
    /// store failed (true) or refused (false).
    /// </summary>
    private async Task<(StorageTransferJobRecord? Stored, bool StoreFailed)> SaveFencedAsync(StorageTransferJobRecord record, StorageTransferLease lease, bool releaseLease)
    {
        var delay = TimeSpan.FromMilliseconds(100);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stored = await _store.SaveAsync(record, lease, releaseLease, CancellationToken.None).ConfigureAwait(false);
                if (stored is not null) return (stored, false);
                // Refused: an earlier try may have committed before it failed.
                var (read, fresh) = await TryGetAsync(record.Id).ConfigureAwait(false);
                if (read && IsOurWrite(fresh, record, lease)) return (fresh, false);
                return (null, false);
            }
            catch (Exception)
            {
                var (read, fresh) = await TryGetAsync(record.Id).ConfigureAwait(false);
                if (read && IsOurWrite(fresh, record, lease)) return (fresh, false);
                // Someone else changed or removed the job: this worker's write is no longer wanted.
                if (read && (fresh is null || fresh.Revision != record.Revision || fresh.FencingToken != lease.FencingToken)) return (null, false);
            }
            if (attempt >= 5 || DateTimeOffset.UtcNow + delay + TimeSpan.FromSeconds(1) >= lease.ExpiresAt) return (null, true);
            await Task.Delay(delay).ConfigureAwait(false);
            delay *= 2;
        }
    }

    /// <summary>Saves a change made by a person (no lease), adopting a write that committed before the store failed.</summary>
    private async Task<(StorageTransferJobRecord? Stored, Error? Error)> SaveControlAsync(StorageTransferJobRecord next, CancellationToken cancellationToken)
    {
        try
        {
            return (await _store.SaveAsync(next, null, releaseLease: false, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception error)
        {
            var (read, fresh) = await TryGetAsync(next.Id).ConfigureAwait(false);
            if (read && IsOurWrite(fresh, next, null)) return (fresh, null);
            return (null, error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? StorageErrors.Cancelled($"Changing job '{next.Id}' was cancelled; its current state has been reloaded.")
                : StoreError(error));
        }
    }

    private async Task ReleaseLeaseAsync(StorageTransferLease lease)
    {
        try { await _store.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* The lease lapses on its own. */ }
    }
}
