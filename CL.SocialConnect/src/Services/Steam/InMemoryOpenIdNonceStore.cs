namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// A bounded process-local replay store. Capacity exhaustion refuses new nonces; unexpired nonces are never
/// evicted to make room. Multi-node applications must supply a shared <see cref="IOpenIdNonceStore"/>.
/// </summary>
public sealed class InMemoryOpenIdNonceStore : IOpenIdNonceStore
{
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly TimeSpan _sweepInterval;
    private readonly TimeProvider _time;
    private DateTime _nextSweep;

    /// <summary>Creates a store with a hard capacity (default 10,000) and periodic expiration.</summary>
    /// <param name="sweepInterval">Expiration interval; zero sweeps on each use. Defaults to one minute.</param>
    /// <param name="maxEntries">Maximum retained nonces; must be positive.</param>
    /// <param name="timeProvider">Clock used for expiration; defaults to the system clock.</param>
    public InMemoryOpenIdNonceStore(TimeSpan? sweepInterval = null, int maxEntries = 10_000, TimeProvider? timeProvider = null)
    {
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        if (_sweepInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sweepInterval));
        MaxEntries = maxEntries;
        _time = timeProvider ?? TimeProvider.System;
        _nextSweep = _time.GetUtcNow().UtcDateTime + _sweepInterval;
    }

    /// <summary>The maximum number of retained nonces.</summary>
    public int MaxEntries { get; }

    /// <summary>Current entries, including entries awaiting expiration.</summary>
    public int Count { get { lock (_lock) return _seen.Count; } }

    /// <inheritdoc/>
    public ValueTask<bool> TryUseAsync(string nonce, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        cancellationToken.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (now >= _nextSweep || _seen.Count >= MaxEntries)
            {
                foreach (var entry in _seen.Where(e => e.Value <= now).ToArray()) _seen.Remove(entry.Key);
                _nextSweep = now + _sweepInterval;
            }
            if (_seen.TryGetValue(nonce, out var expires) && expires > now) return ValueTask.FromResult(false);
            if (!_seen.ContainsKey(nonce) && _seen.Count >= MaxEntries) return ValueTask.FromResult(false);
            _seen[nonce] = expiresAtUtc;
            return ValueTask.FromResult(true);
        }
    }
}
