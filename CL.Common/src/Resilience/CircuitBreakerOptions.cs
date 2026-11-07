using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>Settings for <see cref="CircuitBreaker"/>.</summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Consecutive failures that open the circuit (used when <see cref="FailureRatio"/> is null). Default 5.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    /// Alternative trigger: open when, within <see cref="SamplingWindow"/>, at least <see cref="MinimumThroughput"/>
    /// calls were made and this fraction (0 &lt; ratio ≤ 1) of them failed. When set, it replaces
    /// <see cref="FailureThreshold"/>.
    /// </summary>
    public double? FailureRatio { get; init; }

    /// <summary>Calls needed within the window before <see cref="FailureRatio"/> is evaluated. Default 10.</summary>
    public int MinimumThroughput { get; init; } = 10;

    /// <summary>The sliding window <see cref="FailureRatio"/> is measured over (10 buckets). Default 30 s.</summary>
    public TimeSpan SamplingWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before it lets trial calls through. Default 30 s.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Trial calls allowed at once while half-open. Default 1.</summary>
    public int HalfOpenMaxCalls { get; init; } = 1;
}
