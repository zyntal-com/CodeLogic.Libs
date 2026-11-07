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
