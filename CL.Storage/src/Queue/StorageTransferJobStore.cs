namespace CL.Storage.Queue;

/// <summary>
/// Where a <see cref="StorageTransferQueue"/> keeps its jobs, so they survive a restart and can be shared
/// between processes. Implement it over a database to make the queue durable; the default keeps jobs in memory.
/// </summary>
/// <remarks>
/// <para>Each method must be atomic for its job. Three rules keep concurrent queues from overwriting each other:</para>
/// <list type="bullet">
/// <item><b>Revisions.</b> Every stored record has a <see cref="StorageTransferJobRecord.Revision"/>.
/// <see cref="AddAsync"/> stores revision 1 for an id never stored before, and every successful
/// <see cref="SaveAsync"/> stores the next one. A save or remove whose record does not carry the current revision
/// is refused: the caller worked from a stale copy. Claims, renewals, and releases do not change the revision.
/// Like fencing tokens, revisions do not restart when an id is removed and added again: the new record starts
/// above every revision the id had before, so a copy from its earlier life can neither save over nor remove
/// it. A store keeps the highest revision (and fencing token) per removed id for that.</item>
/// <item><b>Leases.</b> The store owns <see cref="StorageTransferJobRecord.LeaseOwner"/>,
/// <see cref="StorageTransferJobRecord.LeaseExpiresAt"/>, and <see cref="StorageTransferJobRecord.FencingToken"/>;
/// the values in a record passed to <see cref="SaveAsync"/> are ignored. <see cref="TryClaimAsync"/> succeeds
/// only for the expected revision and when no other worker holds an unexpired lease (the same worker may claim
/// again, which a restarted process needs). A save or remove with a lease succeeds only while that lease is
/// current (same owner and token, unexpired); one without a lease only while no worker holds an unexpired lease.</item>
/// <item><b>Fencing.</b> Every claim issues a fencing token greater than any issued before for that job id,
/// including tokens issued before the job was removed and added again, so a lease from an earlier life of the
/// id never matches a later one.</item>
/// </list>
/// <para><b>Whose clock decides.</b> The store's clock alone decides whether a lease has expired: claims, saves,
/// renewals, and removes compare <see cref="StorageTransferJobRecord.LeaseExpiresAt"/> with the store's time
/// (a database should use its own <c>now()</c>, not the caller's). The queue reads the same field with its own
/// clock only as a hint, to decide when to try a claim again and when a renewal is overdue; a skewed worker
/// clock therefore costs a refused claim or an early stop, never two workers holding one job.</para>
/// <para><b>Schema.</b> Records carry a <see cref="StorageTransferJobRecord.SchemaVersion"/> and their spec a
/// <see cref="StorageTransferJobSpec.SchemaVersion"/>; a store that serializes them must keep both (or use
/// <see cref="StorageTransferJobRecord.ToJson"/>). A queue leaves records written by a newer schema alone. A
/// store that keeps records as JSON must skip a row it cannot read (<see cref="StorageTransferJobRecord.FromJson"/>
/// throws for a newer schema) in <see cref="LoadAsync"/> instead of failing the whole load, and may answer null
/// for it from <see cref="GetAsync"/>, which takes the job out of this queue's view without changing it.</para>
/// <para><b>Columns win.</b> A store that keeps the revision and the lease fields
/// (<see cref="StorageTransferJobRecord.Revision"/>, <see cref="StorageTransferJobRecord.LeaseOwner"/>,
/// <see cref="StorageTransferJobRecord.LeaseExpiresAt"/>, <see cref="StorageTransferJobRecord.FencingToken"/>) in
/// columns of their own, next to the record's JSON, must return the columns' values: the copies inside the JSON
/// are only as new as the last save, since claims, renewals, and releases change the columns alone.</para>
/// </remarks>
public interface IStorageTransferJobStore
{
    /// <summary>Returns every stored job.</summary>
    Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Adds a job at revision 1, or above every revision the id had before when it was removed and is added again.</summary>
    /// <returns>The stored record, or null when a job with the same id already exists.</returns>
    Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken);

    /// <summary>Returns one job, or null.</summary>
    Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Claims a job for a worker, provided it is still at <paramref name="expectedRevision"/>.</summary>
    /// <returns>The lease, or null when the job changed, is gone, or another worker holds an unexpired lease.</returns>
    Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Extends a lease.</summary>
    /// <returns>The renewed lease, or null when it is no longer current.</returns>
    Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Ends a lease without changing the record (its revision stays).</summary>
    /// <returns>Whether the lease was current and is now released.</returns>
    Task<bool> ReleaseAsync(StorageTransferLease lease, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a job's record if it still has the record's <see cref="StorageTransferJobRecord.Revision"/>,
    /// fenced by <paramref name="lease"/> when given (see the remarks on the interface).
    /// </summary>
    /// <param name="record">The new content; its lease fields are ignored.</param>
    /// <param name="lease">The caller's lease, or null.</param>
    /// <param name="releaseLease">Whether to end <paramref name="lease"/> with this save.</param>
    /// <param name="cancellationToken">Token used to cancel the save.</param>
    /// <returns>The stored record at its new revision, or null when the save was refused.</returns>
    Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a job if it is still at <paramref name="expectedRevision"/>, fenced by <paramref name="lease"/>
    /// when given, exactly like <see cref="SaveAsync"/>.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="expectedRevision">The revision the caller's copy has.</param>
    /// <param name="lease">The caller's lease, or null.</param>
    /// <param name="cancellationToken">Token used to cancel the removal.</param>
    /// <returns>True when the job was removed or was already gone; false when the removal was refused.</returns>
    Task<bool> RemoveAsync(string jobId, long expectedRevision, StorageTransferLease? lease, CancellationToken cancellationToken);
}

/// <summary>Keeps jobs in memory; they end with the process. The default store.</summary>
public sealed class InMemoryStorageTransferJobStore : IStorageTransferJobStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StorageTransferJobRecord> _jobs = [with(StringComparer.Ordinal)];
    // The highest fencing token issued per id; kept after a removal so a re-added id never reuses a token.
    private readonly Dictionary<string, long> _fences = [with(StringComparer.Ordinal)];
    // The last revision of each removed id, so a re-added id continues above it.
    private readonly Dictionary<string, long> _revisions = [with(StringComparer.Ordinal)];
    private readonly TimeProvider _time;

    /// <summary>Creates an empty store.</summary>
    public InMemoryStorageTransferJobStore() : this(TimeProvider.System) { }

    /// <summary>Creates an empty store with a clock, for tests.</summary>
    /// <param name="time">The clock used for lease expiry.</param>
    public InMemoryStorageTransferJobStore(TimeProvider time) => _time = time;

    /// <inheritdoc />
    public Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<StorageTransferJobRecord>>([.. _jobs.Values]);
    }

    /// <inheritdoc />
    public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var stored = record with { Revision = _revisions.GetValueOrDefault(record.Id) + 1, LeaseOwner = null, LeaseExpiresAt = null, FencingToken = _fences.GetValueOrDefault(record.Id) };
            return Task.FromResult(_jobs.TryAdd(record.Id, stored) ? stored : null);
        }
    }

    /// <inheritdoc />
    public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_jobs.GetValueOrDefault(jobId));
    }

    /// <inheritdoc />
    public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var record) || record.Revision != expectedRevision) return Task.FromResult<StorageTransferLease?>(null);
            var now = _time.GetUtcNow();
            if (record.LeaseOwner is not null && record.LeaseOwner != workerId && record.LeaseExpiresAt > now) return Task.FromResult<StorageTransferLease?>(null);
            var token = Math.Max(record.FencingToken, _fences.GetValueOrDefault(jobId)) + 1;
            _fences[jobId] = token;
            var lease = new StorageTransferLease(jobId, workerId, token, now + duration);
            _jobs[jobId] = record with { LeaseOwner = workerId, LeaseExpiresAt = lease.ExpiresAt, FencingToken = lease.FencingToken };
            return Task.FromResult<StorageTransferLease?>(lease);
        }
    }

    /// <inheritdoc />
    public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease, out var record)) return Task.FromResult<StorageTransferLease?>(null);
            var renewed = lease with { ExpiresAt = _time.GetUtcNow() + duration };
            _jobs[lease.JobId] = record! with { LeaseExpiresAt = renewed.ExpiresAt };
            return Task.FromResult<StorageTransferLease?>(renewed);
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(StorageTransferLease lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease, out var record)) return Task.FromResult(false);
            _jobs[lease.JobId] = record! with { LeaseOwner = null, LeaseExpiresAt = null };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(record.Id, out var current) || current.Revision != record.Revision || !MayChange(current, lease))
                return Task.FromResult<StorageTransferJobRecord?>(null);
            var stored = lease is not null && !releaseLease
                ? record with { LeaseOwner = current.LeaseOwner, LeaseExpiresAt = current.LeaseExpiresAt }
                : record with { LeaseOwner = null, LeaseExpiresAt = null };
            stored = stored with { FencingToken = current.FencingToken, Revision = current.Revision + 1 };
            _jobs[record.Id] = stored;
            return Task.FromResult<StorageTransferJobRecord?>(stored);
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string jobId, long expectedRevision, StorageTransferLease? lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current)) return Task.FromResult(true);
            if (current.Revision != expectedRevision || !MayChange(current, lease)) return Task.FromResult(false);
            _jobs.Remove(jobId);
            _revisions[jobId] = current.Revision;
            return Task.FromResult(true);
        }
    }

    /// <summary>With a lease, only while it is current; without one, only while nobody holds an unexpired lease.</summary>
    private bool MayChange(StorageTransferJobRecord current, StorageTransferLease? lease) =>
        lease is not null
            ? IsCurrent(lease, out _)
            : current.LeaseOwner is null || !(current.LeaseExpiresAt > _time.GetUtcNow());

    private bool IsCurrent(StorageTransferLease lease, out StorageTransferJobRecord? record) =>
        _jobs.TryGetValue(lease.JobId, out record) &&
        record.LeaseOwner == lease.WorkerId &&
        record.FencingToken == lease.FencingToken &&
        record.LeaseExpiresAt > _time.GetUtcNow();
}
