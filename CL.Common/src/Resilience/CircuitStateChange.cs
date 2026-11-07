using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>A state transition of a <see cref="CircuitBreaker"/>.</summary>
/// <param name="Name">The breaker's name.</param>
/// <param name="From">The previous state.</param>
/// <param name="To">The new state.</param>
/// <param name="At">When it happened (from the breaker's <see cref="TimeProvider"/>).</param>
public readonly record struct CircuitStateChange(string Name, CircuitState From, CircuitState To, DateTimeOffset At);
