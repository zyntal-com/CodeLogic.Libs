using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

/// <summary>Limits, retry behaviour, and storage for a <see cref="StorageTransferQueue"/>.</summary>
public sealed record StorageTransferQueueOptions
{
    /// <summary>Gets how many jobs run at once across all connections (the ceiling under adaptive concurrency).</summary>
    public int MaxConcurrentTransfers { get; init; } = 2;
    /// <summary>Gets how many jobs may use one connection at once, like FileZilla's per-server limit.</summary>
    public int MaxTransfersPerConnection { get; init; } = 2;
    /// <summary>
    /// Gets how often a job that failed transiently is retried automatically before it is marked failed. A
    /// failure of the job store itself does not use up a retry; such attempts are tried again with a growing
    /// delay, and after eight in a row that the store stopped, the job fails with <c>storage.unavailable</c>
    /// (only in this queue's view when the store cannot record even that).
    /// </summary>
    public int AutomaticRetries { get; init; } = 3;
    /// <summary>Gets the first retry delay; later retries double it, with jitter, up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Gets the longest retry delay; a server's <c>Retry-After</c> is honoured even when longer (up to 30 days).</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets whether the queue starts paused.</summary>
    public bool StartPaused { get; init; }
    /// <summary>Gets where jobs are kept; in memory when null. A durable store makes jobs survive restarts.</summary>
    public IStorageTransferJobStore? Store { get; init; }
    /// <summary>Gets this queue's worker identity for leases. Keep it stable across restarts to reclaim your own jobs at once.</summary>
    public string WorkerId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Gets how long a running job's lease lasts between renewals (renewed every third of it); one second to one day.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Gets whether a job found running from an earlier process goes straight back to the queue when its
    /// checkpoint shows the destination was never touched; otherwise, and always when it may have been, it
    /// becomes <see cref="StorageTransferState.Interrupted"/>.
    /// </summary>
    public bool RequeueInterruptedWhenSafe { get; init; } = true;
    /// <summary>
    /// Gets how many finished jobs are kept; the oldest beyond it are removed from the store (checked after
    /// every attempt, every change that finishes a job, and when the queue opens). Null keeps all.
    /// </summary>
    public int? MaxFinishedJobs { get; init; } = 1_000;
    /// <summary>
    /// Gets whether concurrency adapts: it starts at one, adds a slot after each success, and halves after a
    /// transient failure, never exceeding <see cref="MaxConcurrentTransfers"/>.
    /// </summary>
    public bool AdaptiveConcurrency { get; init; }
    /// <summary>
    /// Gets the minimum time between two progress reports of one job. The last report of an attempt always
    /// arrives, and no report arrives after the attempt ended.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>
    /// Gets a context to raise <see cref="StorageTransferQueue.JobChanged"/>, <see cref="StorageTransferQueue.JobRemoved"/>,
    /// and <see cref="StorageTransferQueue.ProgressChanged"/> on, such as a UI thread. An event the context refuses
    /// (it throws, for example after the UI shut down) is dropped. Without a context, handlers run inline on the
    /// queue's own threads (progress on the transfer's), never under one of the queue's locks; they must be
    /// quick and must not wait synchronously on the queue's methods: an attempt does not end while one of its
    /// progress handlers runs, so a handler that blocks on pausing, cancelling, or removing its own job stalls
    /// that job until <see cref="ControlTimeout"/> ends the wait.
    /// </summary>
    public SynchronizationContext? EventContext { get; init; }
    /// <summary>Gets how often to read the store for jobs changed by other processes; never when null. Up to one day.</summary>
    public TimeSpan? StoreRefreshInterval { get; init; }
    /// <summary>
    /// Gets how long disposing the queue waits for running jobs to stop and record where they stopped. A job
    /// still running after it goes on in the background and records its outcome when it ends.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// Gets how long <see cref="StorageTransferQueue.PauseJobAsync"/>, <see cref="StorageTransferQueue.CancelAsync"/>,
    /// and <see cref="StorageTransferQueue.RemoveAsync"/> wait for a running job's attempt to stop, since a
    /// provider may not honour cancellation at once (or at all). The request is recorded before the wait and
    /// still takes effect when the attempt stops; a call that stops waiting first fails with
    /// <c>storage.timeout</c>. Zero returns as soon as the request is recorded. Up to one day; 30 seconds by default.
    /// </summary>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal Result Validate()
    {
        if (MaxConcurrentTransfers is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxConcurrentTransfers must be between 1 and 64."));
        if (MaxTransfersPerConnection is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxTransfersPerConnection must be between 1 and 64."));
        if (AutomaticRetries is < 0 or > 50) return Result.Failure(StorageErrors.InvalidContent("AutomaticRetries must be between 0 and 50."));
        if (RetryBaseDelay < TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay) return Result.Failure(StorageErrors.InvalidContent("Retry delays must be non-negative, and RetryMaxDelay at least RetryBaseDelay."));
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromDays(1)) return Result.Failure(StorageErrors.InvalidContent("LeaseDuration must be between one second and one day."));
        if (MaxFinishedJobs is < 0) return Result.Failure(StorageErrors.InvalidContent("MaxFinishedJobs cannot be negative."));
        if (string.IsNullOrWhiteSpace(WorkerId)) return Result.Failure(StorageErrors.InvalidContent("WorkerId is required."));
        if (ProgressInterval < TimeSpan.Zero) return Result.Failure(StorageErrors.InvalidContent("ProgressInterval cannot be negative."));
        if (StoreRefreshInterval is { } refresh && (refresh <= TimeSpan.Zero || refresh > TimeSpan.FromDays(1)))
            return Result.Failure(StorageErrors.InvalidContent("StoreRefreshInterval must be positive and at most one day."));
        if (ShutdownTimeout < TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromDays(1)) return Result.Failure(StorageErrors.InvalidContent("ShutdownTimeout must be between zero and one day."));
        if (ControlTimeout < TimeSpan.Zero || ControlTimeout > TimeSpan.FromDays(1)) return Result.Failure(StorageErrors.InvalidContent("ControlTimeout must be between zero and one day."));
        return Result.Success();
    }
}

/// <summary>
/// Runs transfers in the background, like FileZilla's queue: a global and a per-connection limit (optionally
/// adaptive), integer priorities, pause and resume for the queue or one job, reordering, cancellation,
/// exponential backoff for transient failures, and states for jobs that need a person (blocked on trust or
/// credentials, or needing reconciliation). Jobs are data and live in an <see cref="IStorageTransferJobStore"/>,
/// so a durable store makes them survive restarts; leases with fencing keep two processes from running one job.
/// </summary>
public sealed class StorageTransferQueue : IAsyncDisposable
{
    /// <summary>The longest single timer wait; longer waits are taken in steps.</summary>
    private static readonly TimeSpan LongestTimer = TimeSpan.FromDays(1);
    /// <summary>The longest <c>Retry-After</c> honoured.</summary>
    private static readonly TimeSpan LongestRetryAfter = TimeSpan.FromDays(30);

    private readonly StorageLibrary _library;
    private readonly StorageTransferQueueOptions _options;
    private readonly IStorageTransferJobStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _jobs = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, int> _activeByConnection = [with(StringComparer.OrdinalIgnoreCase)];
    private readonly HashSet<string> _removedWhileRefreshing = [with(StringComparer.Ordinal)];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _attempts = [];
    // Background work (store refreshes, recovery, wake-ups) that DisposeAsync waits for.
    private readonly HashSet<Task> _background = [];
    // Store calls in flight, and whether the store is closed to this queue (after DisposeAsync).
    private int _storeCalls;
    private bool _storeClosed;
    private TaskCompletionSource _storeQuiet = CompletedIdle();
    private TaskCompletionSource _idle = CompletedIdle();
    private Timer? _refresh;
    private int _running;
    private int _refreshing;
    private bool _recovering;
    private bool _pruning;
    private bool _pruneAgain;
    private DateTimeOffset? _wakeAt;
    private int _adaptiveLimit;
    private long _order;
    private bool _paused;
    private bool _disposed;

    private StorageTransferQueue(StorageLibrary library, StorageTransferQueueOptions options)
    {
        _library = library;
        _options = options;
        _store = new GuardedStore(this, options.Store ?? new InMemoryStorageTransferJobStore());
        _paused = options.StartPaused;
        _adaptiveLimit = options.AdaptiveConcurrency ? 1 : options.MaxConcurrentTransfers;
    }

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

    /// <summary>
    /// Raised when a job is added or changes state. Handlers must be quick and must not wait synchronously on
    /// this queue's methods; see <see cref="StorageTransferQueueOptions.EventContext"/>.
    /// </summary>
    public event Action<StorageTransferJob>? JobChanged;

    /// <summary>Raised once when a job leaves the queue: removed, pruned from history, or gone from the store.</summary>
    public event Action<StorageTransferJob>? JobRemoved;

    /// <summary>
    /// Raised with throttled progress of running jobs, one report at a time per job. Without an
    /// <see cref="StorageTransferQueueOptions.EventContext"/> the handler runs on the transfer's thread and the
    /// attempt waits for it before it ends, so it must not wait synchronously on this queue's methods.
    /// </summary>
    public event Action<StorageTransferJob>? ProgressChanged;

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

    /// <summary>
    /// Applies requests that arrived after the attempt decided its outcome (or while it was stepping aside) to
    /// the job as it is now, as if they had been made then; the job is held so it does not start in between.
    /// Every request is answered and the hold released, whatever fails.
    /// </summary>
    private async Task SettleAsync(Entry entry)
    {
        Pending[] late = [];
        try
        {
            lock (_gate)
            {
                late = [.. entry.Requests.OrderByDescending(pending => pending.Request)];
                entry.Requests.Clear();
            }
            foreach (var pending in late)
            {
                Result answer;
                try { answer = await ControlAsync(entry.Record.Id, pending.Request, CancellationToken.None, settling: true).ConfigureAwait(false); }
                catch (Exception error) { answer = Result.Failure(StorageErrors.FromException(error, "Changing a queued job")); }
                pending.Done.TrySetResult(answer);
            }
        }
        finally
        {
            foreach (var pending in late)
                pending.Done.TrySetResult(Result.Failure(StorageErrors.Unavailable($"Job '{entry.Record.Id}' could not be changed.")));
            lock (_gate) entry.HoldCount--;
        }
    }

    /// <summary>
    /// Gives up a job this worker could not claim or start, taking the store's record. After a store failure,
    /// or a claim refused although the record did not change, the job waits (locally) before it is tried again,
    /// so the store is not hammered. A job whose claims or saves keep failing (<paramref name="saveFailed"/>) waits
    /// longer each time, and fails after <see cref="MaxStoreFailures"/> in a row.
    /// </summary>
    private async Task StepAsideAsync(Entry entry, StorageTransferJobRecord claimed, bool storeFailed = false, bool refused = false, bool saveFailed = false)
    {
        var (read, fresh) = await TryGetAsync(claimed.Id).ConfigureAwait(false);
        // A record rewritten by a newer schema is left for that version: the job leaves this queue's view.
        var record = read ? Readable(fresh) : claimed;
        Error? gaveUp = null;
        if (record is not null && saveFailed)
        {
            int failures;
            lock (_gate) failures = ++entry.StoreFailures;
            if (failures >= MaxStoreFailures)
            {
                gaveUp = StoreGaveUp(failures);
                var failed = record with { State = StorageTransferState.Failed, FinishedAt = DateTimeOffset.UtcNow, Failure = StorageTransferFailure.From(gaveUp) };
                var (stored, _) = await SaveControlAsync(failed, CancellationToken.None).ConfigureAwait(false);
                // When the store cannot record even that, the job stops in this queue's view only.
                record = stored ?? failed;
            }
            else
            {
                lock (_gate) entry.NotBefore = DateTimeOffset.UtcNow + StoreFailureDelay(failures);
            }
        }
        else if (record is not null && (storeFailed || !read || (refused && record.Revision == claimed.Revision)))
        {
            lock (_gate) entry.NotBefore = DateTimeOffset.UtcNow + StoreRetryDelay;
        }
        var settle = false;
        try
        {
            settle = EndAttempt(entry, record, null, null);
            if (gaveUp is not null) await PublishOutcomeAsync(record, gaveUp).ConfigureAwait(false);
        }
        finally
        {
            if (settle) await SettleAsync(entry).ConfigureAwait(false);
        }
        UpdateIdle();
        Pump();
    }

    private TimeSpan StoreRetryDelay => _options.RetryBaseDelay > TimeSpan.FromSeconds(1) ? _options.RetryBaseDelay : TimeSpan.FromSeconds(1);

    /// <summary>How many attempts in a row the job store may stop before the job fails.</summary>
    private const int MaxStoreFailures = 8;

    /// <summary>The wait after the <paramref name="failures"/>th store failure in a row: doubling from the retry base delay (at least 100 ms), up to the retry maximum.</summary>
    private TimeSpan StoreFailureDelay(int failures)
    {
        var floor = TimeSpan.FromMilliseconds(100);
        var first = _options.RetryBaseDelay > floor ? _options.RetryBaseDelay : floor;
        var ceiling = _options.RetryMaxDelay > floor ? _options.RetryMaxDelay : floor;
        var delay = first.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30));
        return delay >= ceiling.TotalMilliseconds ? ceiling : TimeSpan.FromMilliseconds(delay);
    }

    private static Error StoreGaveUp(int failures) =>
        StorageErrors.Unavailable($"The job store failed to record this job {failures} times in a row, so it was stopped. Retry it when the store works again.");

    /// <summary>Runs one attempt of a job.</summary>
    private async Task<(Result Result, StorageTransferReport? Report)> ExecuteAsync(
        StorageTransferJobSpec spec,
        StorageResumeToken? resume,
        IProgress<StorageTransferProgress> progress,
        Func<StorageTransferPhase, CancellationToken, Task> phase,
        CancellationToken cancellationToken)
    {
        switch (spec.Kind)
        {
            case StorageTransferKind.Copy or StorageTransferKind.Move:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                // A token continues staged data and replaces the destination, so it is replayed only for a job
                // that allows that; any other job starts its staging again.
                if (resume is not null && ResumeAllowed(options))
                    options = options with { ResumeToken = resume };
                var report = spec.Kind == StorageTransferKind.Copy
                    ? await _library.CopyAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false)
                    : await _library.MoveAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (report.ToResult(), report);
            }
            case StorageTransferKind.UploadFile:
            {
                // The queue cannot see when an upload commits (a provider may write in place), so from here
                // on a restart treats the destination as possibly written.
                await phase(StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                var uploaded = await _library.GetStorage(spec.DestinationConnectionId!)
                    .UploadFileAsync(spec.DestinationPath, spec.SourcePath, (spec.UploadOptions ?? new StorageUploadOptions()) with { Progress = progress }, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            case StorageTransferKind.DownloadFile:
            {
                // The same for downloads: a resumed download appends to the local file in place.
                await phase(StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                var downloaded = await _library.GetStorage(spec.SourceConnectionId!)
                    .DownloadToFileAsync(spec.SourcePath, spec.DestinationPath, (spec.DownloadOptions ?? new StorageDownloadOptions()) with { Progress = progress },
                        conflictPolicy: spec.DownloadConflictPolicy, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
            case StorageTransferKind.UploadDirectory:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var uploaded = await _library.UploadDirectoryAsync(spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            default:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var downloaded = await _library.DownloadDirectoryAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
        }
    }

    /// <summary>Whether a job's options let a stored resume token replace its destination.</summary>
    internal static bool ResumeAllowed(StorageTransferOptions options) =>
        options.ConflictPolicy == StorageConflictPolicy.Resume ||
        (options.Overwrite && options.ConflictPolicy is null or StorageConflictPolicy.Overwrite);

    /// <summary>
    /// Decides a job's next record after an attempt. A transfer that succeeded is Completed, and one that
    /// committed its destination but did not finish (a move whose source is still there) needs reconciliation,
    /// even if it was being paused, cancelled, or shut down as it got there: re-running it would repeat work
    /// already done. An attempt stopped by the job store is tried again without using up a retry, until
    /// <see cref="MaxStoreFailures"/> in a row make the job fail (<c>GaveUp</c>).
    /// </summary>
    private (StorageTransferJobRecord Next, bool Transient, bool GaveUp) Decide(
        Entry entry,
        Result result,
        StorageTransferReport? report,
        StorageTransferPhase phase,
        LeaseHolder holder,
        ControlRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        StorageTransferJobRecord current;
        lock (_gate) current = entry.Record;
        var record = current with
        {
            Checkpoint = new StorageTransferCheckpoint(phase, report?.ResumeToken ?? (result.IsFailure ? current.Checkpoint.ResumeToken : null)),
            Failure = StorageTransferFailure.From(result.Error)
        };
        if (result.IsSuccess)
            return (record with { State = StorageTransferState.Completed, FinishedAt = now, Failure = null, Checkpoint = new StorageTransferCheckpoint(phase) }, false, false);

        var error = result.Error!;
        if (report?.Outcome == StorageTransferOutcome.NeedsReconciliation || error.Code == StorageErrors.PartialFailureCode || StorageErrorInfo.DestinationCommitted(error))
            return (record with { State = StorageTransferState.NeedsReconciliation }, false, false);
        if (request == ControlRequest.Pause)
            return (record with { State = StorageTransferState.Paused, Failure = null }, false, false);
        if (request == ControlRequest.Cancel)
            return (record with { State = StorageTransferState.Cancelled, FinishedAt = now, Failure = null }, false, false);
        if (_shutdown.IsCancellationRequested)
            return (Recover(record) with { Failure = null }, false, false);
        if (holder.StoreFailed)
        {
            // The job store failed, not the transfer: try again later without using up a retry, but not for ever.
            int failures;
            lock (_gate) failures = ++entry.StoreFailures;
            if (failures >= MaxStoreFailures)
                return (record with { State = StorageTransferState.Failed, FinishedAt = now, Failure = StorageTransferFailure.From(StoreGaveUp(failures)) }, false, true);
            return (record with
            {
                State = StorageTransferState.Queued,
                NextAttemptAt = now + StoreFailureDelay(failures),
                Failure = StorageTransferFailure.From(StorageErrors.Unavailable("The transfer was stopped because the job store failed."))
            }, false, false);
        }
        if (BlockReasonFor(error) is { } reason)
            return (record with { State = StorageTransferState.Blocked, BlockReason = reason }, false, false);
        if (StorageErrorInfo.IsTransient(error) && record.RetriesLeft > 0)
        {
            return (record with
            {
                State = StorageTransferState.Queued,
                RetriesLeft = record.RetriesLeft - 1,
                NextAttemptAt = now + Backoff(record.Attempts, error)
            }, true, false);
        }
        return (record with { State = StorageTransferState.Failed, FinishedAt = now }, StorageErrorInfo.IsTransient(error), false);
    }

    /// <summary>Trust and credential failures need a person, not a retry.</summary>
    internal static StorageTransferBlockReason? BlockReasonFor(Error error)
    {
        if (error.Code == StorageErrors.HostKeyRejectedCode) return StorageTransferBlockReason.Trust;
        if (error.Code == StorageErrors.AuthenticationFailedCode) return StorageTransferBlockReason.Credential;
        if (error.Code != StorageErrors.TlsFailureCode) return null;
        StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var reason);
        return reason switch
        {
            TlsDiagnosis.ServerCertificateRejected => StorageTransferBlockReason.Trust,
            TlsDiagnosis.ClientCertificateRejected => StorageTransferBlockReason.Credential,
            _ => null
        };
    }

    /// <summary>Exponential backoff with ±50% jitter, clamped, and never shorter than a server's Retry-After (up to 30 days).</summary>
    private TimeSpan Backoff(int attempt, Error error)
    {
        var exponential = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 20)));
        var jittered = exponential * (0.5 + Random.Shared.NextDouble());
        var delay = TimeSpan.FromMilliseconds(Math.Min(jittered, _options.RetryMaxDelay.TotalMilliseconds));
        if (!StorageErrorInfo.TryGetRetryAfter(error, out var requested) || requested <= delay) return delay;
        return requested > LongestRetryAfter ? LongestRetryAfter : requested;
    }

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

    /// <summary>
    /// Keeps the latest report on the job and forwards it at most every <see cref="StorageTransferQueueOptions.ProgressInterval"/>.
    /// Handlers run outside every lock, one at a time: a report that comes while one is being forwarded is held
    /// back like a throttled one, so a slow handler never blocks the transfer. <see cref="CloseAsync"/> waits for
    /// a report being forwarded, then forwards a report held back, and ignores any that come later.
    /// </summary>
    private sealed class Throttled(StorageTransferQueue queue, Entry entry) : IProgress<StorageTransferProgress>
    {
        private readonly Lock _sync = new();
        private long _lastForward = long.MinValue;
        private bool _pending;
        private bool _closed;
        // Completed while no report is being forwarded.
        private TaskCompletionSource _forwarded = CompletedForward();

        public void Report(StorageTransferProgress value)
        {
            StorageTransferJob snapshot;
            lock (_sync)
            {
                if (_closed) return;
                lock (queue._gate) entry.Progress = value;
                var now = Environment.TickCount64;
                var early = !value.IsCompleted && _lastForward != long.MinValue && now - _lastForward < queue._options.ProgressInterval.TotalMilliseconds;
                if (early || !_forwarded.Task.IsCompleted)
                {
                    _pending = true;
                    return;
                }
                _lastForward = now;
                _pending = false;
                _forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (queue._gate) snapshot = entry.Snapshot();
            }
            Forward(snapshot);
        }

        public async Task CloseAsync()
        {
            Task forwarding;
            lock (_sync)
            {
                if (_closed) return;
                _closed = true;
                forwarding = _forwarded.Task;
            }
            // No report may arrive after the attempt ended, so one being forwarded elsewhere is waited for.
            await forwarding.ConfigureAwait(false);
            StorageTransferJob snapshot;
            lock (_sync)
            {
                if (!_pending) return;
                _pending = false;
                lock (queue._gate) snapshot = entry.Snapshot();
            }
            queue.Raise(queue.ProgressChanged, snapshot);
        }

        private void Forward(StorageTransferJob snapshot)
        {
            try
            {
                queue.Raise(queue.ProgressChanged, snapshot);
            }
            finally
            {
                TaskCompletionSource done;
                lock (_sync) done = _forwarded;
                done.TrySetResult();
            }
        }

        private static TaskCompletionSource CompletedForward()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.SetResult();
            return done;
        }
    }

    /// <summary>The queue's view of the job store: every call is counted, and refused once the queue closed the store.</summary>
    private sealed class GuardedStore(StorageTransferQueue queue, IStorageTransferJobStore inner) : IStorageTransferJobStore
    {
        private async Task<T> CallAsync<T>(Func<Task<T>> call)
        {
            queue.EnterStore();
            try { return await call().ConfigureAwait(false); }
            finally { queue.LeaveStore(); }
        }

        public Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken) =>
            CallAsync(() => inner.LoadAsync(cancellationToken));

        public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken) =>
            CallAsync(() => inner.AddAsync(record, cancellationToken));

        public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken) =>
            CallAsync(() => inner.GetAsync(jobId, cancellationToken));

        public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken) =>
            CallAsync(() => inner.TryClaimAsync(jobId, workerId, expectedRevision, duration, cancellationToken));

        public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken) =>
            CallAsync(() => inner.RenewAsync(lease, duration, cancellationToken));

        public Task<bool> ReleaseAsync(StorageTransferLease lease, CancellationToken cancellationToken) =>
            CallAsync(() => inner.ReleaseAsync(lease, cancellationToken));

        public Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken) =>
            CallAsync(() => inner.SaveAsync(record, lease, releaseLease, cancellationToken));

        public Task<bool> RemoveAsync(string jobId, long expectedRevision, StorageTransferLease? lease, CancellationToken cancellationToken) =>
            CallAsync(() => inner.RemoveAsync(jobId, expectedRevision, lease, cancellationToken));
    }

    private sealed class LeaseHolder(StorageTransferLease lease)
    {
        private StorageTransferLease _lease = lease;
        public StorageTransferLease Lease { get => Volatile.Read(ref _lease); set => Volatile.Write(ref _lease, value); }
        public bool Lost { get; set; }
        public bool StoreFailed { get; set; }
    }

    private sealed class Entry(StorageTransferJobRecord record)
    {
        public StorageTransferJobRecord Record { get; set; } = record;
        public StorageTransferProgress? Progress { get; set; }
        public StorageTransferReport? LastReport { get; set; }
        public CancellationTokenSource Cancellation { get; set; } = new();
        public Task? Attempt { get; set; }
        /// <summary>Requests made while an attempt runs, answered when it ends.</summary>
        public List<Pending> Requests { get; } = [];
        /// <summary>Above zero while the job is being removed or settled; it does not start then.</summary>
        public int HoldCount { get; set; }
        /// <summary>When the job may be tried again after a store failure or a refused claim (local only).</summary>
        public DateTimeOffset NotBefore { get; set; } = DateTimeOffset.MinValue;
        /// <summary>When recovering a job left running may be tried again.</summary>
        public DateTimeOffset RecoverAfter { get; set; } = DateTimeOffset.MinValue;
        /// <summary>Attempts in a row stopped because the job store failed to save (local only).</summary>
        public int StoreFailures { get; set; }

        public StorageTransferJob Snapshot() => new() { Record = Record, Progress = Progress, LastReport = LastReport };
    }
}
