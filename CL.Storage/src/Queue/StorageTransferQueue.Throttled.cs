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
