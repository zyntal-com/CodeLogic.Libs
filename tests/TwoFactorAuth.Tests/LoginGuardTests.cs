using CL.TwoFactorAuth.Models;
using CL.TwoFactorAuth.Services;
using Xunit;

namespace TwoFactorAuth.Tests;

public sealed class LoginGuardTests
{
    [Fact]
    public async Task Concurrent_attempts_cannot_exceed_the_limit()
    {
        var store = new InMemoryTotpAttemptStore(maxAttempts: 5);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => store.TryBegin("user"))));
        Assert.Equal(5, results.Count(r => r));
    }

    [Fact]
    public void Expiration_and_success_free_attempt_state_without_evicting_live_lockouts()
    {
        var clock = new Clock();
        var store = new InMemoryTotpAttemptStore(maxAttempts: 1, maxEntries: 1, timeProvider: clock);
        Assert.True(store.TryBegin("a"));
        Assert.False(store.TryBegin("a"));
        Assert.False(store.TryBegin("b"));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.True(store.TryBegin("b"));
        store.Reset("b");
        Assert.True(store.TryBegin("a"));
    }

    [Fact]
    public void Guard_hides_lockout_and_replay_and_clears_only_successful_attempts()
    {
        var authenticator = new TwoFactorAuthenticator(new TwoFactorAuthConfig());
        var attempts = new InMemoryTotpAttemptStore(maxAttempts: 1);
        var guard = new TotpLoginGuard(authenticator, attempts);
        var secret = authenticator.GenerateSecretKey();
        var code = authenticator.GenerateTotpCode(secret).ValueOrThrow();
        Assert.True(guard.Validate(code, secret, "user").IsValid);
        var replay = guard.Validate(code, secret, "user");
        var locked = guard.Validate("wrong", secret, "user");
        Assert.False(replay.IsValid);
        Assert.Equal(replay.ErrorMessage, locked.ErrorMessage);
        Assert.False(attempts.TryBegin("user"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
