using CodeLogic.Core.Results;

namespace CL.Common.Resilience;

/// <summary>How the delay grows between retries.</summary>
public enum BackoffKind
{
    /// <summary>Always <see cref="RetryOptions.BaseDelay"/>.</summary>
    Constant,

    /// <summary><see cref="RetryOptions.BaseDelay"/> × retry number.</summary>
    Linear,

    /// <summary><see cref="RetryOptions.BaseDelay"/> × 2^(retry number − 1).</summary>
    Exponential,

    /// <summary>
    /// A random delay between <see cref="RetryOptions.BaseDelay"/> and <see cref="RetryOptions.BaseDelay"/> × 3^retry
    /// (capped), which spreads retries of many clients apart. <see cref="RetryOptions.JitterFactor"/> is not applied
    /// on top.
    /// </summary>
    DecorrelatedJitter
}

/// <summary>Settings for <see cref="RetryPolicy"/>.</summary>
public sealed record RetryOptions
{
    /// <summary>Total attempts including the first (1 = no retries). Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>The base delay the backoff grows from. Default 100 ms.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>No delay is longer than this. Default 5 s.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How the delay grows. Default <see cref="BackoffKind.Exponential"/>.</summary>
    public BackoffKind Backoff { get; init; } = BackoffKind.Exponential;

    /// <summary>Random spread, 0..1: a delay d becomes a random value in d × [1 − f, 1 + f]. Default 0.2.</summary>
    public double JitterFactor { get; init; } = 0.2;
}

/// <summary>A retry that is about to happen.</summary>
/// <param name="Attempt">The attempt that just failed (1 = the first call).</param>
/// <param name="Delay">How long the policy waits before the next attempt.</param>
/// <param name="Error">The failure of that attempt.</param>
/// <param name="Exception">When the action threw rather than returned a failure, the exception.</param>
public readonly record struct RetryAttempt(int Attempt, TimeSpan Delay, Error? Error, Exception? Exception);

/// <summary>
/// Retries an operation that returns a <see cref="Result"/> while its failures are transient, with configurable
/// backoff and jitter.
/// <list type="bullet">
/// <item>A failure that <c>isTransient</c> rejects is returned as is, immediately.</item>
/// <item>When the last attempt fails transiently, the result is <see cref="ResilienceErrors.RetriesExhausted"/>
/// with the last error as <c>InnerError</c>.</item>
/// <item>Once the <see cref="CancellationToken"/> is cancelled no further attempt starts, and the result is
/// <see cref="ResilienceErrors.Cancelled"/>.</item>
/// <item>An action that throws is treated as having failed with <see cref="ResilienceErrors.UnhandledException"/>
/// (which <c>isTransient</c> sees like any other error).</item>
/// </list>
/// Delays use the <see cref="TimeProvider"/>, so tests can drive them with a fake clock. Thread-safe and reusable:
/// one instance can serve many concurrent calls.
/// </summary>
public sealed class RetryPolicy
{
    private readonly RetryOptions _options;
    private readonly TimeProvider _time;
    private readonly Random? _random;
    private readonly Lock _randomSync = new();

    /// <summary>Creates a policy that draws jitter from <see cref="Random.Shared"/>.</summary>
    /// <param name="options">The retry settings.</param>
    /// <param name="timeProvider">Source of delays; null uses <see cref="TimeProvider.System"/>.</param>
    public RetryPolicy(RetryOptions options, TimeProvider? timeProvider = null)
        : this(options, timeProvider, null)
    {
    }

    /// <summary>Creates a policy with an injected random source (for reproducible jitter in tests).</summary>
    /// <param name="options">The retry settings.</param>
    /// <param name="timeProvider">Source of delays; null uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="random">Jitter source; null uses <see cref="Random.Shared"/>. Access to it is serialized.</param>
    public RetryPolicy(RetryOptions options, TimeProvider? timeProvider, Random? random)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxAttempts must be at least 1.");
        if (options.BaseDelay < TimeSpan.Zero || options.MaxDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "BaseDelay and MaxDelay must not be negative.");
        if (options.JitterFactor is < 0 or > 1 || double.IsNaN(options.JitterFactor))
            throw new ArgumentOutOfRangeException(nameof(options), "JitterFactor must be between 0 and 1.");
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _random = random;
    }

    /// <summary>Raised before each retry's delay starts. Handlers run on the retrying thread; keep them fast.</summary>
    public event Action<RetryAttempt>? Retrying;

    /// <summary>The options this policy runs with.</summary>
    public RetryOptions Options => _options;

    /// <summary>
    /// The delay before the retry that follows failed attempt <paramref name="attempt"/>: backoff from
    /// <see cref="RetryOptions.BaseDelay"/>, jittered, capped at <see cref="RetryOptions.MaxDelay"/>.
    /// Depends only on the options and the random source.
    /// </summary>
    /// <param name="attempt">The failed attempt, 1-based.</param>
    /// <returns>The delay.</returns>
    public TimeSpan GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        var baseTicks = (double)_options.BaseDelay.Ticks;
        var maxTicks = (double)_options.MaxDelay.Ticks;

        double ticks;
        switch (_options.Backoff)
        {
            case BackoffKind.Constant:
                ticks = Jitter(baseTicks);
                break;
            case BackoffKind.Linear:
                ticks = Jitter(baseTicks * attempt);
                break;
            case BackoffKind.Exponential:
                ticks = Jitter(baseTicks * Math.Pow(2, Math.Min(attempt - 1, 62)));
                break;
            case BackoffKind.DecorrelatedJitter:
                var upper = Math.Min(maxTicks, baseTicks * Math.Pow(3, Math.Min(attempt, 40)));
                ticks = upper <= baseTicks ? upper : baseTicks + NextDouble() * (upper - baseTicks);
                break;
            default:
                throw new InvalidOperationException($"Unknown backoff {_options.Backoff}.");
        }
        return TimeSpan.FromTicks((long)Math.Clamp(ticks, 0, maxTicks));
    }

    /// <summary>Runs <paramref name="action"/>, retrying transient failures.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="action">The operation; receives the 1-based attempt number and the token.</param>
    /// <param name="isTransient">Decides whether a failure is worth retrying.</param>
    /// <param name="ct">Stops further attempts and cancels the delay between them.</param>
    /// <returns>The first success, the first non-transient failure, retries-exhausted, or cancelled.</returns>
    public async ValueTask<Result<T>> ExecuteAsync<T>(Func<int, CancellationToken, ValueTask<Result<T>>> action,
        Func<Error, bool> isTransient, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(isTransient);
        Error? lastError = null;
        for (var attempt = 1; ; attempt++)
        {
            if (ct.IsCancellationRequested)
                return ResilienceErrors.Cancelled(lastError);

            Exception? exception = null;
            Error error;
            try
            {
                var result = await action(attempt, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                    return result;
                error = result.Error ?? Error.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ResilienceErrors.Cancelled(lastError);
            }
            catch (Exception ex)
            {
                exception = ex;
                error = ResilienceErrors.UnhandledException(ex);
            }

            lastError = error;
            if (!isTransient(error))
                return error;
            if (attempt >= _options.MaxAttempts)
                return ResilienceErrors.RetriesExhausted(attempt, error);
            if (!await DelayAsync(attempt, error, exception, ct).ConfigureAwait(false))
                return ResilienceErrors.Cancelled(lastError);
        }
    }

    /// <summary>Runs <paramref name="action"/>, retrying transient failures.</summary>
    /// <param name="action">The operation; receives the 1-based attempt number and the token.</param>
    /// <param name="isTransient">Decides whether a failure is worth retrying.</param>
    /// <param name="ct">Stops further attempts and cancels the delay between them.</param>
    /// <returns>The first success, the first non-transient failure, retries-exhausted, or cancelled.</returns>
    public async ValueTask<Result> ExecuteAsync(Func<int, CancellationToken, ValueTask<Result>> action,
        Func<Error, bool> isTransient, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(isTransient);
        Error? lastError = null;
        for (var attempt = 1; ; attempt++)
        {
            if (ct.IsCancellationRequested)
                return ResilienceErrors.Cancelled(lastError);

            Exception? exception = null;
            Error error;
            try
            {
                var result = await action(attempt, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                    return result;
                error = result.Error ?? Error.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ResilienceErrors.Cancelled(lastError);
            }
            catch (Exception ex)
            {
                exception = ex;
                error = ResilienceErrors.UnhandledException(ex);
            }

            lastError = error;
            if (!isTransient(error))
                return error;
            if (attempt >= _options.MaxAttempts)
                return ResilienceErrors.RetriesExhausted(attempt, error);
            if (!await DelayAsync(attempt, error, exception, ct).ConfigureAwait(false))
                return ResilienceErrors.Cancelled(lastError);
        }
    }

    /// <summary>Announces and waits out the delay after <paramref name="attempt"/>; false when cancelled.</summary>
    private async ValueTask<bool> DelayAsync(int attempt, Error error, Exception? exception, CancellationToken ct)
    {
        var delay = GetDelay(attempt);
        Retrying?.Invoke(new RetryAttempt(attempt, delay, error, exception));
        if (ct.IsCancellationRequested)
            return false;
        if (delay <= TimeSpan.Zero)
            return true;
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private double Jitter(double ticks)
    {
        var factor = _options.JitterFactor;
        return factor <= 0 ? ticks : ticks * (1 + factor * (2 * NextDouble() - 1));
    }

    private double NextDouble()
    {
        if (_random is null)
            return Random.Shared.NextDouble();
        lock (_randomSync)
            return _random.NextDouble();
    }
}
