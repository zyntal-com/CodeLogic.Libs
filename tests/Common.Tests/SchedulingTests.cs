using CL.Common.Parser;
using CL.Common.Scheduling;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Common.Tests;

public class JobSchedulerTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Interval_jobs_run_at_a_fixed_rate()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        scheduler.Schedule("tick", new JobSchedule.Interval(TimeSpan.FromMinutes(1)), _ => { runs++; return ValueTask.CompletedTask; });

        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, runs);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runs);
        for (var i = 0; i < 4; i++)
            time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(5, runs);

        var info = Assert.Single(scheduler.Jobs);
        Assert.Equal(5, info.RunCount);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(1), info.NextRun);
        Assert.Null(info.LastError);
    }

    [Fact]
    public async Task Initial_delay_and_missed_slots_are_skipped_not_burst()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        scheduler.Schedule("tick", new JobSchedule.Interval(TimeSpan.FromMinutes(1), InitialDelay: TimeSpan.Zero), _ => { runs++; return ValueTask.CompletedTask; });
        Assert.Equal(1, runs);                      // zero initial delay runs at once
        time.Advance(TimeSpan.FromMinutes(10));     // one jump over ten slots
        Assert.Equal(2, runs);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(1), scheduler.Jobs[0].NextRun);
    }

    [Fact]
    public async Task A_throwing_job_reports_the_error_and_keeps_running()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var completed = new List<JobRunCompleted>();
        scheduler.JobCompleted += completed.Add;
        scheduler.JobCompleted += _ => throw new InvalidOperationException("bad observer");
        var runs = 0;
        scheduler.Schedule("flaky", new JobSchedule.Interval(TimeSpan.FromSeconds(10)), _ =>
        {
            runs++;
            return runs == 1 ? throw new InvalidOperationException("boom") : ValueTask.CompletedTask;
        });

        time.Advance(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, runs);
        Assert.Equal("scheduling.job_failed", completed[0].Error!.Code);
        Assert.Contains("boom", completed[0].Error!.Message);
        Assert.Null(completed[1].Error);
        Assert.Null(scheduler.Jobs[0].LastError);
    }

    [Fact]
    public async Task Overlapping_runs_are_skipped_by_default()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var gate = new TaskCompletionSource();
        var started = 0;
        scheduler.Schedule("slow", new JobSchedule.Interval(TimeSpan.FromSeconds(1)), async _ => { started++; await gate.Task; });

        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, started);
        Assert.True(scheduler.Jobs[0].IsRunning);

        var trigger = await scheduler.TriggerAsync("slow");
        Assert.Equal("scheduling.already_running", trigger.Error!.Code);

        gate.SetResult();
        await WaitUntil(() => !scheduler.Jobs[0].IsRunning);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task Overlap_can_be_allowed()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var gate = new TaskCompletionSource();
        var started = 0;
        scheduler.Schedule("slow", new JobSchedule.Interval(TimeSpan.FromSeconds(1)), async _ => { started++; await gate.Task; },
            new JobOptions { AllowOverlap = true });

        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, started);
        gate.SetResult();
        await WaitUntil(() => scheduler.Jobs[0].RunCount == 3);
    }

    [Fact]
    public async Task Max_concurrency_queues_runs()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time, maxConcurrency: 1);
        var gate = new TaskCompletionSource();
        var started = new List<string>();
        foreach (var name in new[] { "a", "b" })
            scheduler.Schedule(name, new JobSchedule.Once(time.GetUtcNow()), async _ => { lock (started) started.Add(name); await gate.Task; });

        Assert.Single(started);
        Assert.All(scheduler.Jobs, j => Assert.True(j.IsRunning)); // the second waits for the slot
        gate.SetResult();
        await WaitUntil(() => scheduler.Jobs.All(j => j.RunCount == 1));
        Assert.Equal(2, started.Count);
    }

    [Fact]
    public async Task Paused_jobs_skip_scheduled_runs_but_can_be_triggered()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        var handle = scheduler.Schedule("p", new JobSchedule.Interval(TimeSpan.FromMinutes(1)), _ => { runs++; return ValueTask.CompletedTask; });
        handle.Pause();
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, runs);
        Assert.True(scheduler.Jobs[0].IsPaused);

        Assert.True((await scheduler.TriggerAsync("p")).IsSuccess);
        Assert.Equal(1, runs);

        handle.Resume();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Disposing_the_handle_unschedules_the_job()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        var handle = scheduler.Schedule("x", new JobSchedule.Interval(TimeSpan.FromMinutes(1)), _ => { runs++; return ValueTask.CompletedTask; });
        handle.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, runs);
        Assert.Empty(scheduler.Jobs);
        Assert.Equal("scheduling.job_not_found", (await scheduler.TriggerAsync("x")).Error!.Code);

        // A new job with the same name is not affected by the old handle.
        scheduler.Schedule("x", new JobSchedule.Interval(TimeSpan.FromMinutes(1)), _ => ValueTask.CompletedTask);
        handle.Dispose();
        Assert.Single(scheduler.Jobs);
        Assert.True(scheduler.Unschedule("x"));
        Assert.False(scheduler.Unschedule("x"));
    }

    [Fact]
    public async Task Once_runs_a_single_time()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        scheduler.Schedule("once", new JobSchedule.Once(time.GetUtcNow().AddHours(1)), _ => { runs++; return ValueTask.CompletedTask; });
        time.Advance(TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, runs);
        Assert.Null(scheduler.Jobs[0].NextRun);
    }

    [Fact]
    public async Task Cron_jobs_run_on_their_minutes_in_their_time_zone()
    {
        var time = Clock(); // Monday 2026-01-05 12:00 UTC
        await using var scheduler = new JobScheduler(time);
        var runs = 0;
        var tz = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        scheduler.Schedule("daily", new JobSchedule.Cron("30 15 * * *", tz), _ => { runs++; return ValueTask.CompletedTask; });

        Assert.Equal(new DateTimeOffset(2026, 1, 5, 13, 30, 0, TimeSpan.Zero), scheduler.Jobs[0].NextRun); // 15:30 at +2
        time.Advance(TimeSpan.FromMinutes(89));
        Assert.Equal(0, runs);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, runs);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 13, 30, 0, TimeSpan.Zero), scheduler.Jobs[0].NextRun);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Timeout_cancels_the_job_token_and_reports_a_timeout()
    {
        var time = Clock();
        await using var scheduler = new JobScheduler(time);
        scheduler.Schedule("slow", new JobSchedule.Once(time.GetUtcNow().AddDays(1)), async ct => await Task.Delay(Timeout.Infinite, ct),
            new JobOptions { Timeout = TimeSpan.FromSeconds(5) });

        var run = scheduler.TriggerAsync("slow").AsTask();
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));
        var result = await run;
        Assert.Equal("scheduling.job_timeout", result.Error!.Code);
        Assert.IsType<CodeLogic.Core.Results.TimeoutError>(result.Error);
    }

    [Fact]
    public async Task Stop_drains_then_cancels_running_jobs()
    {
        var time = Clock();
        var scheduler = new JobScheduler(time);
        var cancelled = false;
        scheduler.Schedule("long", new JobSchedule.Once(time.GetUtcNow()), async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        Assert.True(scheduler.Jobs[0].IsRunning);

        var stop = scheduler.StopAsync(TimeSpan.FromSeconds(10));
        Assert.False(stop.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(cancelled);
        time.Advance(TimeSpan.FromSeconds(1));
        await stop;
        Assert.True(cancelled);
        Assert.Equal("scheduling.cancelled", scheduler.Jobs[0].LastError!.Code);

        Assert.Equal("scheduling.stopped", (await scheduler.TriggerAsync("long")).Error!.Code);
        Assert.Throws<ObjectDisposedException>(() => scheduler.Schedule("new", new JobSchedule.Once(time.GetUtcNow()), _ => ValueTask.CompletedTask));
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_schedules_are_programming_errors()
    {
        await using var scheduler = new JobScheduler(Clock());
        Assert.Throws<ArgumentException>(() => scheduler.Schedule("a", new JobSchedule.Interval(TimeSpan.Zero), _ => ValueTask.CompletedTask));
        Assert.Throws<ArgumentException>(() => scheduler.Schedule("b", new JobSchedule.Cron("not cron"), _ => ValueTask.CompletedTask));
        Assert.Throws<ArgumentException>(() => scheduler.Schedule("c", new JobSchedule.Once(DateTimeOffset.UnixEpoch), _ => ValueTask.CompletedTask, new JobOptions { Jitter = 2 }));
        scheduler.Schedule("d", new JobSchedule.Interval(TimeSpan.FromDays(1)), _ => ValueTask.CompletedTask);
        Assert.Throws<ArgumentException>(() => scheduler.Schedule("d", new JobSchedule.Interval(TimeSpan.FromDays(1)), _ => ValueTask.CompletedTask));
    }

    [Fact]
    public void Schedule_switches_are_exhaustive_over_the_closed_hierarchy()
    {
        static string Describe(JobSchedule s) => s switch
        {
            JobSchedule.Interval i => $"every {i.Period}",
            JobSchedule.Cron c => $"cron {c.Expression}",
            JobSchedule.Once o => $"once {o.At:O}",
        };
        Assert.Equal("cron * * * * *", Describe(new JobSchedule.Cron("* * * * *")));
    }

    /// <summary>Waits for continuations that run on the thread pool after a gate opens (no clock involved).</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}

public class CronTimeZoneTests
{
    [Fact]
    public void Next_occurrence_in_utc_matches_the_legacy_overload()
    {
        var from = new DateTime(2026, 3, 1, 10, 17, 0, DateTimeKind.Utc);
        var legacy = CronParser.GetNextOccurrence("*/15 * * * *", from).Value;
        var zoned = CronParser.GetNextOccurrence("*/15 * * * *", new DateTimeOffset(from), null).Value;
        Assert.Equal(new DateTimeOffset(legacy), zoned);
    }

    [Fact]
    public void Fields_are_evaluated_in_the_given_zone()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("minus5", TimeSpan.FromHours(-5), "minus5", "minus5");
        var next = CronParser.GetNextOccurrence("0 9 * * *", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), tz).Value;
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 14, 0, 0, TimeSpan.Zero), next);
    }
}
