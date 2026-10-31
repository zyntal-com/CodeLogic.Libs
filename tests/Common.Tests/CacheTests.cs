using CL.Common.Caching;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Common.Tests;

public class MemoryCacheTests
{
    private static readonly MemoryCacheOptions NoSweep = new() { ExpirationScanInterval = TimeSpan.Zero };

    [Fact]
    public void Set_and_get_round_trip()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("k", "v");
        Assert.True(cache.TryGet("k", out string? value));
        Assert.Equal("v", value);
        Assert.True(cache.TryGet("k".AsSpan(), out string? fromSpan));
        Assert.Equal("v", fromSpan);
    }

    [Fact]
    public void Wrong_type_is_a_miss()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("k", 42);
        Assert.False(cache.TryGet("k", out string? _));
        Assert.True(cache.TryGet("k", out int number));
        Assert.Equal(42, number);
    }

    [Fact]
    public void Null_values_are_cached()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set<string?>("k", null);
        Assert.True(cache.TryGet("k", out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void Absolute_expiration_removes_the_entry_lazily()
    {
        var time = new FakeTimeProvider();
        using var cache = new MemoryCache(NoSweep, time);
        cache.Set("k", 1, new CacheEntryOptions { AbsoluteExpiration = TimeSpan.FromMinutes(5) });
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(cache.TryGet("k", out int _));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGet("k", out int _));
        Assert.Equal(0, cache.GetStatistics().Count);
        Assert.Equal(1, cache.GetStatistics().Evictions);
    }

    [Fact]
    public void Sliding_expiration_is_renewed_by_reads()
    {
        var time = new FakeTimeProvider();
        using var cache = new MemoryCache(NoSweep, time);
        cache.Set("k", 1, new CacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(2) });
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1.5));
            Assert.True(cache.TryGet("k", out int _));
        }
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(cache.TryGet("k", out int _));
    }

    [Fact]
    public void Periodic_sweep_removes_expired_entries_without_reads()
    {
        var time = new FakeTimeProvider();
        using var cache = new MemoryCache(new MemoryCacheOptions { ExpirationScanInterval = TimeSpan.FromMinutes(1) }, time);
        cache.Set("a", 1, new CacheEntryOptions { AbsoluteExpiration = TimeSpan.FromSeconds(30) });
        cache.Set("b", 2);
        Assert.Equal(2, cache.GetStatistics().Count);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, cache.GetStatistics().Count);
    }

    [Fact]
    public void Max_entries_evicts_least_recently_used_first()
    {
        var time = new FakeTimeProvider();
        using var cache = new MemoryCache<int>(new MemoryCacheOptions { MaxEntries = 4, CompactionPercentage = 0.5, ExpirationScanInterval = TimeSpan.Zero }, time);
        for (var i = 1; i <= 4; i++)
            cache.Set(i, i);
        Assert.True(cache.TryGet(1, out int _)); // 1 is now the most recently used
        cache.Set(5, 5);                          // over the limit: compact to 2 entries, sparing 5

        Assert.Equal(2, cache.GetStatistics().Count);
        Assert.True(cache.TryGet(1, out int _));
        Assert.True(cache.TryGet(5, out int _));
        Assert.False(cache.TryGet(2, out int _));
        Assert.False(cache.TryGet(3, out int _));
    }

    [Fact]
    public void Eviction_respects_priority_and_never_removes_pinned_entries()
    {
        using var cache = new MemoryCache<string>(new MemoryCacheOptions { MaxEntries = 3, CompactionPercentage = 0, ExpirationScanInterval = TimeSpan.Zero });
        cache.Set("pinned", 0, new CacheEntryOptions { Priority = CachePriority.NeverRemove });
        cache.Set("high", 0, new CacheEntryOptions { Priority = CachePriority.High });
        cache.Set("low", 0, new CacheEntryOptions { Priority = CachePriority.Low });
        cache.Set("normal", 0);

        Assert.False(cache.TryGet("low", out int _));
        Assert.True(cache.TryGet("pinned", out int _));
        Assert.True(cache.TryGet("high", out int _));
        Assert.True(cache.TryGet("normal", out int _));
    }

    [Fact]
    public void Size_limit_counts_entry_sizes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100, CompactionPercentage = 0, ExpirationScanInterval = TimeSpan.Zero });
        cache.Set("a", 1, new CacheEntryOptions { Size = 60 });
        cache.Set("b", 2, new CacheEntryOptions { Size = 30 });
        Assert.Equal(90, cache.GetStatistics().Size);
        cache.Set("c", 3, new CacheEntryOptions { Size = 50 }); // 140 > 100: evict the oldest (a)
        Assert.False(cache.TryGet("a", out int _));
        Assert.Equal(80, cache.GetStatistics().Size);

        cache.Set("huge", 4, new CacheEntryOptions { Size = 101 }); // larger than the whole cache: not stored
        Assert.False(cache.TryGet("huge", out int _));
    }

    [Fact]
    public void Replacing_an_entry_updates_size_and_tags()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("k", 1, new CacheEntryOptions { Size = 10, Tags = ["old"] });
        cache.Set("k", 2, new CacheEntryOptions { Size = 3, Tags = ["new"] });
        Assert.Equal(3, cache.GetStatistics().Size);
        Assert.Equal(0, cache.RemoveByTag("old"));
        Assert.Equal(1, cache.RemoveByTag("new"));
    }

    [Fact]
    public void Remove_by_tag_removes_only_tagged_entries()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("user:1", 1, new CacheEntryOptions { Tags = ["users", "tenant:a"] });
        cache.Set("user:2", 2, new CacheEntryOptions { Tags = ["users"] });
        cache.Set("order:1", 3, new CacheEntryOptions { Tags = ["tenant:a"] });

        Assert.Equal(2, cache.RemoveByTag("users"));
        Assert.False(cache.TryGet("user:1", out int _));
        Assert.True(cache.TryGet("order:1", out int _));
        Assert.Equal(1, cache.RemoveByTag("tenant:a"));
        Assert.Equal(0, cache.RemoveByTag("missing"));
    }

    [Fact]
    public void Remove_by_prefix_and_clear()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("a:1", 1);
        cache.Set("a:2", 2);
        cache.Set("b:1", 3);
        Assert.Equal(2, cache.RemoveByPrefix("a:"));
        Assert.True(cache.Remove("b:1"));
        Assert.False(cache.Remove("b:1"));
        cache.Set("x", 1);
        cache.Clear();
        Assert.Equal(new CacheStatistics(0, 0, 0, 0, 0), cache.GetStatistics() with { Hits = 0, Misses = 0 });
    }

    [Fact]
    public void Statistics_count_hits_and_misses()
    {
        using var cache = new MemoryCache(NoSweep);
        cache.Set("k", 1);
        cache.TryGet("k", out int _);
        cache.TryGet("k".AsSpan(), out int _);
        cache.TryGet("nope", out int _);
        cache.TryGet("nope".AsSpan(), out int _);
        var stats = cache.GetStatistics();
        Assert.Equal((2, 2, 1L), (stats.Hits, stats.Misses, stats.Count));
    }

    [Fact]
    public void UInt128_keys_work()
    {
        using var cache = new MemoryCache<UInt128>(NoSweep);
        var key = UInt128.MaxValue - 5;
        cache.Set(key, "plan");
        Assert.True(cache.TryGet(key, out string? plan));
        Assert.Equal("plan", plan);
    }

    [Fact]
    public void Invalid_options_are_programming_errors()
    {
        using var cache = new MemoryCache(NoSweep);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Set("k", 1, new CacheEntryOptions { AbsoluteExpiration = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Set("k", 1, new CacheEntryOptions { Size = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryCache(new MemoryCacheOptions { CompactionPercentage = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryCache(new MemoryCacheOptions { MaxEntries = 0 }));
    }

    [Fact]
    public async Task Get_or_create_runs_the_factory_once_for_100_concurrent_callers()
    {
        using var cache = new MemoryCache(NoSweep);
        var runs = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 100).Select(_ => Task.Run(() => cache.GetOrCreateAsync("k", async (key, ct) =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
            return key.ToUpperInvariant();
        }).AsTask())).ToArray();

        // Let every caller reach the cache before the single factory run completes.
        while (cache.GetStatistics().Misses < 100)
            await Task.Yield();
        gate.SetResult();

        var results = await Task.WhenAll(callers);
        Assert.Equal(1, runs);
        Assert.All(results, r => Assert.Equal("K", r));
    }

    [Fact]
    public async Task Get_or_create_does_not_cache_a_failed_factory()
    {
        using var cache = new MemoryCache(NoSweep);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cache.GetOrCreateAsync<int>("k", (_, _) => throw new InvalidOperationException("boom")));
        Assert.Equal(7, await cache.GetOrCreateAsync("k", (_, _) => ValueTask.FromResult(7)));
        Assert.Equal(7, await cache.GetOrCreateAsync("k", (_, _) => ValueTask.FromResult(8)));
    }

    [Fact]
    public async Task Get_or_create_applies_entry_options()
    {
        var time = new FakeTimeProvider();
        using var cache = new MemoryCache<int>(NoSweep, time);
        var options = new CacheEntryOptions { AbsoluteExpiration = TimeSpan.FromSeconds(10) };
        Assert.Equal(1, await cache.GetOrCreateAsync(1, (_, _) => ValueTask.FromResult(1), options));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, await cache.GetOrCreateAsync(1, (_, _) => ValueTask.FromResult(2), options));
    }
}
