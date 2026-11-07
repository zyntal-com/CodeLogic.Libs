using CL.Common.Errors;
using CL.Common.Parser;
using CodeLogic.Core.Results;

namespace CL.Common.Scheduling;

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
