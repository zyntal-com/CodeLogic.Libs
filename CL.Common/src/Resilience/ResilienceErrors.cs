using CL.Common.Errors;
using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>Errors returned by <see cref="RetryPolicy"/> and <see cref="CircuitBreaker"/>.</summary>
public static class ResilienceErrors
{
    /// <summary><c>resilience.circuit_open</c>: the breaker refused the call. Kind: unavailable.</summary>
    public const string CircuitOpenCode = CommonErrors.CircuitOpenCode;

    /// <summary><c>resilience.retries_exhausted</c>: every attempt failed; the last error is the <c>InnerError</c>. Kind: unavailable.</summary>
    public const string RetriesExhaustedCode = CommonErrors.RetriesExhaustedCode;

    /// <summary><c>resilience.cancelled</c>: the caller cancelled; no further attempt was made. Kind: unavailable.</summary>
    public const string CancelledCode = CommonErrors.ResilienceCancelledCode;

    /// <summary><c>resilience.unhandled_exception</c>: the action threw instead of returning a failure. Kind: internal.</summary>
    public const string UnhandledExceptionCode = CommonErrors.UnhandledExceptionCode;

    /// <summary>The breaker <paramref name="name"/> is open and refused the call.</summary>
    /// <param name="name">The breaker's name.</param>
    /// <param name="retryAfter">When known, how long until the breaker lets a trial call through.</param>
    /// <returns>An unavailable error.</returns>
    public static Error CircuitOpen(string name, TimeSpan? retryAfter = null) =>
        Error.Unavailable(CircuitOpenCode, $"Circuit '{name}' is open; the call was not attempted.",
            retryAfter is { } after ? $"retry_after={Math.Ceiling(after.TotalSeconds):0}s" : null);

    /// <summary>Every one of <paramref name="attempts"/> attempts failed; <paramref name="lastError"/> becomes the inner error.</summary>
    /// <param name="attempts">How many attempts were made.</param>
    /// <param name="lastError">The error of the last attempt.</param>
    /// <returns>An unavailable error wrapping <paramref name="lastError"/>.</returns>
    public static Error RetriesExhausted(int attempts, Error lastError) =>
        Error.Unavailable(RetriesExhaustedCode, $"The operation failed after {attempts} attempt(s): {lastError.Message}")
            .WithInner(lastError);

    /// <summary>The operation was cancelled by the caller.</summary>
    /// <param name="lastError">The error of the last attempt, if one failed before the cancellation.</param>
    /// <returns>An unavailable error.</returns>
    public static Error Cancelled(Error? lastError = null)
    {
        var error = Error.Unavailable(CancelledCode, "The operation was cancelled.");
        return lastError is null ? error : error.WithInner(lastError);
    }

    /// <summary>The protected action threw <paramref name="exception"/>.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>An internal error carrying the exception's message and type name.</returns>
    public static Error UnhandledException(Exception exception) =>
        Error.FromException(exception, UnhandledExceptionCode);
}
