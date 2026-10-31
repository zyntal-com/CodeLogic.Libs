namespace CL.Common.Errors;

/// <summary>
/// Stable error codes raised by CL.Common's resilience and scheduling building blocks. Codes follow
/// <c>area.snake_case</c> and never change once released; match on them rather than on messages.
/// The resilience codes are also exposed, with factory methods, on <see cref="Resilience.ResilienceErrors"/>.
/// </summary>
public static class CommonErrors
{
    // ── Resilience ────────────────────────────────────────────────────────────

    /// <summary>A circuit breaker is open (or isolated) and refused the call. Kind: unavailable.</summary>
    public const string CircuitOpenCode = "resilience.circuit_open";

    /// <summary>Every retry attempt failed with a transient error; the last error is the <c>InnerError</c>. Kind: unavailable.</summary>
    public const string RetriesExhaustedCode = "resilience.retries_exhausted";

    /// <summary>The caller cancelled the operation; no further attempt was made. Kind: unavailable.</summary>
    public const string ResilienceCancelledCode = "resilience.cancelled";

    /// <summary>The protected action threw instead of returning a failed result. Kind: internal.</summary>
    public const string UnhandledExceptionCode = "resilience.unhandled_exception";

    // ── Scheduling ────────────────────────────────────────────────────────────

    /// <summary>No job with the given name is scheduled. Kind: not found.</summary>
    public const string JobNotFoundCode = "scheduling.job_not_found";

    /// <summary>The job is already running and does not allow overlapping runs. Kind: conflict.</summary>
    public const string JobAlreadyRunningCode = "scheduling.already_running";

    /// <summary>The job threw an exception. Kind: internal.</summary>
    public const string JobFailedCode = "scheduling.job_failed";

    /// <summary>The job ran longer than its <c>JobOptions.Timeout</c>. Kind: timeout.</summary>
    public const string JobTimeoutCode = "scheduling.job_timeout";

    /// <summary>The run was cancelled (by the caller or because the scheduler stopped). Kind: unavailable.</summary>
    public const string JobCancelledCode = "scheduling.cancelled";

    /// <summary>The scheduler has been stopped and no longer runs jobs. Kind: unavailable.</summary>
    public const string SchedulerStoppedCode = "scheduling.stopped";
}
