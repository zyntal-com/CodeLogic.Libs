using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

public sealed partial class JobScheduler
{

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
}
