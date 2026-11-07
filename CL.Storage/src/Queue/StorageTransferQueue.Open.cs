using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

public sealed partial class StorageTransferQueue
{

    /// <summary>Creates a queue and loads the store's jobs, applying restart rules to jobs left running.</summary>
    internal static async Task<Result<StorageTransferQueue>> OpenAsync(StorageLibrary library, StorageTransferQueueOptions options, CancellationToken cancellationToken)
    {
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageTransferQueue>.Failure(valid.Error!);
        var queue = new StorageTransferQueue(library, options);
        try
        {
            await queue.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<StorageTransferQueue>.Failure(StorageErrors.Cancelled("Opening the transfer queue was cancelled."));
        }
        catch (Exception error)
        {
            return Result<StorageTransferQueue>.Failure(StoreError(error));
        }
        if (options.StoreRefreshInterval is { } interval)
            queue._refresh = new Timer(_ => queue.Track(queue.RefreshAsync(CancellationToken.None)), null, interval, interval);
        await queue.PruneAsync().ConfigureAwait(false);
        queue.Pump();
        return Result<StorageTransferQueue>.Success(queue);
    }

    /// <summary>Gets whether the queue is paused; running jobs finish, but no new job starts.</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>Gets the current concurrency limit (it changes under <see cref="StorageTransferQueueOptions.AdaptiveConcurrency"/>).</summary>
    public int ConcurrencyLimit { get { lock (_gate) return _adaptiveLimit; } }

    /// <summary>Gets a snapshot of every job: highest priority first, then in queue order.</summary>
    public IReadOnlyList<StorageTransferJob> Jobs
    {
        get { lock (_gate) return [.. InQueueOrder(_jobs.Values).Select(entry => entry.Snapshot())]; }
    }

    /// <summary>Gets a snapshot of failed jobs, which <see cref="RetryFailedAsync"/> re-queues.</summary>
    public IReadOnlyList<StorageTransferJob> FailedJobs => [.. Jobs.Where(job => job.State == StorageTransferState.Failed)];

    /// <summary>Returns one job, or null.</summary>
    /// <param name="jobId">The job.</param>
    public StorageTransferJob? Get(string jobId)
    {
        if (jobId is null) return null;
        lock (_gate) return _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : null;
    }

    /// <summary>
    /// Adds a job. With a caller-chosen <paramref name="jobId"/> the call is idempotent: the same id with the
    /// same spec returns the existing job; the same id with a different spec fails with <c>storage.conflict</c>.
    /// A job the store accepted is returned even when the call was cancelled as the store answered.
    /// </summary>
    /// <param name="spec">The work.</param>
    /// <param name="priority">Higher starts first; 0 by default.</param>
    /// <param name="jobId">A stable id, or null to generate one.</param>
    /// <param name="cancellationToken">Token used to cancel storing the job.</param>
    public async Task<Result<StorageTransferJob>> EnqueueAsync(StorageTransferJobSpec spec, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var valid = spec.Validate();
        if (valid.IsFailure) return Result<StorageTransferJob>.Failure(valid.Error!);
        if (jobId is not null && string.IsNullOrWhiteSpace(jobId))
            return Result<StorageTransferJob>.Failure(StorageErrors.InvalidContent("A job id cannot be blank."));
        var id = jobId ?? Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (_disposed) return Result<StorageTransferJob>.Failure(Disposed());
            if (_jobs.TryGetValue(id, out var local))
                return SameOrConflict(local.Record, spec);
        }
        if (cancellationToken.IsCancellationRequested)
            return Result<StorageTransferJob>.Failure(StorageErrors.Cancelled("Adding the job was cancelled."));
        StorageTransferJobRecord? existing;
        try { existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result<StorageTransferJob>.Failure(StorageErrors.Cancelled("Adding the job was cancelled.")); }
        catch (Exception error) { return Result<StorageTransferJob>.Failure(StoreError(error)); }
        if (existing is not null)
        {
            Adopt(existing);
            return SameOrConflict(existing, spec);
        }

        var record = new StorageTransferJobRecord
        {
            Id = id,
            Spec = spec,
            Priority = priority,
            Order = NextOrder(),
            State = StorageTransferState.Queued,
            RetriesLeft = _options.AutomaticRetries,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        StorageTransferJobRecord? added;
        try { added = await _store.AddAsync(record, cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            // The store may have committed before it failed or the call was cancelled: its record decides.
            var (read, stored) = await TryGetAsync(id).ConfigureAwait(false);
            if (read && stored is not null)
            {
                Adopt(stored);
                Pump();
                return SameOrConflict(stored, spec);
            }
            return Result<StorageTransferJob>.Failure(error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? StorageErrors.Cancelled("Adding the job was cancelled.")
                : StoreError(error));
        }
        if (added is null)
        {
            var (read, raced) = await TryGetAsync(id).ConfigureAwait(false);
            if (!read) return Result<StorageTransferJob>.Failure(StorageErrors.Unavailable($"Job '{id}' already exists, but the job store could not be read."));
            if (raced is null) return Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{id}' could not be stored."));
            Adopt(raced);
            return SameOrConflict(raced, spec);
        }
        AdvanceOrder(added.Order);
        Entry entry;
        lock (_gate)
        {
            // A refresh or another call may have taken in the job meanwhile; a newer copy is kept.
            if (_jobs.TryGetValue(id, out var present))
            {
                entry = present;
                if (entry.Attempt is null && entry.HoldCount == 0 && entry.Record.Revision < added.Revision) entry.Record = added;
            }
            else
            {
                entry = new Entry(added);
                _jobs[id] = entry;
            }
            if (!entry.Record.IsFinished) MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        Pump();
        return Result<StorageTransferJob>.Success(entry.Snapshot());
    }

    /// <summary>Queues a copy between (or within) connections.</summary>
    public Task<Result<StorageTransferJob>> EnqueueCopyAsync(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a move between (or within) connections.</summary>
    public Task<Result<StorageTransferJob>> EnqueueMoveAsync(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.Move, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues an upload of a local file.</summary>
    public Task<Result<StorageTransferJob>> EnqueueUploadAsync(string localFilePath, string destinationConnectionId, string destinationPath, StorageUploadOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.UploadFile, SourcePath = localFilePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, UploadOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a download to a local file; <paramref name="options"/> can pick a version or a range.</summary>
    public Task<Result<StorageTransferJob>> EnqueueDownloadAsync(string sourceConnectionId, string sourcePath, string localFilePath, StorageDownloadOptions? options = null, StorageConflictPolicy? conflictPolicy = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.DownloadFile, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationPath = localFilePath, DownloadOptions = options, DownloadConflictPolicy = conflictPolicy }, priority, jobId, cancellationToken);

    /// <summary>Queues an upload of a local directory tree.</summary>
    public Task<Result<StorageTransferJob>> EnqueueUploadDirectoryAsync(string localDirectoryPath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.UploadDirectory, SourcePath = localDirectoryPath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a download of a directory tree to a local directory.</summary>
    public Task<Result<StorageTransferJob>> EnqueueDownloadDirectoryAsync(string sourceConnectionId, string sourcePath, string localDirectoryPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.DownloadDirectory, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationPath = localDirectoryPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Stops starting new jobs; running jobs finish.</summary>
    public void Pause()
    {
        lock (_gate) _paused = true;
    }

    /// <summary>Resumes starting queued jobs.</summary>
    public void Resume()
    {
        lock (_gate) _paused = false;
        Pump();
    }

    /// <summary>
    /// Holds one job. A running job's attempt is stopped, and the call returns once it has: successfully when
    /// the job is paused, with <c>storage.conflict</c> when the attempt finished first (or committed its
    /// destination, which makes the job need reconciliation). The wait is bounded by
    /// <see cref="StorageTransferQueueOptions.ControlTimeout"/> and <paramref name="cancellationToken"/>; a call
    /// that stops waiting first fails with <c>storage.timeout</c> or <c>storage.cancelled</c>, and the request
    /// still takes effect when the attempt stops. A resumable transfer keeps its staged data and continues from
    /// it when resumed. Pausing a paused job succeeds.
    /// </summary>
    public Task<Result> PauseJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, ControlRequest.Pause, cancellationToken);

    /// <summary>Releases a paused job back into the queue.</summary>
    public Task<Result> ResumeJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        ChangeAsync(jobId, cancellationToken, entry => entry.Record.State == StorageTransferState.Paused
            ? (Result.Success(), entry.Record with { State = StorageTransferState.Queued, NextAttemptAt = null })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is not paused.")), null));

    /// <summary>
    /// Cancels a queued, paused, or running job. For a running job the call returns once its attempt has
    /// stopped: successfully when the job is cancelled, with <c>storage.conflict</c> when the attempt finished
    /// first (or committed its destination, which makes the job need reconciliation). The wait is bounded as
    /// for <see cref="PauseJobAsync"/>: after <see cref="StorageTransferQueueOptions.ControlTimeout"/> the call
    /// fails with <c>storage.timeout</c>, and the cancel still takes effect when the attempt stops.
    /// </summary>
    public Task<Result> CancelAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, ControlRequest.Cancel, cancellationToken);

    /// <summary>
    /// Re-queues a failed, cancelled, blocked, interrupted, or reconciled job. Its retries and attempt count
    /// start over, so backoff starts from the base delay again.
    /// </summary>
    public Task<Result> RetryAsync(string jobId, CancellationToken cancellationToken = default) =>
        ChangeAsync(jobId, cancellationToken, entry => entry.Record.State is StorageTransferState.Failed or StorageTransferState.Cancelled or
            StorageTransferState.Blocked or StorageTransferState.NeedsReconciliation or StorageTransferState.Interrupted
            ? (Result.Success(), entry.Record with
            {
                State = StorageTransferState.Queued,
                BlockReason = null,
                Attempts = 0,
                RetriesLeft = _options.AutomaticRetries,
                NextAttemptAt = null,
                Failure = null,
                FinishedAt = null
            })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is {entry.Record.State}; only stopped jobs can be retried.")), null));

    /// <summary>Re-queues every failed job.</summary>
    /// <returns>The number of jobs re-queued.</returns>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var job in FailedJobs)
            if ((await RetryAsync(job.Id, cancellationToken).ConfigureAwait(false)).IsSuccess) count++;
        return count;
    }

    /// <summary>Changes a waiting (queued or paused) job's priority; higher starts first.</summary>
    public Task<Result> SetPriorityAsync(string jobId, int priority, CancellationToken cancellationToken = default) =>
        ChangeAsync(jobId, cancellationToken, entry => entry.Record.State is StorageTransferState.Queued or StorageTransferState.Paused
            ? (Result.Success(), entry.Record with { Priority = priority })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is {entry.Record.State}; only waiting jobs can be reprioritized.")), null));
}
