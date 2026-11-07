using System.Collections.Concurrent;

namespace CL.TwoFactorAuth.Services;

/// <summary>
/// The default <see cref="ITotpUsedStepStore"/>: keeps the last accepted step per user in memory. Suitable for one
/// process; a replay against another server, or after a restart, is not detected. Entries are forgotten after
/// <see cref="Retention"/> (default two hours), which is longer than any drift window the configuration allows, so
/// forgetting an entry never lets a code within its window through twice.
/// </summary>
public sealed class InMemoryTotpUsedStepStore : ITotpUsedStepStore
{
    /// <summary>Minimum retention covering the largest supported TOTP drift window, including both endpoints.</summary>
    public static TimeSpan MinimumRetention => TimeSpan.FromSeconds(
        (2 * Models.TwoFactorAuthConfig.MaxWindowSize + 1) * Models.TwoFactorAuthConfig.MaxTimeStepSeconds);

    private readonly ConcurrentDictionary<string, (long step, DateTime recordedAtUtc)> _lastAccepted = new(StringComparer.Ordinal);
    private readonly Lock _sweepLock = new();
    private readonly TimeSpan _sweepInterval;
    private DateTime _nextSweep;

    /// <summary>Initializes a store that forgets a user's entry <paramref name="retention"/> after its last accepted code.</summary>
    /// <param name="retention">How long an entry is kept; <c>null</c> for two hours. Must be at least <see cref="MinimumRetention"/>.</param>
    /// <param name="sweepInterval">How often expired entries are removed; <c>null</c> for one minute.</param>
    public InMemoryTotpUsedStepStore(TimeSpan? retention = null, TimeSpan? sweepInterval = null)
    {
        Retention = retention ?? TimeSpan.FromHours(2);
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
        if (Retention < MinimumRetention)
            throw new ArgumentOutOfRangeException(nameof(retention), $"Retention must be at least {MinimumRetention}.");
        if (_sweepInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sweepInterval));
        _nextSweep = DateTime.UtcNow + _sweepInterval;
    }

    /// <summary>How long a user's last accepted step is remembered.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Number of users with a remembered step (including ones not swept yet).</summary>
    public int Count => _lastAccepted.Count;

    /// <inheritdoc/>
    public bool TryUse(string userId, long step)
    {
        ArgumentNullException.ThrowIfNull(userId);
        var now = DateTime.UtcNow;
        SweepIfDue(now);

        while (true)
        {
            if (_lastAccepted.TryAdd(userId, (step, now))) return true;
            if (!_lastAccepted.TryGetValue(userId, out var current)) continue;   // swept between the two calls
            if (current.step >= step) return false;
            if (_lastAccepted.TryUpdate(userId, (step, now), current)) return true;
        }
    }

    private void SweepIfDue(DateTime now)
    {
        if (now < _nextSweep) return;
        lock (_sweepLock)
        {
            if (now < _nextSweep) return;
            _nextSweep = now + _sweepInterval;
        }
        var cutoff = now - Retention;
        foreach (var entry in _lastAccepted)
            if (entry.Value.recordedAtUtc <= cutoff)
                _lastAccepted.TryRemove(entry);
    }
}
