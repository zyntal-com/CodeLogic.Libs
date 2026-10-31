using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

/// <summary>When a job runs. A closed hierarchy: a switch over it must handle every case.</summary>
public closed record JobSchedule
{
    /// <summary>
    /// Every <paramref name="Period"/>, at a fixed rate measured from the scheduled times (not from when the previous
    /// run finished). Missed slots are skipped, not run in a burst.
    /// </summary>
    /// <param name="Period">The time between runs. Must be positive.</param>
    /// <param name="InitialDelay">Delay before the first run; null means one <paramref name="Period"/>.</param>
    public sealed record Interval(TimeSpan Period, TimeSpan? InitialDelay = null) : JobSchedule;

    /// <summary>On a 5-field cron expression (see <see cref="CronParser"/>), to the minute.</summary>
    /// <param name="Expression">The cron expression, e.g. <c>"0 3 * * *"</c>.</param>
    /// <param name="TimeZone">The zone the expression is written in; null means UTC.</param>
    public sealed record Cron(string Expression, TimeZoneInfo? TimeZone = null) : JobSchedule;

    /// <summary>Once, at <paramref name="At"/> (immediately when that is already past).</summary>
    /// <param name="At">When to run.</param>
    public sealed record Once(DateTimeOffset At) : JobSchedule;
}

/// <summary>Per-job settings for <see cref="JobScheduler.Schedule"/>.</summary>
public sealed record JobOptions
{
    /// <summary>
    /// Whether a run may start while the previous one is still going. Default false: a due run is skipped
    /// (and <see cref="JobScheduler.TriggerAsync"/> returns <c>scheduling.already_running</c>).
    /// </summary>
    public bool AllowOverlap { get; init; }

    /// <summary>Cancel the job's token after this long; the run then reports <c>scheduling.job_timeout</c>. Null = no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Random delay, as a fraction 0..1 of the interval (or of the time until the next cron slot), added to each
    /// scheduled run so many instances don't fire in lockstep. Default 0.
    /// </summary>
    public double Jitter { get; init; }
}

/// <summary>A snapshot of a scheduled job.</summary>
/// <param name="Name">The job's name.</param>
/// <param name="Schedule">Its schedule.</param>
/// <param name="LastRun">When the last run started.</param>
/// <param name="LastDuration">How long the last run took.</param>
/// <param name="LastError">The last run's error; null when it succeeded.</param>
/// <param name="NextRun">When the next scheduled run is due; null when none is (a finished <see cref="JobSchedule.Once"/>).</param>
/// <param name="RunCount">Completed runs, scheduled and triggered.</param>
/// <param name="IsRunning">Whether a run is in progress (or waiting for a concurrency slot).</param>
/// <param name="IsPaused">Whether scheduled runs are paused.</param>
public sealed record JobInfo(string Name, JobSchedule Schedule, DateTimeOffset? LastRun, TimeSpan? LastDuration,
    Error? LastError, DateTimeOffset? NextRun, long RunCount, bool IsRunning, bool IsPaused);

/// <summary>A finished run.</summary>
/// <param name="Name">The job's name.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Error">Its error; null when it succeeded.</param>
public readonly record struct JobRunCompleted(string Name, DateTimeOffset StartedAt, TimeSpan Duration, Error? Error);

/// <summary>A scheduled job. Disposing it unschedules the job.</summary>
public interface IJobHandle : IDisposable
{
    /// <summary>The job's name.</summary>
    string Name { get; }

    /// <summary>Skips scheduled runs until <see cref="Resume"/> (manual triggers still run).</summary>
    void Pause();

    /// <summary>Resumes scheduled runs from the next due time.</summary>
    void Resume();
}

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
public sealed class JobScheduler : IAsyncDisposable
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

    /// <summary>A snapshot of every scheduled job, ordered by name.</summary>
    public IReadOnlyList<JobInfo> Jobs
    {
        get
        {
            lock (_sync)
            {
                var list = new List<JobInfo>(_jobs.Count);
                foreach (var job in _jobs.Values)
                    list.Add(job.ToInfo());
                list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
                return list;
            }
        }
    }

    /// <summary>Schedules a job.</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="schedule">When it runs.</param>
    /// <param name="work">The work; observe the token (cancelled on timeout and when the scheduler stops).</param>
    /// <param name="options">Overlap, timeout and jitter; null uses the defaults.</param>
    /// <returns>A handle to pause, resume or unschedule (dispose) the job.</returns>
    /// <exception cref="ArgumentException">The name is taken, or the schedule or options are invalid.</exception>
    /// <exception cref="InvalidOperationException">The scheduler has been stopped.</exception>
    public IJobHandle Schedule(string name, JobSchedule schedule, Func<CancellationToken, ValueTask> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(work);
        options ??= new JobOptions();
        Validate(schedule, options);

        var job = new Job(name, schedule, work, options);
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_jobs.ContainsKey(name))
                throw new ArgumentException($"A job named '{name}' is already scheduled.", nameof(name));
            job.NextSlot = FirstSlot(schedule, now);
            job.NextRun = WithJitter(job, job.NextSlot, now);
            job.Timer = _time.CreateTimer(static state => ((JobTimerState)state!).Fire(), new JobTimerState(this, job),
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _jobs.Add(name, job);
        }
        Arm(job, now);
        return new JobHandle(this, job);
    }

    /// <summary>Unschedules a job. A run in progress finishes; no new runs start.</summary>
    /// <param name="name">The job's name.</param>
    /// <returns><c>true</c> when a job was removed.</returns>
    public bool Unschedule(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Job? job;
        lock (_sync)
        {
            if (!_jobs.Remove(name, out job))
                return false;
            job.Removed = true;
        }
        job.Timer?.Dispose();
        return true;
    }

    /// <summary>
    /// Runs a job now, outside its schedule, and waits for it. The schedule is unchanged; pausing doesn't prevent it.
    /// </summary>
    /// <param name="name">The job's name.</param>
    /// <param name="ct">Cancels the run (linked into the job's token).</param>
    /// <returns>
    /// Success, the run's error, or <c>scheduling.job_not_found</c> / <c>scheduling.already_running</c> /
    /// <c>scheduling.stopped</c>.
    /// </returns>
    public async ValueTask<Result> TriggerAsync(string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        Job? job;
        lock (_sync)
        {
            if (_stopped)
                return Error.Unavailable(CommonErrors.SchedulerStoppedCode, "The scheduler has been stopped.");
            if (!_jobs.TryGetValue(name, out job))
                return Error.NotFound(CommonErrors.JobNotFoundCode, $"No job named '{name}' is scheduled.");
            if (job.Running > 0 && !job.Options.AllowOverlap)
                return Error.Conflict(CommonErrors.JobAlreadyRunningCode, $"Job '{name}' is already running.");
            job.Running++;
            _activeRuns++;
        }
        return await RunAsync(job, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the scheduler: no new runs start, then running jobs get <paramref name="drainTimeout"/> to finish before
    /// their tokens are cancelled. Completes when every run has ended.
    /// </summary>
    /// <param name="drainTimeout">Grace period before cancelling running jobs; null = 30 s, zero = cancel at once.</param>
    /// <param name="ct">Stops waiting (running jobs are then cancelled but not awaited).</param>
    /// <returns>A task that completes when the scheduler has stopped.</returns>
    public async Task StopAsync(TimeSpan? drainTimeout = null, CancellationToken ct = default)
    {
        Job[] jobs;
        TaskCompletionSource? idle;
        lock (_sync)
        {
            _stopped = true;
            jobs = [.. _jobs.Values];
            idle = _activeRuns > 0 ? _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        }
        foreach (var job in jobs)
            job.Timer?.Dispose();
        if (idle is null)
            return;

        var drain = drainTimeout ?? DefaultDrainTimeout;
        if (drain > TimeSpan.Zero)
        {
            using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(drain, _time, delayCancel.Token);
            await Task.WhenAny(idle.Task, delay).ConfigureAwait(false);
            await delayCancel.CancelAsync().ConfigureAwait(false);
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await idle.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller stopped waiting; the runs have been cancelled and finish on their own.
        }
    }

    /// <summary>Stops the scheduler (see <see cref="StopAsync"/> with the default drain timeout).</summary>
    /// <returns>A task that completes when the scheduler has stopped.</returns>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnTimer(Job job)
    {
        var now = _time.GetUtcNow();
        bool start;
        lock (_sync)
        {
            if (job.Removed || _stopped)
                return;
            // A timer can fire early or twice when re-armed concurrently; only act when a run is actually due.
            if (job.NextRun is not { } due || now < due)
            {
                start = false;
            }
            else
            {
                job.NextSlot = FollowingSlot(job, now);
                job.NextRun = WithJitter(job, job.NextSlot, now);
                start = !job.Paused && (job.Options.AllowOverlap || job.Running == 0);
                if (start)
                {
                    job.Running++;
                    _activeRuns++;
                }
            }
        }
        Arm(job, now);
        if (start)
            _ = RunAsync(job, CancellationToken.None);
    }

    /// <summary>Executes one run; the caller has already counted it in <c>Running</c> and <c>_activeRuns</c>.</summary>
    private async Task<Result> RunAsync(Job job, CancellationToken ct)
    {
        var startedAt = _time.GetUtcNow();
        var startedTimestamp = _time.GetTimestamp();
        Error? error = null;
        var acquired = false;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;
        try
        {
            if (_concurrency is not null)
            {
                using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct);
                await _concurrency.WaitAsync(waitToken.Token).ConfigureAwait(false);
                acquired = true;
                startedAt = _time.GetUtcNow();
                startedTimestamp = _time.GetTimestamp();
            }

            if (job.Options.Timeout is { } limit)
                timeout = new CancellationTokenSource(limit, _time);
            linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, ct, timeout?.Token ?? CancellationToken.None);
            await job.Work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true && !_stopping.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = Error.Timeout(CommonErrors.JobTimeoutCode, $"Job '{job.Name}' exceeded its timeout of {job.Options.Timeout}.");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested || ct.IsCancellationRequested)
        {
            error = Error.Unavailable(CommonErrors.JobCancelledCode, $"Job '{job.Name}' was cancelled.");
        }
        catch (Exception ex)
        {
            error = Error.FromException(ex, CommonErrors.JobFailedCode);
        }
        finally
        {
            if (acquired)
                _concurrency!.Release();
            linked?.Dispose();
            timeout?.Dispose();
        }

        var duration = _time.GetElapsedTime(startedTimestamp);
        TaskCompletionSource? idle = null;
        lock (_sync)
        {
            job.Running--;
            job.RunCount++;
            job.LastRun = startedAt;
            job.LastDuration = duration;
            job.LastError = error;
            if (--_activeRuns == 0)
                idle = _idle;
        }

        try
        {
            JobCompleted?.Invoke(new JobRunCompleted(job.Name, startedAt, duration, error));
        }
        catch
        {
            // A faulty observer must not break the scheduler.
        }
        idle?.TrySetResult();
        return error is null ? Result.Success() : Result.Failure(error);
    }

    private void Arm(Job job, DateTimeOffset now)
    {
        DateTimeOffset? next;
        lock (_sync)
        {
            if (job.Removed || _stopped)
                return;
            next = job.NextRun;
        }
        var due = next is { } at ? (at > now ? at - now : TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
        try
        {
            job.Timer!.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Unscheduled or stopped concurrently.
        }
    }

    private void SetPaused(Job job, bool paused)
    {
        lock (_sync)
            job.Paused = paused;
    }

    private void Unschedule(Job job)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(job.Name, out var current) || !ReferenceEquals(current, job))
                return;
            _jobs.Remove(job.Name);
            job.Removed = true;
        }
        job.Timer?.Dispose();
    }

    private static void Validate(JobSchedule schedule, JobOptions options)
    {
        switch (schedule)
        {
            case JobSchedule.Interval interval:
                if (interval.Period <= TimeSpan.Zero)
                    throw new ArgumentException("The interval period must be positive.", nameof(schedule));
                if (interval.InitialDelay is { } initial && initial < TimeSpan.Zero)
                    throw new ArgumentException("The initial delay must not be negative.", nameof(schedule));
                break;
            case JobSchedule.Cron cron:
                var parsed = CronParser.GetNextOccurrence(cron.Expression, DateTimeOffset.UnixEpoch, cron.TimeZone);
                if (parsed.IsFailure)
                    throw new ArgumentException($"Invalid cron schedule '{cron.Expression}': {parsed.Error!.Message}", nameof(schedule));
                break;
            case JobSchedule.Once:
                break;
        }
        if (options.Jitter is < 0 or > 1 || double.IsNaN(options.Jitter))
            throw new ArgumentException("Jitter must be between 0 and 1.", nameof(options));
        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentException("Timeout must be positive.", nameof(options));
    }

    private static DateTimeOffset? FirstSlot(JobSchedule schedule, DateTimeOffset now) => schedule switch
    {
        JobSchedule.Interval interval => now + (interval.InitialDelay ?? interval.Period),
        JobSchedule.Cron cron => NextCron(cron, now),
        JobSchedule.Once once => once.At,
    };

    private static DateTimeOffset? FollowingSlot(Job job, DateTimeOffset now)
    {
        switch (job.Schedule)
        {
            case JobSchedule.Interval interval:
                var slot = (job.NextSlot ?? now) + interval.Period;
                if (slot <= now)
                    slot += interval.Period * (Math.Floor((now - slot) / interval.Period) + 1);
                return slot;
            case JobSchedule.Cron cron:
                return NextCron(cron, now);
            case JobSchedule.Once:
                return null;
            default:
                throw new InvalidOperationException("Unknown schedule.");
        }
    }

    private static DateTimeOffset? NextCron(JobSchedule.Cron cron, DateTimeOffset now)
    {
        var next = CronParser.GetNextOccurrence(cron.Expression, now, cron.TimeZone);
        return next.IsSuccess ? next.Value : null;
    }

    private static DateTimeOffset? WithJitter(Job job, DateTimeOffset? slot, DateTimeOffset now)
    {
        if (slot is not { } at || job.Options.Jitter <= 0)
            return slot;
        var span = job.Schedule switch
        {
            JobSchedule.Interval interval => interval.Period,
            JobSchedule.Cron => at > now ? at - now : TimeSpan.Zero,
            JobSchedule.Once => TimeSpan.Zero,
        };
        return at + span * (job.Options.Jitter * Random.Shared.NextDouble());
    }

    private sealed class Job(string name, JobSchedule schedule, Func<CancellationToken, ValueTask> work, JobOptions options)
    {
        public string Name { get; } = name;
        public JobSchedule Schedule { get; } = schedule;
        public Func<CancellationToken, ValueTask> Work { get; } = work;
        public JobOptions Options { get; } = options;
        public ITimer? Timer;
        public DateTimeOffset? NextSlot;
        public DateTimeOffset? NextRun;
        public DateTimeOffset? LastRun;
        public TimeSpan? LastDuration;
        public Error? LastError;
        public long RunCount;
        public int Running;
        public bool Paused;
        public bool Removed;

        public JobInfo ToInfo() =>
            new(Name, Schedule, LastRun, LastDuration, LastError, NextRun, RunCount, Running > 0, Paused);
    }

    private sealed class JobTimerState(JobScheduler scheduler, Job job)
    {
        public void Fire() => scheduler.OnTimer(job);
    }

    private sealed class JobHandle(JobScheduler scheduler, Job job) : IJobHandle
    {
        public string Name => job.Name;
        public void Pause() => scheduler.SetPaused(job, true);
        public void Resume() => scheduler.SetPaused(job, false);
        public void Dispose() => scheduler.Unschedule(job);
    }
}
