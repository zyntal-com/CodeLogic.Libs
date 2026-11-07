using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>
/// Stops calling a failing dependency for a while so it can recover, then probes it with a few trial calls.
/// <list type="bullet">
/// <item><b>Closed</b>: calls flow. <see cref="CircuitBreakerOptions.FailureThreshold"/> consecutive failures (or the
/// <see cref="CircuitBreakerOptions.FailureRatio"/> within the sampling window) open the circuit.</item>
/// <item><b>Open</b>: calls are refused with <see cref="ResilienceErrors.CircuitOpen"/> for
/// <see cref="CircuitBreakerOptions.BreakDuration"/>.</item>
/// <item><b>Half-open</b>: up to <see cref="CircuitBreakerOptions.HalfOpenMaxCalls"/> trial calls flow; the first
/// success closes the circuit, the first failure re-opens it.</item>
/// </list>
/// Use <see cref="ExecuteAsync{T}"/>, or <see cref="TryAcquire"/> plus <see cref="CircuitLease.Success"/> /
/// <see cref="CircuitLease.Failure"/> around code that doesn't fit a delegate. Thread-safe; acquiring and reporting
/// don't allocate. <see cref="StateChanged"/> handlers run synchronously on the thread that caused the transition,
/// outside the breaker's lock.
/// </summary>
public sealed class CircuitBreaker
{
    private const int Buckets = 10;

    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();
    private readonly long _bucketTicks;
    private readonly long[] _bucketIds = new long[Buckets];
    private readonly int[] _bucketSuccesses = new int[Buckets];
    private readonly int[] _bucketFailures = new int[Buckets];

    private CircuitState _state = CircuitState.Closed;
    private bool _isolated;
    private int _consecutiveFailures;
    private int _halfOpenInFlight;
    private long _openUntilTicks;
    private long _generation;

    /// <summary>Creates a breaker.</summary>
    /// <param name="name">A name for diagnostics, events and errors.</param>
    /// <param name="options">The breaker settings.</param>
    /// <param name="timeProvider">Clock source; null uses <see cref="TimeProvider.System"/>.</param>
    public CircuitBreaker(string name, CircuitBreakerOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        if (options.FailureThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "FailureThreshold must be at least 1.");
        if (options.FailureRatio is { } ratio && (ratio is <= 0 or > 1 || double.IsNaN(ratio)))
            throw new ArgumentOutOfRangeException(nameof(options), "FailureRatio must be above 0 and at most 1.");
        if (options.MinimumThroughput < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MinimumThroughput must be at least 1.");
        if (options.SamplingWindow < TimeSpan.FromTicks(Buckets))
            throw new ArgumentOutOfRangeException(nameof(options), "SamplingWindow is too short.");
        if (options.BreakDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "BreakDuration must not be negative.");
        if (options.HalfOpenMaxCalls < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "HalfOpenMaxCalls must be at least 1.");

        Name = name;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _bucketTicks = options.SamplingWindow.Ticks / Buckets;
        Array.Fill(_bucketIds, long.MinValue);
    }

    /// <summary>The breaker's name.</summary>
    public string Name { get; }

    /// <summary>The options this breaker runs with.</summary>
    public CircuitBreakerOptions Options => _options;

    /// <summary>
    /// The current state. An open circuit whose break duration has passed reports (and moves to)
    /// <see cref="CircuitState.HalfOpen"/>.
    /// </summary>
    public CircuitState State
    {
        get
        {
            CircuitStateChange? change;
            CircuitState state;
            lock (_sync)
            {
                change = PromoteIfDue(Now());
                state = _state;
            }
            Raise(change);
            return state;
        }
    }

    /// <summary>Raised on every state transition.</summary>
    public event Action<CircuitStateChange>? StateChanged;

    /// <summary>
    /// Asks to make a call. On <c>true</c>, make the call and report its outcome on <paramref name="lease"/> exactly
    /// once (<see cref="CircuitLease.Success"/>, <see cref="CircuitLease.Failure"/> or <see cref="CircuitLease.Ignore"/>).
    /// </summary>
    /// <param name="lease">The permission to report on; <c>default</c> when refused.</param>
    /// <returns><c>false</c> when the circuit is open, or half-open with every trial slot taken.</returns>
    public bool TryAcquire(out CircuitLease lease)
    {
        CircuitStateChange? change;
        bool acquired;
        lock (_sync)
        {
            change = PromoteIfDue(Now());
            switch (_state)
            {
                case CircuitState.Closed:
                    lease = new CircuitLease(this, _generation, halfOpen: false);
                    acquired = true;
                    break;
                case CircuitState.HalfOpen when _halfOpenInFlight < _options.HalfOpenMaxCalls:
                    _halfOpenInFlight++;
                    lease = new CircuitLease(this, _generation, halfOpen: true);
                    acquired = true;
                    break;
                default:
                    lease = default;
                    acquired = false;
                    break;
            }
        }
        Raise(change);
        return acquired;
    }

    /// <summary>
    /// Runs <paramref name="action"/> through the breaker. Refused calls return
    /// <see cref="ResilienceErrors.CircuitOpen"/> without running it.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="action">The protected call.</param>
    /// <param name="countsAsFailure">
    /// Decides whether a failed result means the dependency is unhealthy (e.g. timeouts) or not (e.g. not-found,
    /// validation), in which case it counts as a success for the breaker.
    /// </param>
    /// <param name="ct">Passed to the action. A cancelled call is neither a success nor a failure.</param>
    /// <returns>The action's result, <see cref="ResilienceErrors.CircuitOpen"/>, or <see cref="ResilienceErrors.Cancelled"/>.</returns>
    public async ValueTask<Result<T>> ExecuteAsync<T>(Func<CancellationToken, ValueTask<Result<T>>> action,
        Func<Error, bool> countsAsFailure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(countsAsFailure);
        if (ct.IsCancellationRequested)
            return ResilienceErrors.Cancelled();
        if (!TryAcquire(out var lease))
            return ResilienceErrors.CircuitOpen(Name, RetryAfter());

        Result<T> result;
        try
        {
            result = await action(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lease.Ignore();
            return ResilienceErrors.Cancelled();
        }
        catch (Exception ex)
        {
            lease.Failure();
            return ResilienceErrors.UnhandledException(ex);
        }

        if (result.IsFailure && countsAsFailure(result.Error ?? Error.Empty))
            lease.Failure();
        else
            lease.Success();
        return result;
    }

    /// <summary>Closes the circuit and clears every counter (also ends an <see cref="Isolate"/>).</summary>
    public void Reset()
    {
        CircuitStateChange? change;
        lock (_sync)
        {
            _isolated = false;
            ClearCounters();
            change = Transition(CircuitState.Closed, Now());
        }
        Raise(change);
    }

    /// <summary>Forces the circuit open until <see cref="Reset"/> is called (e.g. for maintenance).</summary>
    public void Isolate()
    {
        CircuitStateChange? change;
        lock (_sync)
        {
            _isolated = true;
            change = Transition(CircuitState.Open, Now());
        }
        Raise(change);
    }

    internal void Report(in CircuitLease lease, Outcome outcome)
    {
        CircuitStateChange? change = null;
        lock (_sync)
        {
            // An outcome from before the last transition (or Reset) says nothing about the current state.
            if (lease.Generation != _generation)
                return;

            var now = Now();
            if (lease.HalfOpen)
            {
                _halfOpenInFlight--;
                change = outcome switch
                {
                    Outcome.Success => CloseFromHalfOpen(now),
                    Outcome.Failure => Open(now),
                    _ => null,
                };
            }
            else if (_state == CircuitState.Closed)
            {
                switch (outcome)
                {
                    case Outcome.Success:
                        _consecutiveFailures = 0;
                        Record(now, failure: false);
                        break;
                    case Outcome.Failure:
                        _consecutiveFailures++;
                        Record(now, failure: true);
                        if (ShouldOpen(now))
                            change = Open(now);
                        break;
                }
            }
        }
        Raise(change);
    }

    private CircuitStateChange? CloseFromHalfOpen(long now)
    {
        ClearCounters();
        return Transition(CircuitState.Closed, now);
    }

    private CircuitStateChange? Open(long now)
    {
        _openUntilTicks = now + _options.BreakDuration.Ticks;
        return Transition(CircuitState.Open, now);
    }

    private bool ShouldOpen(long now)
    {
        if (_options.FailureRatio is not { } ratio)
            return _consecutiveFailures >= _options.FailureThreshold;

        var current = now / _bucketTicks;
        long successes = 0, failures = 0;
        for (var i = 0; i < Buckets; i++)
        {
            if (_bucketIds[i] > current - Buckets)
            {
                successes += _bucketSuccesses[i];
                failures += _bucketFailures[i];
            }
        }
        var total = successes + failures;
        return total >= _options.MinimumThroughput && failures >= ratio * total;
    }

    private void Record(long now, bool failure)
    {
        if (_options.FailureRatio is null)
            return;
        var id = now / _bucketTicks;
        var index = (int)(id % Buckets);
        if (index < 0) index += Buckets;
        if (_bucketIds[index] != id)
        {
            _bucketIds[index] = id;
            _bucketSuccesses[index] = 0;
            _bucketFailures[index] = 0;
        }
        if (failure)
            _bucketFailures[index]++;
        else
            _bucketSuccesses[index]++;
    }

    private void ClearCounters()
    {
        _consecutiveFailures = 0;
        Array.Fill(_bucketIds, long.MinValue);
        Array.Clear(_bucketSuccesses);
        Array.Clear(_bucketFailures);
    }

    /// <summary>Moves an open circuit whose break has passed to half-open; call with the lock held.</summary>
    private CircuitStateChange? PromoteIfDue(long now) =>
        _state == CircuitState.Open && !_isolated && now >= _openUntilTicks
            ? Transition(CircuitState.HalfOpen, now)
            : null;

    /// <summary>Changes state (bumping the generation so stale leases are ignored); call with the lock held.</summary>
    private CircuitStateChange? Transition(CircuitState to, long now)
    {
        var from = _state;
        _generation++;
        _halfOpenInFlight = 0;
        if (from == to)
            return null;
        _state = to;
        return new CircuitStateChange(Name, from, to, new DateTimeOffset(now, TimeSpan.Zero));
    }

    private TimeSpan? RetryAfter()
    {
        lock (_sync)
        {
            if (_isolated || _state != CircuitState.Open)
                return null;
            var remaining = _openUntilTicks - Now();
            return remaining > 0 ? TimeSpan.FromTicks(remaining) : TimeSpan.Zero;
        }
    }

    private void Raise(CircuitStateChange? change)
    {
        if (change is { } value)
            StateChanged?.Invoke(value);
    }

    private long Now() => _time.GetUtcNow().UtcTicks;

    internal enum Outcome
    {
        Success,
        Failure,
        Ignore
    }
}
