using CL.Common.Resilience;
using CodeLogic.Core.Results;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Common.Tests;

public class RetryPolicyTests
{
    private static readonly Error Transient = Error.Unavailable("db.connection_lost", "lost");
    private static readonly Error Permanent = Error.Validation("db.syntax", "bad sql");
    private static bool IsTransient(Error e) => e.Code == Transient.Code || e.Code == ResilienceErrors.UnhandledExceptionCode;

    [Theory]
    [InlineData(BackoffKind.Constant, 1, 100)]
    [InlineData(BackoffKind.Constant, 4, 100)]
    [InlineData(BackoffKind.Linear, 1, 100)]
    [InlineData(BackoffKind.Linear, 3, 300)]
    [InlineData(BackoffKind.Exponential, 1, 100)]
    [InlineData(BackoffKind.Exponential, 4, 800)]
    [InlineData(BackoffKind.Exponential, 10, 5000)] // capped at MaxDelay
    public void Delays_follow_the_backoff_without_jitter(BackoffKind kind, int attempt, int expectedMs)
    {
        var policy = new RetryPolicy(new RetryOptions { Backoff = kind, JitterFactor = 0 });
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), policy.GetDelay(attempt));
    }

    [Fact]
    public void Jitter_stays_within_the_factor_and_is_reproducible_with_a_seeded_random()
    {
        var options = new RetryOptions { Backoff = BackoffKind.Constant, BaseDelay = TimeSpan.FromSeconds(1), JitterFactor = 0.2 };
        var a = new RetryPolicy(options, null, new Random(42));
        var b = new RetryPolicy(options, null, new Random(42));
        for (var i = 1; i <= 50; i++)
        {
            var delay = a.GetDelay(i);
            Assert.InRange(delay, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(1200));
            Assert.Equal(delay, b.GetDelay(i));
        }
    }

    [Fact]
    public void Decorrelated_jitter_stays_between_base_and_cap()
    {
        var policy = new RetryPolicy(new RetryOptions { Backoff = BackoffKind.DecorrelatedJitter, BaseDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromSeconds(2) }, null, new Random(1));
        for (var i = 1; i <= 20; i++)
            Assert.InRange(policy.GetDelay(i), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Transient_failures_are_retried_until_success_waiting_the_delays()
    {
        var time = new FakeTimeProvider();
        var policy = new RetryPolicy(new RetryOptions { MaxAttempts = 3, Backoff = BackoffKind.Constant, JitterFactor = 0, BaseDelay = TimeSpan.FromSeconds(1) }, time);
        var attempts = new List<int>();
        var retries = new List<RetryAttempt>();
        policy.Retrying += retries.Add;

        var task = policy.ExecuteAsync<int>((attempt, _) =>
        {
            attempts.Add(attempt);
            return ValueTask.FromResult<Result<int>>(attempt < 3 ? Transient : 99);
        }, IsTransient).AsTask();

        Assert.False(task.IsCompleted);           // waiting out the first delay
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal([1], attempts);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntil(() => attempts.Count == 2 && retries.Count == 2); // the continuation may hop to the pool
        time.Advance(TimeSpan.FromSeconds(1));

        var result = await task;
        Assert.Equal(99, result.Value);
        Assert.Equal([1, 2, 3], attempts);
        Assert.Equal([1, 2], retries.Select(r => r.Attempt));
        Assert.All(retries, r => Assert.Same(Transient, r.Error));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Permanent_failures_are_returned_immediately()
    {
        var policy = new RetryPolicy(new RetryOptions());
        var calls = 0;
        var result = await policy.ExecuteAsync((_, _) => { calls++; return ValueTask.FromResult<Result>(Permanent); }, IsTransient);
        Assert.Same(Permanent, result.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Exhausted_retries_wrap_the_last_error()
    {
        var policy = new RetryPolicy(new RetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.Zero });
        var result = await policy.ExecuteAsync((_, _) => ValueTask.FromResult<Result>(Transient), IsTransient);
        Assert.Equal(ResilienceErrors.RetriesExhaustedCode, result.Error!.Code);
        Assert.IsType<UnavailableError>(result.Error);
        Assert.Same(Transient, result.Error.InnerError);
    }

    [Fact]
    public async Task Exceptions_become_errors_and_are_offered_to_is_transient()
    {
        var policy = new RetryPolicy(new RetryOptions { MaxAttempts = 2, BaseDelay = TimeSpan.Zero });
        var retries = new List<RetryAttempt>();
        policy.Retrying += retries.Add;
        var result = await policy.ExecuteAsync<int>((attempt, _) =>
            attempt == 1 ? throw new IOException("socket") : ValueTask.FromResult<Result<int>>(5), IsTransient);
        Assert.Equal(5, result.Value);
        Assert.IsType<IOException>(Assert.Single(retries).Exception);
    }

    [Fact]
    public async Task Cancellation_during_the_delay_stops_retrying()
    {
        var time = new FakeTimeProvider();
        var policy = new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromSeconds(10), JitterFactor = 0 }, time);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var task = policy.ExecuteAsync((_, _) => { calls++; return ValueTask.FromResult<Result>(Transient); }, IsTransient, cts.Token).AsTask();
        await cts.CancelAsync();
        var result = await task;
        Assert.Equal(ResilienceErrors.CancelledCode, result.Error!.Code);
        Assert.IsType<UnavailableError>(result.Error);
        Assert.Same(Transient, result.Error.InnerError);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Already_cancelled_token_never_calls_the_action()
    {
        var policy = new RetryPolicy(new RetryOptions());
        var calls = 0;
        var result = await policy.ExecuteAsync<int>((_, _) => { calls++; return ValueTask.FromResult<Result<int>>(1); }, IsTransient, new CancellationToken(true));
        Assert.Equal(ResilienceErrors.CancelledCode, result.Error!.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Action_that_throws_cancellation_when_cancelled_is_cancelled_not_retried()
    {
        var policy = new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.Zero });
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var result = await policy.ExecuteAsync((_, ct) => { calls++; cts.Cancel(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Result.Success()); }, IsTransient, cts.Token);
        Assert.Equal(ResilienceErrors.CancelledCode, result.Error!.Code);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Invalid_options_are_programming_errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(new RetryOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(new RetryOptions { JitterFactor = 1.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(new RetryOptions()).GetDelay(0));
    }
}

public class CircuitBreakerTests
{
    private static readonly Error Down = Error.Timeout("db.timeout", "timeout");
    private static readonly Error Missing = Error.NotFound("db.not_found", "no row");
    private static bool CountsAsFailure(Error e) => e is TimeoutError or UnavailableError;

    private static ValueTask<Result<int>> Fail(CancellationToken _) => ValueTask.FromResult<Result<int>>(Down);
    private static ValueTask<Result<int>> Ok(CancellationToken _) => ValueTask.FromResult<Result<int>>(1);

    [Fact]
    public async Task Consecutive_failures_open_the_circuit_and_refuse_calls()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 3, BreakDuration = TimeSpan.FromSeconds(30) }, time);
        var changes = new List<CircuitStateChange>();
        breaker.StateChanged += changes.Add;

        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        await breaker.ExecuteAsync(Ok, CountsAsFailure); // a success resets the consecutive count
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        Assert.Equal(CircuitState.Closed, breaker.State);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        Assert.Equal(CircuitState.Open, breaker.State);

        var calls = 0;
        var refused = await breaker.ExecuteAsync(_ => { calls++; return Ok(default); }, CountsAsFailure);
        Assert.Equal(ResilienceErrors.CircuitOpenCode, refused.Error!.Code);
        Assert.IsType<UnavailableError>(refused.Error);
        Assert.Equal(0, calls);
        Assert.Equal(new CircuitStateChange("db", CircuitState.Closed, CircuitState.Open, time.GetUtcNow()), Assert.Single(changes));
    }

    [Fact]
    public async Task Failures_that_do_not_count_keep_the_circuit_closed()
    {
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 1 });
        var result = await breaker.ExecuteAsync(_ => ValueTask.FromResult<Result<int>>(Missing), CountsAsFailure);
        Assert.Same(Missing, result.Error);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task After_the_break_a_trial_success_closes_and_a_trial_failure_reopens()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(10) }, time);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        Assert.Equal(CircuitState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(10));
        await breaker.ExecuteAsync(Ok, CountsAsFailure);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task Half_open_admits_only_the_configured_number_of_concurrent_trials()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(5), HalfOpenMaxCalls = 2 }, time);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        time.Advance(TimeSpan.FromSeconds(5));

        var gate = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var calls = Enumerable.Range(0, 20).Select(_ => Task.Run(() => breaker.ExecuteAsync(_ =>
        {
            Interlocked.Increment(ref started);
            return new ValueTask<Result<int>>(gate.Task);
        }, CountsAsFailure).AsTask())).ToArray();

        // 18 calls are refused straight away; the two trials wait on the gate.
        while (calls.Count(c => c.IsCompleted) < 18)
            await Task.Yield();
        Assert.Equal(2, Volatile.Read(ref started));
        Assert.All(calls.Where(c => c.IsCompleted), c => Assert.Equal(ResilienceErrors.CircuitOpenCode, c.Result.Error!.Code));

        gate.SetResult(1);
        await Task.WhenAll(calls);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void Leases_from_before_a_transition_are_ignored()
    {
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 1 });
        Assert.True(breaker.TryAcquire(out var stale));
        breaker.Isolate();
        breaker.Reset();
        stale.Failure(); // belongs to the closed period before Isolate; must not reopen
        Assert.Equal(CircuitState.Closed, breaker.State);
        default(CircuitLease).Failure(); // no-op
    }

    [Fact]
    public void Isolate_holds_the_circuit_open_until_reset()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { BreakDuration = TimeSpan.FromSeconds(1) }, time);
        breaker.Isolate();
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryAcquire(out _));
        breaker.Reset();
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.TryAcquire(out var lease));
        lease.Success();
    }

    [Fact]
    public void Failure_ratio_opens_only_with_enough_throughput()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions
        {
            FailureRatio = 0.5, MinimumThroughput = 10, SamplingWindow = TimeSpan.FromSeconds(10),
        }, time);

        for (var i = 0; i < 9; i++)
            Report(breaker, failure: true);          // 9 calls: below the minimum throughput
        Assert.Equal(CircuitState.Closed, breaker.State);

        time.Advance(TimeSpan.FromSeconds(11));      // the window slides past them
        for (var i = 0; i < 6; i++)
            Report(breaker, failure: false);
        for (var i = 0; i < 4; i++)
            Report(breaker, failure: true);          // 4/10 failed
        Assert.Equal(CircuitState.Closed, breaker.State);
        Report(breaker, failure: true);              // 5/11 < 0.5
        Assert.Equal(CircuitState.Closed, breaker.State);
        Report(breaker, failure: true);              // 6/12 = 0.5
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task Cancelled_calls_free_the_trial_slot_without_a_verdict()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker("db", new CircuitBreakerOptions { FailureThreshold = 1, BreakDuration = TimeSpan.FromSeconds(1) }, time);
        await breaker.ExecuteAsync(Fail, CountsAsFailure);
        time.Advance(TimeSpan.FromSeconds(1));

        using var cts = new CancellationTokenSource();
        var result = await breaker.ExecuteAsync<int>(ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Ok(ct); }, CountsAsFailure, cts.Token);
        Assert.Equal(ResilienceErrors.CancelledCode, result.Error!.Code);
        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.True(breaker.TryAcquire(out var lease)); // the slot is free again
        lease.Success();
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    private static void Report(CircuitBreaker breaker, bool failure)
    {
        Assert.True(breaker.TryAcquire(out var lease));
        if (failure) lease.Failure(); else lease.Success();
    }
}
