using CL.Common.Scheduling;
using CL.Data.Execution;
using Microsoft.Extensions.Time.Testing;

namespace Data.Tests.Execution;

public sealed class DataSchedulerTests
{
    [Fact]
    public async Task Interval_jobs_run_on_the_fake_clock_and_failures_are_reported()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        await using var scheduler = new DataScheduler(time);
        var runs = 0;
        List<JobRunCompleted> completed = [];
        scheduler.JobCompleted += completed.Add;

        scheduler.Every("retention:main", TimeSpan.FromMinutes(5), _ =>
        {
            runs++;
            return runs == 2 ? throw new InvalidOperationException("boom") : ValueTask.CompletedTask;
        });

        Assert.Equal("retention:main", Assert.Single(scheduler.Jobs).Name);

        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            await WaitForAsync(() => completed.Count == i + 1);
        }

        Assert.Equal(3, runs);                                   // the failure did not stop the schedule
        Assert.Null(completed[0].Error);
        Assert.NotNull(completed[1].Error);
        Assert.All(completed, c => Assert.Equal("retention:main", c.Name));
    }

    [Fact]
    public async Task Scheduling_the_same_name_replaces_the_job_and_trigger_runs_now()
    {
        await using var scheduler = new DataScheduler(new FakeTimeProvider());
        var which = "";
        scheduler.Every("probe", TimeSpan.FromHours(1), _ => { which = "first"; return ValueTask.CompletedTask; });
        scheduler.Every("probe", TimeSpan.FromHours(1), _ => { which = "second"; return ValueTask.CompletedTask; });

        Assert.Single(scheduler.Jobs);
        Assert.True((await scheduler.TriggerAsync("probe")).IsSuccess);
        Assert.Equal("second", which);
        Assert.True(scheduler.Unschedule("probe"));
        Assert.Empty(scheduler.Jobs);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Jobs run on the thread pool after the fake timer fires; wait briefly for completion (no clock dependency).
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }
}
