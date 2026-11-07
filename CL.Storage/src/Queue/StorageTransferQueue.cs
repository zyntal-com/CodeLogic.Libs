using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

/// <summary>
/// Runs transfers in the background, like FileZilla's queue: a global and a per-connection limit (optionally
/// adaptive), integer priorities, pause and resume for the queue or one job, reordering, cancellation,
/// exponential backoff for transient failures, and states for jobs that need a person (blocked on trust or
/// credentials, or needing reconciliation). Jobs are data and live in an <see cref="IStorageTransferJobStore"/>,
/// so a durable store makes them survive restarts; leases with fencing keep two processes from running one job.
/// </summary>
public sealed partial class StorageTransferQueue : IAsyncDisposable
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

    /// <summary>How many attempts in a row the job store may stop before the job fails.</summary>
    private const int MaxStoreFailures = 8;
}
