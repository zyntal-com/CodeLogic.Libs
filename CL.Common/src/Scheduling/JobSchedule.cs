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
