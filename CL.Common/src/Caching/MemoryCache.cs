using CL.Common.Threading;

namespace CL.Common.Caching;

/// <summary>
/// A thread-safe in-process <see cref="ICache{TKey}"/>.
/// <list type="bullet">
/// <item>Expiration: absolute and/or sliding; expired entries are removed when read and by a periodic sweep.</item>
/// <item>Capacity: <see cref="MemoryCacheOptions.SizeLimit"/> and <see cref="MemoryCacheOptions.MaxEntries"/>; when
/// exceeded, entries are evicted lowest <see cref="CachePriority"/> first and least recently used within a
/// priority (approximate LRU), down to <see cref="MemoryCacheOptions.CompactionPercentage"/> below the limit.</item>
/// <item>Tags: kept in a reverse index, so <see cref="RemoveByTag"/> touches only the tagged entries.</item>
/// <item>Stampede protection: <see cref="GetOrCreateAsync{T}"/> runs one factory per key at a time.</item>
/// </list>
/// Keys are never boxed (value-type keys such as <see cref="UInt128"/> hashes are stored as is). Time comes from the
/// <see cref="TimeProvider"/>, so expiration is testable with a fake clock.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public class MemoryCache<TKey> : ICache<TKey>, IDisposable where TKey : notnull
{
    private readonly Dictionary<string, HashSet<TKey>> _tags = new(StringComparer.Ordinal);
    private readonly AsyncKeyedLock<TKey> _locks;
    private readonly MemoryCacheOptions _options;
    private readonly TimeProvider _time;
    private readonly ScanState? _scan;
    private long _hits, _misses, _evictions, _size, _clock;

    /// <summary>Creates a cache.</summary>
    /// <param name="options">Limits and sweep interval; null uses the defaults (unbounded, 1-minute sweep).</param>
    /// <param name="timeProvider">Clock and timer source; null uses <see cref="TimeProvider.System"/>.</param>
    public MemoryCache(MemoryCacheOptions? options = null, TimeProvider? timeProvider = null)
        : this(options, timeProvider, null)
    {
    }

    /// <summary>Creates a cache with a custom key comparer.</summary>
    /// <param name="options">Limits and sweep interval; null uses the defaults.</param>
    /// <param name="timeProvider">Clock and timer source; null uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="comparer">Key comparer; null uses the default equality comparer.</param>
    public MemoryCache(MemoryCacheOptions? options, TimeProvider? timeProvider, IEqualityComparer<TKey>? comparer)
    {
        _options = options ?? new MemoryCacheOptions();
        if (_options.SizeLimit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "SizeLimit must be positive.");
        if (_options.MaxEntries is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxEntries must be positive.");
        if (_options.CompactionPercentage is < 0 or >= 1 || double.IsNaN(_options.CompactionPercentage))
            throw new ArgumentOutOfRangeException(nameof(options), "CompactionPercentage must be at least 0 and below 1.");
        if (_options.ExpirationScanInterval < TimeSpan.Zero && _options.ExpirationScanInterval != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "ExpirationScanInterval must not be negative.");

        _time = timeProvider ?? TimeProvider.System;
        Entries = new Dictionary<TKey, Entry>(comparer);
        _locks = new AsyncKeyedLock<TKey>(comparer);

        var interval = _options.ExpirationScanInterval;
        if (interval > TimeSpan.Zero)
        {
            // The timer holds the cache only weakly, so a cache that is never disposed can still be collected.
            _scan = new ScanState(new WeakReference<MemoryCache<TKey>>(this));
            _scan.Timer = _time.CreateTimer(static state => ((ScanState)state!).Tick(), _scan, interval, interval);
        }
    }

    /// <summary>The entries, guarded by <see cref="Sync"/>.</summary>
    private protected Dictionary<TKey, Entry> Entries { get; }

    /// <summary>Guards every piece of mutable state.</summary>
    private protected Lock Sync { get; } = new();

    /// <inheritdoc/>
    public bool TryGet<T>(TKey key, out T? value)
    {
        lock (Sync)
        {
            if (Entries.TryGetValue(key, out var entry))
                return Hit(key, entry, countStatistics: true, out value);
            _misses++;
            value = default;
            return false;
        }
    }

    /// <inheritdoc/>
    public ValueTask<T> GetOrCreateAsync<T>(TKey key, Func<TKey, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet(key, out T? cached))
            return ValueTask.FromResult(cached!);
        return CreateAsync(key, factory, options, ct);
    }

    private async ValueTask<T> CreateAsync<T>(TKey key, Func<TKey, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options, CancellationToken ct)
    {
        using (await _locks.LockAsync(key, ct).ConfigureAwait(false))
        {
            // Another caller may have created it while we waited; that is not a second miss.
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var entry) && Hit(key, entry, countStatistics: false, out T? created))
                    return created!;
            }
            var value = await factory(key, ct).ConfigureAwait(false);
            Set(key, value, options);
            return value;
        }
    }

    /// <inheritdoc/>
    public void Set<T>(TKey key, T value, CacheEntryOptions? options = null)
    {
        var size = options?.Size ?? 1;
        ValidateEntryOptions(options);
        var now = _time.GetUtcNow().UtcTicks;
        var entry = new Entry
        {
            Value = value,
            AbsoluteExpiry = options?.AbsoluteExpiration is { } absolute ? now + absolute.Ticks : long.MaxValue,
            SlidingTicks = options?.SlidingExpiration?.Ticks ?? 0,
            LastAccessTicks = now,
            Size = size,
            Priority = options?.Priority ?? CachePriority.Normal,
            Tags = options?.Tags is { Count: > 0 } tags ? [.. tags.Distinct(StringComparer.Ordinal)] : null,
        };

        lock (Sync)
        {
            if (Entries.Remove(key, out var previous))
                Unlink(key, previous);
            if (_options.SizeLimit is { } limit && size > limit)
                return;

            entry.LastUsed = ++_clock;
            Entries.Add(key, entry);
            _size += size;
            if (entry.Tags is { } entryTags)
            {
                foreach (var tag in entryTags)
                {
                    if (!_tags.TryGetValue(tag, out var keys))
                        _tags.Add(tag, keys = new HashSet<TKey>(Entries.Comparer));
                    keys.Add(key);
                }
            }

            if (IsOverLimit())
                Compact(key);
        }
    }

    /// <inheritdoc/>
    public bool Remove(TKey key)
    {
        lock (Sync)
        {
            if (!Entries.Remove(key, out var entry))
                return false;
            Unlink(key, entry);
            return true;
        }
    }

    /// <inheritdoc/>
    public int RemoveByTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        lock (Sync)
        {
            if (!_tags.Remove(tag, out var keys))
                return 0;
            var removed = 0;
            foreach (var key in keys)
            {
                if (Entries.Remove(key, out var entry))
                {
                    Unlink(key, entry);
                    removed++;
                }
            }
            return removed;
        }
    }

    /// <inheritdoc/>
    public void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
            _tags.Clear();
            _size = 0;
        }
    }

    /// <inheritdoc/>
    public CacheStatistics GetStatistics()
    {
        lock (Sync)
            return new CacheStatistics(_hits, _misses, _evictions, Entries.Count, _size);
    }

    /// <summary>Removes every expired entry now (the periodic sweep calls this).</summary>
    /// <returns>The number of entries removed.</returns>
    public int RemoveExpired()
    {
        lock (Sync)
            return RemoveExpiredCore(_time.GetUtcNow().UtcTicks);
    }

    /// <summary>Stops the background sweep. The cache stays usable; expired entries are then removed only when read.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the sweep timer.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _scan?.Timer?.Dispose();
    }

    /// <summary>Removes the entries matching <paramref name="predicate"/>; call with <see cref="Sync"/> held.</summary>
    private protected int RemoveWhere(Func<TKey, bool> predicate)
    {
        List<TKey>? doomed = null;
        foreach (var key in Entries.Keys)
        {
            if (predicate(key))
                (doomed ??= []).Add(key);
        }
        if (doomed is null)
            return 0;
        foreach (var key in doomed)
        {
            if (Entries.Remove(key, out var entry))
                Unlink(key, entry);
        }
        return doomed.Count;
    }

    /// <summary>Completes a lookup that found <paramref name="entry"/>; call with <see cref="Sync"/> held.</summary>
    private protected bool Hit<T>(TKey key, Entry entry, bool countStatistics, out T? value)
    {
        var now = _time.GetUtcNow().UtcTicks;
        if (entry.IsExpired(now))
        {
            Entries.Remove(key);
            Unlink(key, entry);
            _evictions++;
        }
        else if (entry.Value is T typed)
        {
            entry.LastAccessTicks = now;
            entry.LastUsed = ++_clock;
            if (countStatistics) _hits++;
            value = typed;
            return true;
        }
        else if (entry.Value is null && default(T) is null)
        {
            entry.LastAccessTicks = now;
            entry.LastUsed = ++_clock;
            if (countStatistics) _hits++;
            value = default;
            return true;
        }

        if (countStatistics) _misses++;
        value = default;
        return false;
    }

    /// <summary>Records a miss for a lookup that found no entry; call with <see cref="Sync"/> held.</summary>
    private protected void Miss() => _misses++;

    private static void ValidateEntryOptions(CacheEntryOptions? options)
    {
        if (options is null)
            return;
        if (options.AbsoluteExpiration is { } absolute && absolute <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "AbsoluteExpiration must be positive.");
        if (options.SlidingExpiration is { } sliding && sliding <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "SlidingExpiration must be positive.");
        if (options.Size < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Size must not be negative.");
    }

    /// <summary>Drops an entry that was already removed from <see cref="Entries"/> from the size total and tag index.</summary>
    private void Unlink(TKey key, Entry entry)
    {
        _size -= entry.Size;
        if (entry.Tags is null)
            return;
        foreach (var tag in entry.Tags)
        {
            if (_tags.TryGetValue(tag, out var keys) && keys.Remove(key) && keys.Count == 0)
                _tags.Remove(tag);
        }
    }

    private bool IsOverLimit() =>
        (_options.SizeLimit is { } sizeLimit && _size > sizeLimit)
        || (_options.MaxEntries is { } maxEntries && Entries.Count > maxEntries);

    private int RemoveExpiredCore(long now)
    {
        List<KeyValuePair<TKey, Entry>>? expired = null;
        foreach (var pair in Entries)
        {
            if (pair.Value.IsExpired(now))
                (expired ??= []).Add(pair);
        }
        if (expired is null)
            return 0;
        foreach (var (key, entry) in expired)
        {
            Entries.Remove(key);
            Unlink(key, entry);
        }
        _evictions += expired.Count;
        return expired.Count;
    }

    /// <summary>Evicts down to the compaction target, sparing <paramref name="justAdded"/>; call with <see cref="Sync"/> held.</summary>
    private void Compact(TKey justAdded)
    {
        RemoveExpiredCore(_time.GetUtcNow().UtcTicks);
        if (!IsOverLimit())
            return;

        var keep = 1 - _options.CompactionPercentage;
        var targetSize = _options.SizeLimit is { } sizeLimit ? (long)(sizeLimit * keep) : long.MaxValue;
        var targetCount = _options.MaxEntries is { } maxEntries ? (int)(maxEntries * keep) : int.MaxValue;

        var candidates = new List<KeyValuePair<TKey, Entry>>(Entries.Count);
        foreach (var pair in Entries)
        {
            if (pair.Value.Priority != CachePriority.NeverRemove && !Entries.Comparer.Equals(pair.Key, justAdded))
                candidates.Add(pair);
        }
        candidates.Sort(static (a, b) =>
        {
            var byPriority = a.Value.Priority.CompareTo(b.Value.Priority);
            return byPriority != 0 ? byPriority : a.Value.LastUsed.CompareTo(b.Value.LastUsed);
        });

        foreach (var (key, entry) in candidates)
        {
            if (_size <= targetSize && Entries.Count <= targetCount)
                break;
            Entries.Remove(key);
            Unlink(key, entry);
            _evictions++;
        }
    }

    /// <summary>A stored value with its expiration and eviction bookkeeping.</summary>
    private protected sealed class Entry
    {
        public object? Value;
        public long AbsoluteExpiry;
        public long SlidingTicks;
        public long LastAccessTicks;
        public long LastUsed;
        public long Size;
        public CachePriority Priority;
        public string[]? Tags;

        public bool IsExpired(long now) =>
            now >= AbsoluteExpiry || (SlidingTicks > 0 && now - LastAccessTicks >= SlidingTicks);
    }

    private sealed class ScanState(WeakReference<MemoryCache<TKey>> cache)
    {
        public ITimer? Timer;

        public void Tick()
        {
            if (cache.TryGetTarget(out var target))
                target.RemoveExpired();
            else
                Timer?.Dispose();
        }
    }
}

/// <summary>
/// A string-keyed <see cref="MemoryCache{TKey}"/> (ordinal keys) that adds <see cref="RemoveByPrefix"/> and
/// allocation-free <see cref="TryGet{T}(ReadOnlySpan{char}, out T)"/> lookups.
/// </summary>
public sealed class MemoryCache : MemoryCache<string>, ICache
{
    private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    /// <summary>Creates a cache.</summary>
    /// <param name="options">Limits and sweep interval; null uses the defaults.</param>
    /// <param name="timeProvider">Clock and timer source; null uses <see cref="TimeProvider.System"/>.</param>
    public MemoryCache(MemoryCacheOptions? options = null, TimeProvider? timeProvider = null)
        : base(options, timeProvider, StringComparer.Ordinal) =>
        _spanLookup = Entries.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <inheritdoc/>
    public bool TryGet<T>(ReadOnlySpan<char> key, out T? value)
    {
        lock (Sync)
        {
            if (_spanLookup.TryGetValue(key, out var actualKey, out var entry))
                return Hit(actualKey, entry, countStatistics: true, out value);
            Miss();
            value = default;
            return false;
        }
    }

    /// <inheritdoc/>
    public int RemoveByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (Sync)
            return RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }
}
