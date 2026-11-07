using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>The state of a <see cref="CircuitBreaker"/>.</summary>
public enum CircuitState
{
    /// <summary>Calls flow; failures are counted.</summary>
    Closed,

    /// <summary>Calls are refused until the break duration has passed (or until <see cref="CircuitBreaker.Reset"/> after <see cref="CircuitBreaker.Isolate"/>).</summary>
    Open,

    /// <summary>A limited number of trial calls flow; the first outcome closes or re-opens the circuit.</summary>
    HalfOpen
}
