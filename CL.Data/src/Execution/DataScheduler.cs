using CL.Common.Scheduling;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>
/// Runs CL.Data's background work (retention, warm-cache refresh, replica probes, cache-bus polling, health
/// checks) on one <see cref="JobScheduler"/>, so all of it uses the same clock, stops together and reports
/// failures the same way. A job never overlaps itself, and a failing job keeps its schedule.
/// </summary>
public sealed class DataScheduler : IAsyncDisposable
{
    private const string Prefix = "data:";
    private readonly JobScheduler _scheduler;

    /// <summary>Creates a scheduler on <paramref name="timeProvider"/> (default: the system clock).</summary>
    public DataScheduler(TimeProvider? timeProvider = null)
    {
        Time = timeProvider ?? TimeProvider.System;
        _scheduler = new JobScheduler(Time);
        _scheduler.JobCompleted += OnCompleted;
    }

    /// <summary>The clock jobs run on.</summary>
    public TimeProvider Time { get; }

    /// <summary>Raised after every run; <see cref="JobRunCompleted.Error"/> is set when the job failed. Names drop the internal prefix.</summary>
    public event Action<JobRunCompleted>? JobCompleted;

    /// <summary>Scheduled jobs.</summary>
    public IReadOnlyList<JobInfo> Jobs => _scheduler.Jobs
        .Where(j => j.Name.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(j => j with { Name = j.Name[Prefix.Length..] })
        .ToList();

    /// <summary>Runs <paramref name="work"/> every <paramref name="period"/>, first after <paramref name="initialDelay"/> (default: one period).</summary>
    public IJobHandle Every(string name, TimeSpan period, Func<CancellationToken, ValueTask> work, TimeSpan? initialDelay = null) =>
        Schedule(name, new JobSchedule.Interval(period, initialDelay), work);

    /// <summary>Schedules <paramref name="work"/> on any <see cref="JobSchedule"/>. Replaces a job with the same name.</summary>
    public IJobHandle Schedule(string name, JobSchedule schedule, Func<CancellationToken, ValueTask> work, JobOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _scheduler.Unschedule(Prefix + name);
        return _scheduler.Schedule(Prefix + name, schedule, work, (options ?? new JobOptions()) with { AllowOverlap = false });
    }

    /// <summary>Removes a job.</summary>
    public bool Unschedule(string name) => _scheduler.Unschedule(Prefix + name);

    /// <summary>Runs a job now (outside its schedule).</summary>
    public ValueTask<Result> TriggerAsync(string name, CancellationToken ct = default) => _scheduler.TriggerAsync(Prefix + name, ct);

    /// <summary>Stops all jobs, waiting up to <paramref name="drainTimeout"/> for running ones.</summary>
    public Task StopAsync(TimeSpan? drainTimeout = null, CancellationToken ct = default) => _scheduler.StopAsync(drainTimeout, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _scheduler.JobCompleted -= OnCompleted;
        await _scheduler.DisposeAsync().ConfigureAwait(false);
    }

    private void OnCompleted(JobRunCompleted run)
    {
        if (!run.Name.StartsWith(Prefix, StringComparison.Ordinal)) return;
        JobCompleted?.Invoke(run with { Name = run.Name[Prefix.Length..] });
    }
}
