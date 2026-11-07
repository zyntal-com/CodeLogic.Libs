using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

/// <summary>
/// Runs named background jobs on intervals, cron expressions or once.
/// <list type="bullet">
/// <item>A job that throws never stops the scheduler: the exception becomes the run's error
/// (<c>scheduling.job_failed</c>), reported through <see cref="JobCompleted"/> and <see cref="Jobs"/>.</item>
/// <item>Overlap: by default a due run is skipped while the previous run is still going
/// (<see cref="JobOptions.AllowOverlap"/>).</item>
/// <item>Concurrency: with <c>maxConcurrency</c> &gt; 0, runs beyond the limit wait for a slot.</item>
/// <item>Timers come from the <see cref="TimeProvider"/>, so a fake clock drives the scheduler deterministically.</item>
/// </list>
/// Thread-safe. Dispose (or <see cref="StopAsync"/>) to stop it.
/// </summary>
public sealed partial class JobScheduler : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly SemaphoreSlim? _concurrency;
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _activeRuns;
    private TaskCompletionSource? _idle;
    private bool _stopped;

    /// <summary>Creates a scheduler.</summary>
    /// <param name="timeProvider">Clock and timer source; null uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="maxConcurrency">Most runs at once across all jobs; 0 = unlimited.</param>
    public JobScheduler(TimeProvider? timeProvider = null, int maxConcurrency = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxConcurrency);
        _time = timeProvider ?? TimeProvider.System;
        _concurrency = maxConcurrency > 0 ? new SemaphoreSlim(maxConcurrency, maxConcurrency) : null;
    }

    /// <summary>Raised after every run, scheduled or triggered. Exceptions from handlers are ignored.</summary>
    public event Action<JobRunCompleted>? JobCompleted;
}
