using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>
/// Permission from <see cref="CircuitBreaker.TryAcquire"/> to make one call. Report the outcome exactly once.
/// Reporting on a <c>default</c> lease does nothing.
/// </summary>
public readonly struct CircuitLease
{
    private readonly CircuitBreaker? _breaker;

    internal CircuitLease(CircuitBreaker breaker, long generation, bool halfOpen)
    {
        _breaker = breaker;
        Generation = generation;
        HalfOpen = halfOpen;
    }

    internal long Generation { get; }

    internal bool HalfOpen { get; }

    /// <summary>The call succeeded (or failed in a way that says nothing about the dependency's health).</summary>
    public void Success() => _breaker?.Report(this, CircuitBreaker.Outcome.Success);

    /// <summary>The call failed in a way that counts against the dependency.</summary>
    public void Failure() => _breaker?.Report(this, CircuitBreaker.Outcome.Failure);

    /// <summary>The call ended without a verdict (e.g. cancelled by the caller); frees a half-open trial slot.</summary>
    public void Ignore() => _breaker?.Report(this, CircuitBreaker.Outcome.Ignore);
}
