using System.Collections.Concurrent;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// Remembers the <c>openid.response_nonce</c> of every Steam sign-in assertion <see cref="SteamOpenIdService"/>
/// has verified, so the same assertion cannot be replayed. The default is <see cref="InMemoryOpenIdNonceStore"/>;
/// an application that runs on several servers must share one store between them (a database, a cache).
/// </summary>
public interface IOpenIdNonceStore
{
    /// <summary>
    /// Records <paramref name="nonce"/> unless it has been recorded before. The check and the record are one atomic
    /// step, so two concurrent verifications of the same assertion cannot both succeed.
    /// </summary>
    /// <param name="nonce">The assertion's <c>openid.response_nonce</c>.</param>
    /// <param name="expiresAtUtc">
    /// When the entry can be forgotten: after this moment <see cref="SteamOpenIdService.VerifyAsync(string, string, CancellationToken)"/>
    /// refuses the assertion by age anyway.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><c>true</c> when the nonce was new and is now recorded; <c>false</c> when it was seen before.</returns>
    ValueTask<bool> TryUseAsync(string nonce, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IOpenIdNonceStore"/>: keeps nonces in memory until they expire. Suitable for one process;
/// a replay against another server, or after a restart, is not detected.
/// </summary>
public sealed class InMemoryOpenIdNonceStore : IOpenIdNonceStore
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Lock _sweepLock = new();
    private readonly TimeSpan _sweepInterval;
    private DateTime _nextSweep;

    /// <summary>Initializes a store that drops expired nonces at most every <paramref name="sweepInterval"/> (default: one minute).</summary>
    /// <param name="sweepInterval">How often expired entries are removed; <c>null</c> for one minute.</param>
    public InMemoryOpenIdNonceStore(TimeSpan? sweepInterval = null)
    {
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
        _nextSweep = DateTime.UtcNow + _sweepInterval;
    }

    /// <summary>Number of nonces currently remembered (including ones not swept yet).</summary>
    public int Count => _seen.Count;

    /// <inheritdoc/>
    public ValueTask<bool> TryUseAsync(string nonce, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        SweepIfDue(now);

        // A nonce that expired is as good as unseen: VerifyAsync refuses the assertion by age before asking here.
        while (true)
        {
            if (_seen.TryAdd(nonce, expiresAtUtc)) return ValueTask.FromResult(true);
            if (!_seen.TryGetValue(nonce, out var expires) || expires > now) return ValueTask.FromResult(false);
            if (_seen.TryRemove(new KeyValuePair<string, DateTime>(nonce, expires)))
                continue;
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
        foreach (var entry in _seen)
            if (entry.Value <= now)
                _seen.TryRemove(entry);
    }
}
