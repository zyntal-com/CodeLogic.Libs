using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

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
