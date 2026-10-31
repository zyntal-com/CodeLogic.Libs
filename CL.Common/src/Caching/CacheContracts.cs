namespace CL.Common.Caching;

/// <summary>How readily an entry is evicted when the cache is over its size or entry limit.</summary>
public enum CachePriority
{
    /// <summary>Evicted first.</summary>
    Low,

    /// <summary>The default.</summary>
    Normal,

    /// <summary>Evicted only after every <see cref="Low"/> and <see cref="Normal"/> entry.</summary>
    High,

    /// <summary>Never evicted for capacity; still removed when it expires or explicitly.</summary>
    NeverRemove
}

/// <summary>Per-entry cache settings. All members are optional.</summary>
public sealed record CacheEntryOptions
{
    /// <summary>Remove the entry this long after it was stored, however often it is read. Must be positive.</summary>
    public TimeSpan? AbsoluteExpiration { get; init; }

    /// <summary>Remove the entry when it has not been read for this long. Must be positive.</summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>Tags for group invalidation with <see cref="ICache{TKey}.RemoveByTag"/>.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// The entry's size in the unit of <see cref="MemoryCacheOptions.SizeLimit"/> (bytes, rows, …). Default 1.
    /// An entry larger than the whole limit is not stored.
    /// </summary>
    public long Size { get; init; } = 1;

    /// <summary>Eviction priority. Default <see cref="CachePriority.Normal"/>.</summary>
    public CachePriority Priority { get; init; } = CachePriority.Normal;
}

/// <summary>A point-in-time snapshot of a cache's counters.</summary>
/// <param name="Hits">Reads that found a live entry.</param>
/// <param name="Misses">Reads that found nothing (or an expired entry, or a value of another type).</param>
/// <param name="Evictions">Entries removed by capacity compaction or because they expired (not explicit removals).</param>
/// <param name="Count">Entries currently stored.</param>
/// <param name="Size">Sum of the stored entries' <see cref="CacheEntryOptions.Size"/>.</param>
public readonly record struct CacheStatistics(long Hits, long Misses, long Evictions, long Count, long Size);

/// <summary>Settings for <see cref="MemoryCache{TKey}"/>.</summary>
public sealed record MemoryCacheOptions
{
    /// <summary>Upper bound on the summed <see cref="CacheEntryOptions.Size"/> of all entries. Null = unbounded.</summary>
    public long? SizeLimit { get; init; }

    /// <summary>Upper bound on the number of entries. Null = unbounded.</summary>
    public int? MaxEntries { get; init; }

    /// <summary>
    /// When a limit is exceeded, evict until the cache is this fraction below the limit (0 ≤ value &lt; 1), so a
    /// full cache doesn't compact on every insert. Default 0.1.
    /// </summary>
    public double CompactionPercentage { get; init; } = 0.1;

    /// <summary>
    /// How often expired entries are swept in the background (they are also removed lazily when read).
    /// <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> disables the sweep. Default 1 minute.
    /// </summary>
    public TimeSpan ExpirationScanInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// An in-process cache keyed by <typeparamref name="TKey"/>, with expiration, tags, size limits and
/// stampede-protected population.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public interface ICache<TKey> where TKey : notnull
{
    /// <summary>Reads a live entry.</summary>
    /// <typeparam name="T">The expected value type; an entry holding another type counts as a miss.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="value">The value when found.</param>
    /// <returns><c>true</c> when a live entry of type <typeparamref name="T"/> was found.</returns>
    bool TryGet<T>(TKey key, out T? value);

    /// <summary>
    /// Returns the cached value, or runs <paramref name="factory"/> and caches its result. Concurrent callers for the
    /// same key wait for a single factory run instead of all running it (stampede protection). A factory that throws
    /// caches nothing; the exception reaches the caller that ran it, and the next waiter tries again.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="factory">Creates the value on a miss.</param>
    /// <param name="options">Entry options for a newly created value.</param>
    /// <param name="ct">Cancels the wait and is passed to the factory.</param>
    /// <returns>The cached or newly created value.</returns>
    ValueTask<T> GetOrCreateAsync<T>(TKey key, Func<TKey, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken ct = default);

    /// <summary>Stores (or replaces) an entry.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">Entry options; null uses the defaults (no expiration, size 1, normal priority).</param>
    void Set<T>(TKey key, T value, CacheEntryOptions? options = null);

    /// <summary>Removes an entry.</summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> when an entry was removed.</returns>
    bool Remove(TKey key);

    /// <summary>Removes every entry stored with <paramref name="tag"/>.</summary>
    /// <param name="tag">The tag (ordinal, case-sensitive).</param>
    /// <returns>The number of entries removed.</returns>
    int RemoveByTag(string tag);

    /// <summary>Removes every entry. Statistics counters are kept.</summary>
    void Clear();

    /// <summary>A snapshot of the counters.</summary>
    /// <returns>The statistics.</returns>
    CacheStatistics GetStatistics();
}

/// <summary>A string-keyed <see cref="ICache{TKey}"/> with prefix removal and allocation-free span lookups.</summary>
public interface ICache : ICache<string>
{
    /// <summary>Removes every entry whose key starts with <paramref name="prefix"/> (ordinal).</summary>
    /// <param name="prefix">The key prefix.</param>
    /// <returns>The number of entries removed.</returns>
    int RemoveByPrefix(string prefix);

    /// <summary>Reads a live entry by a span key, without allocating a string.</summary>
    /// <typeparam name="T">The expected value type.</typeparam>
    /// <param name="key">The key characters.</param>
    /// <param name="value">The value when found.</param>
    /// <returns><c>true</c> when a live entry of type <typeparamref name="T"/> was found.</returns>
    bool TryGet<T>(ReadOnlySpan<char> key, out T? value);
}
