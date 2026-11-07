namespace CL.TwoFactorAuth.Services;

/// <summary>A bounded single-process attempt store; never evicts a live lockout to admit another user.</summary>
public sealed class InMemoryTotpAttemptStore : ITotpAttemptStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Attempts, DateTimeOffset Until)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates a limiter, defaulting to five attempts per five-minute window and 10,000 user entries.</summary>
    /// <param name="maxAttempts">Attempts admitted before lockout.</param>
    /// <param name="window">Fixed window during which attempts accumulate and the lockout remains active.</param>
    /// <param name="maxEntries">Maximum active users; capacity exhaustion refuses attempts.</param>
    /// <param name="timeProvider">Clock used for expiration.</param>
    public InMemoryTotpAttemptStore(int maxAttempts = 5, TimeSpan? window = null, int maxEntries = 10_000, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        Window = window ?? TimeSpan.FromMinutes(5);
        if (Window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        MaxAttempts = maxAttempts;
        MaxEntries = maxEntries;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Attempts permitted per user in one window.</summary>
    public int MaxAttempts { get; }
    /// <summary>Duration of the attempt window.</summary>
    public TimeSpan Window { get; }
    /// <summary>Maximum simultaneously tracked users.</summary>
    public int MaxEntries { get; }

    /// <inheritdoc/>
    public bool TryBegin(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_entries.TryGetValue(userId, out var entry) && entry.Until > now)
            {
                if (entry.Attempts >= MaxAttempts) return false;
                _entries[userId] = (entry.Attempts + 1, entry.Until);
                return true;
            }
            if (_entries.Count >= MaxEntries)
                foreach (var expired in _entries.Where(e => e.Value.Until <= now).ToArray()) _entries.Remove(expired.Key);
            if (!_entries.ContainsKey(userId) && _entries.Count >= MaxEntries) return false;
            _entries[userId] = (1, now + Window);
            return true;
        }
    }

    /// <inheritdoc/>
    public void Reset(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        lock (_lock) _entries.Remove(userId);
    }
}
