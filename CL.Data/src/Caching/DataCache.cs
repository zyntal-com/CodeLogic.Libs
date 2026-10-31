using System.Collections.Concurrent;
using CL.Common.Caching;
using CL.Data.Configuration;
using CodeLogic.Core.Results;

namespace CL.Data.Caching;

/// <summary>How long a cached query result lives.</summary>
public sealed record CachePolicy
{
    /// <summary>Time to live from when the result was read (null: the cache's default).</summary>
    public TimeSpan? Ttl { get; init; }

    /// <summary>Extends the lifetime on every hit, up to <see cref="Ttl"/> from the last hit.</summary>
    public bool Sliding { get; init; }

    /// <summary>Extra tags; invalidating any of them drops the result (tables read are tagged automatically).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Keeps the result warm: it is read again in the background every <see cref="Refresh"/> (before it expires), so hits never
    /// wait for the database. Refreshing stops after <see cref="IdleRefreshes"/> periods without a hit. Needs the cache's
    /// refresh loop (<see cref="DataCache.RefreshDueAsync"/>, started by <see cref="DataCache.StartRefreshing"/>).
    /// </summary>
    public TimeSpan? Refresh { get; init; }

    /// <summary>Refresh periods without a hit after which a warm query is dropped (default 3).</summary>
    public int IdleRefreshes { get; init; } = 3;

    /// <summary>A policy with the cache's default lifetime.</summary>
    public static CachePolicy Default { get; } = new();

    /// <summary>A policy living <paramref name="ttl"/>.</summary>
    public static CachePolicy For(TimeSpan ttl) => new() { Ttl = ttl };
}

/// <summary>
/// The query result cache shared by the databases that use it (<c>DatabaseOptions.Cache</c>). Results are kept as raw
/// row values and materialized fresh on every hit. Every write through CL.Data drops the results of the tables it
/// touched (after commit, inside transactions); concurrent misses for the same query run it once. Reads inside a
/// transaction never use the cache.
/// </summary>
public sealed class DataCache : IDisposable
{
    private readonly MemoryCache<UInt128> _store;
    private readonly ConcurrentDictionary<UInt128, Lazy<Task<Result<object>>>> _loading = new();
    private readonly ConcurrentDictionary<UInt128, WarmEntry> _warm = new();
    private long _generation;
    private long _hits;
    private long _misses;
    private long _refreshes;
    private IDisposable? _refreshJob;

    /// <summary>A result kept warm: how to read it again, and when it was last used.</summary>
    private sealed class WarmEntry(CachePolicy policy, IReadOnlyList<string> tags, Func<CancellationToken, ValueTask<Result<(object Value, long Size)>>> load)
    {
        public CachePolicy Policy { get; } = policy;
        public IReadOnlyList<string> Tags { get; } = tags;
        public Func<CancellationToken, ValueTask<Result<(object Value, long Size)>>> Load { get; } = load;
        public long LastHit;
        public long NextRefresh;
        public int Running;
    }

    /// <summary>Creates a cache with <paramref name="config"/> limits (defaults when null).</summary>
    public DataCache(DataCacheConfig? config = null, TimeProvider? time = null)
    {
        Config = config ?? new DataCacheConfig();
        Time = time ?? TimeProvider.System;
        _store = new MemoryCache<UInt128>(new MemoryCacheOptions
        {
            SizeLimit = Config.MaxMemoryMb * 1024L * 1024L,
            MaxEntries = Config.MaxEntries,
        }, Time);
    }

    /// <summary>Limits and defaults.</summary>
    public DataCacheConfig Config { get; }

    /// <summary>Clock for lifetimes.</summary>
    public TimeProvider Time { get; }

    /// <summary>Results served from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Results read from the database.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Background refreshes of warm queries.</summary>
    public long Refreshes => Interlocked.Read(ref _refreshes);

    /// <summary>Queries kept warm.</summary>
    public int WarmQueries => _warm.Count;

    /// <summary>Entries, evictions and size.</summary>
    public CacheStatistics Statistics => _store.GetStatistics();

    /// <summary>Raised after tags were invalidated (the invalidation bus forwards them to other nodes).</summary>
    public event Action<IReadOnlyList<string>>? Invalidated;

    /// <summary>
    /// Links this cache to other nodes: invalidations made here are published on <paramref name="bus"/>, and invalidations
    /// published by other nodes drop the matching results here (without being published again).
    /// </summary>
    public void UseBus(ICacheInvalidationBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Subscribe(tags => Invalidate(tags, notify: false));
        Invalidated += tags => _ = PublishSafelyAsync(bus, tags);
    }

    private static async Task PublishSafelyAsync(ICacheInvalidationBus bus, IReadOnlyList<string> tags)
    {
        try { await bus.PublishAsync(tags).ConfigureAwait(false); }
        catch { /* other nodes fall back to their TTL; the local write already succeeded */ }
    }

    /// <summary>Drops every result carrying any of <paramref name="tags"/>.</summary>
    public void Invalidate(IReadOnlyList<string> tags) => Invalidate(tags, notify: true);

    internal void Invalidate(IReadOnlyList<string> tags, bool notify)
    {
        if (tags.Count == 0) return;
        Interlocked.Increment(ref _generation);   // loads that started before this write must not store their result
        foreach (var tag in tags) _store.RemoveByTag(tag);
        if (notify) Invalidated?.Invoke(tags);
    }

    /// <summary>Drops everything.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _store.Clear();
    }

    /// <summary>The cached value for <paramref name="key"/>, or the result of <paramref name="load"/> (stored when it succeeds).</summary>
    internal async ValueTask<Result<object>> GetOrLoadAsync(UInt128 key, CachePolicy policy, IReadOnlyList<string> tags,
        Func<CancellationToken, ValueTask<Result<(object Value, long Size)>>> load, CancellationToken ct)
    {
        if (policy.Refresh is not null)
        {
            var warm = _warm.GetOrAdd(key, _ => new WarmEntry(policy, tags, load) { NextRefresh = Now() + Ticks(policy.Refresh.Value) });
            Interlocked.Exchange(ref warm.LastHit, Now());
        }

        if (_store.TryGet<object>(key, out var cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            return Result<object>.Success(cached);
        }

        var loader = _loading.GetOrAdd(key, k => new Lazy<Task<Result<object>>>(() => LoadAsync(k, policy, tags, () => load(ct))));
        try
        {
            return await loader.Value.ConfigureAwait(false);
        }
        finally
        {
            _loading.TryRemove(new KeyValuePair<UInt128, Lazy<Task<Result<object>>>>(key, loader));
        }
    }

    private async Task<Result<object>> LoadAsync(UInt128 key, CachePolicy policy, IReadOnlyList<string> tags,
        Func<ValueTask<Result<(object Value, long Size)>>> load)
    {
        Interlocked.Increment(ref _misses);
        var generation = Interlocked.Read(ref _generation);
        var loaded = await load().ConfigureAwait(false);
        if (loaded.IsFailure) return loaded.Error!;

        var (value, size) = loaded.Value;
        if (Interlocked.Read(ref _generation) == generation) Store(key, policy, tags, value, size);
        return Result<object>.Success(value);
    }

    private void Store(UInt128 key, CachePolicy policy, IReadOnlyList<string> tags, object value, long size)
    {
        var ttl = policy.Ttl ?? TimeSpan.FromSeconds(Config.DefaultTtlSeconds);
        if (policy.Refresh is { } refresh && refresh * 2 > ttl) ttl = refresh * 2;   // a warm entry outlives its refresh period
        _store.Set(key, value, new CacheEntryOptions
        {
            AbsoluteExpiration = policy.Sliding ? null : ttl,
            SlidingExpiration = policy.Sliding ? ttl : null,
            Tags = [.. tags, .. policy.Tags],
            Size = Math.Max(1, size),
        });
    }

    // Warm-query timing follows GetUtcNow (like entry expiry), so test clocks that move only the wall clock work.
    private long Now() => Time.GetUtcNow().UtcTicks;

    private static long Ticks(TimeSpan span) => span.Ticks;

    /// <summary>
    /// Refreshes warm queries whose refresh time has come and drops the ones idle for too long. Called by the refresh loop
    /// (<see cref="StartRefreshing"/>); call it yourself from your own scheduler if you prefer. Returns how many were refreshed.
    /// </summary>
    public async ValueTask<int> RefreshDueAsync(CancellationToken ct = default)
    {
        var now = Now();
        var refreshed = 0;
        foreach (var (key, warm) in _warm)
        {
            var period = Ticks(warm.Policy.Refresh!.Value);
            if (now - Interlocked.Read(ref warm.LastHit) > period * Math.Max(1, warm.Policy.IdleRefreshes))
            {
                _warm.TryRemove(key, out _);
                continue;
            }
            if (now < Interlocked.Read(ref warm.NextRefresh) || Interlocked.Exchange(ref warm.Running, 1) == 1) continue;
            try
            {
                var generation = Interlocked.Read(ref _generation);
                var loaded = await warm.Load(ct).ConfigureAwait(false);
                if (loaded.IsSuccess && Interlocked.Read(ref _generation) == generation)
                {
                    Store(key, warm.Policy, warm.Tags, loaded.Value.Value, loaded.Value.Size);
                    Interlocked.Increment(ref _refreshes);
                    refreshed++;
                }
                Interlocked.Exchange(ref warm.NextRefresh, Now() + period);
            }
            finally
            {
                Interlocked.Exchange(ref warm.Running, 0);
            }
        }
        return refreshed;
    }

    /// <summary>
    /// Runs <see cref="RefreshDueAsync"/> on <paramref name="scheduler"/> every <paramref name="interval"/> (default one second).
    /// A failing refresh is reported through the scheduler and leaves the cached value in place.
    /// </summary>
    public void StartRefreshing(CL.Data.Execution.DataScheduler scheduler, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _refreshJob?.Dispose();
        var handle = scheduler.Every("cache-refresh-" + GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
            interval ?? TimeSpan.FromSeconds(1), async ct => await RefreshDueAsync(ct).ConfigureAwait(false));
        _refreshJob = handle as IDisposable;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _refreshJob?.Dispose();
        _store.Dispose();
    }
}
