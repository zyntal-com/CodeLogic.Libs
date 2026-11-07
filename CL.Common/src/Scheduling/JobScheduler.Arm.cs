using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

public sealed partial class JobScheduler
{

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
