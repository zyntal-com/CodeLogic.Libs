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
    private long _generation;
    private long _hits;
    private long _misses;

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

    /// <summary>Entries, evictions and size.</summary>
    public CacheStatistics Statistics => _store.GetStatistics();

    /// <summary>Raised after tags were invalidated (the invalidation bus forwards them to other nodes).</summary>
    public event Action<IReadOnlyList<string>>? Invalidated;

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
        Func<ValueTask<Result<(object Value, long Size)>>> load)
    {
        if (_store.TryGet<object>(key, out var cached) && cached is not null)
        {
            Interlocked.Increment(ref _hits);
            return Result<object>.Success(cached);
        }

        var loader = _loading.GetOrAdd(key, k => new Lazy<Task<Result<object>>>(() => LoadAsync(k, policy, tags, load)));
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
        if (Interlocked.Read(ref _generation) == generation)
        {
            var ttl = policy.Ttl ?? TimeSpan.FromSeconds(Config.DefaultTtlSeconds);
            _store.Set(key, value, new CacheEntryOptions
            {
                AbsoluteExpiration = policy.Sliding ? null : ttl,
                SlidingExpiration = policy.Sliding ? ttl : null,
                Tags = [.. tags, .. policy.Tags],
                Size = Math.Max(1, size),
            });
        }
        return Result<object>.Success(value);
    }

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();
}
