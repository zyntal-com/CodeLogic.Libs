using CL.Data.Caching;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>The result cache: hits skip the database, and no write through CL.Data ever leaves a stale result behind.</summary>
public abstract class CacheConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Reads : IDataInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public ValueTask<CodeLogic.Core.Results.Error?> OnExecutingAsync(CommandContext context)
        {
            if (!context.IsWrite) Interlocked.Increment(ref _count);
            return ValueTask.FromResult<CodeLogic.Core.Results.Error?>(null);
        }
    }

    private (Database Db, Reads Reads, Clock Clock) Cached(bool entityCache = false)
    {
        var clock = new Clock();
        var reads = new Reads();
        var cache = new DataCache(new DataCacheConfig { EntityCache = entityCache, DefaultTtlSeconds = 60 }, clock);
        return (Fixture.CreateDatabase(new DatabaseOptions { Cache = cache, Interceptors = [reads], Time = clock }), reads, clock);
    }

    private static async Task<List<Product>> SeedAsync(Database db)
    {
        var rows = Enumerable.Range(1, 3).Select(i => new Product { Sku = $"C-{i}", Name = $"n{i}", Price = i * 10m, Stock = i }).ToList();
        (await db.Repository<Product>().InsertManyAsync(rows)).ValueOrThrow();
        return rows;
    }

    private static Task<List<Product>> AllAsync(Database db) =>
        db.Query<Product>().WithCache().OrderBy(p => p.Sku).ToListAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

    [SkippableFact]
    public async Task Hits_skip_the_database_and_return_fresh_objects()
    {
        RequireProvider();
        var (db, reads, _) = Cached();
        await using var _ = db;
        await SeedAsync(db);

        var first = await AllAsync(db);
        var before = reads.Count;
        var second = await AllAsync(db);
        Assert.Equal(before, reads.Count);                                   // served from the cache
        Assert.Equal(first.Select(p => (p.Id, p.Name, p.Price)), second.Select(p => (p.Id, p.Name, p.Price)));
        Assert.NotSame(first[0], second[0]);                                 // fresh objects every time
        second[0].Name = "mutated";
        Assert.Equal("n1", (await AllAsync(db))[0].Name);                    // callers cannot change the cache

        Assert.Equal(3L, (await db.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
        var counted = reads.Count;
        Assert.Equal(3L, (await db.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
        Assert.Equal(counted, reads.Count);
    }

    [SkippableFact]
    public async Task Every_write_path_drops_stale_results()
    {
        RequireProvider();
        var (db, _, _) = Cached();
        await using var __ = db;
        var rows = await SeedAsync(db);
        var repo = db.Repository<Product>();

        async Task Expect(string step, params string[] names)
        {
            var actual = (await AllAsync(db)).Select(p => p.Name).ToArray();
            Assert.True(names.SequenceEqual(actual), $"{step}: expected [{string.Join(",", names)}], got [{string.Join(",", actual)}]");
        }

        await Expect("start", "n1", "n2", "n3");
        (await repo.InsertAsync(new Product { Sku = "C-4", Name = "n4", Price = 1, Stock = 1 })).ValueOrThrow();
        await Expect("insert", "n1", "n2", "n3", "n4");
        rows[0].Name = "u1";
        (await repo.UpdateAsync(rows[0])).ValueOrThrow();
        await Expect("update", "u1", "n2", "n3", "n4");
        (await repo.UpsertAsync(new Product { Id = rows[1].Id, Sku = "C-2", Name = "up2", Price = 1, Stock = 1 })).ValueOrThrow();
        await Expect("upsert", "u1", "up2", "n3", "n4");
        (await db.Query<Product>().Where(p => p.Sku == "C-3").UpdateAsync(p => new Product { Name = "set3" })).ValueOrThrow();
        await Expect("set-based update", "u1", "up2", "set3", "n4");
        (await repo.HardDeleteAsync(rows[0])).ValueOrThrow();
        await Expect("delete", "up2", "set3", "n4");
        (await db.ExecuteSqlAsync($"UPDATE conf_products SET {Sql.Identifier("Name")} = {"raw"} WHERE {Sql.Identifier("Sku")} = {"C-4"}")).ValueOrThrow();
        await Expect("raw SQL", "up2", "set3", "raw");

        var stock = (await db.Query<Product>().WithCache().Where(p => p.Sku == "C-2").FirstOrDefaultAsync()).ValueOrThrow()!.Stock;
        (await repo.IncrementAsync(rows[1].Id, p => p.Stock)).ValueOrThrow();
        Assert.Equal(stock + 1, (await db.Query<Product>().WithCache().Where(p => p.Sku == "C-2").FirstOrDefaultAsync()).ValueOrThrow()!.Stock);
    }

    [SkippableFact]
    public async Task Transactions_bypass_the_cache_and_invalidate_on_commit()
    {
        RequireProvider();
        var (db, _, _) = Cached();
        await using var __ = db;
        var rows = await SeedAsync(db);
        Assert.Equal(3, (await AllAsync(db)).Count);

        var tx = (await db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx)
        {
            (await tx.Repository<Product>().InsertAsync(new Product { Sku = "T-1", Name = "tx", Price = 1, Stock = 1 })).ValueOrThrow();
            Assert.Equal(4, (await tx.Db.Query<Product>().WithCache().ToListAsync()).ValueOrThrow().Count);   // reads in the transaction see its writes
            Assert.Equal(3, (await AllAsync(db)).Count);                                                       // others still see committed data
            tx.CommitAsync().GetAwaiter().GetResult().ThrowIfFailure();
        }
        Assert.Equal(4, (await AllAsync(db)).Count);

        var rolledBack = (await db.BeginTransactionAsync()).ValueOrThrow();
        await using (rolledBack)
        {
            rows[0].Name = "never";
            (await rolledBack.Repository<Product>().UpdateAsync(rows[0])).ValueOrThrow();
        }
        Assert.DoesNotContain(await AllAsync(db), p => p.Name == "never");
    }

    [SkippableFact]
    public async Task Joins_projections_groups_and_the_entity_cache_are_cached_and_invalidated()
    {
        RequireProvider();
        var (db, reads, _) = Cached(entityCache: true);
        await using var __ = db;
        var rows = await SeedAsync(db);
        (await db.Repository<ProductTag>().InsertAsync(new ProductTag { ProductId = rows[0].Id, Tag = "red", Weight = 1 })).ValueOrThrow();

        async Task<int> Joined() => (await db.Query<Product>().WithCache().Join<ProductTag>((p, t) => p.Id == t.ProductId)
            .Select((p, t) => new { p.Sku, t.Tag }).ToListAsync()).ValueOrThrow().Count;
        Assert.Equal(1, await Joined());
        var count = reads.Count;
        Assert.Equal(1, await Joined());
        Assert.Equal(count, reads.Count);
        (await db.Repository<ProductTag>().InsertAsync(new ProductTag { ProductId = rows[1].Id, Tag = "blue", Weight = 1 })).ValueOrThrow();
        Assert.Equal(2, await Joined());                                     // a write to the joined table drops it

        Assert.Equal(["C-1", "C-2", "C-3"], (await db.Query<Product>().WithCache().OrderBy(p => p.Sku).Select(p => p.Sku).ToListAsync()).ValueOrThrow());
        Assert.Equal(3, (await db.Query<Product>().WithCache().GroupBy(p => p.Stock).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ValueOrThrow().Count);

        var byId = (await db.Repository<Product>().GetByIdAsync(rows[2].Id)).ValueOrThrow()!;
        count = reads.Count;
        Assert.Equal(byId.Sku, (await db.Repository<Product>().GetByIdAsync(rows[2].Id)).ValueOrThrow()!.Sku);
        Assert.Equal(count, reads.Count);                                    // entity cache hit
        byId.Name = "renamed";
        (await db.Repository<Product>().UpdateAsync(byId)).ValueOrThrow();
        Assert.Equal("renamed", (await db.Repository<Product>().GetByIdAsync(rows[2].Id)).ValueOrThrow()!.Name);
    }

    [SkippableFact]
    public async Task Concurrent_misses_run_once_and_entries_expire()
    {
        RequireProvider();
        var (db, reads, clock) = Cached();
        await using var __ = db;
        await SeedAsync(db);
        var before = reads.Count;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => AllAsync(db)));
        Assert.Equal(before + 1, reads.Count);

        clock.Now = clock.Now.AddMinutes(2);                                 // past the 60 s default
        await AllAsync(db);
        Assert.Equal(before + 2, reads.Count);
    }

    [SkippableFact]
    public async Task Warm_queries_refresh_in_the_background_and_stop_when_idle()
    {
        RequireProvider();
        var (db, reads, clock) = Cached();
        await using var __ = db;
        await SeedAsync(db);
        var policy = new CachePolicy { Ttl = TimeSpan.FromMinutes(1), Refresh = TimeSpan.FromMinutes(1) };
        Task<long> Count() => db.Query<Product>().WithCache(policy).CountAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

        Assert.Equal(3L, await Count());
        // Another writer (no cache of its own, so nothing is invalidated here) adds a row.
        (await Db.Repository<Product>().InsertAsync(new Product { Sku = "W-1", Name = "w", Price = 1, Stock = 1 })).ValueOrThrow();
        Assert.Equal(3L, await Count());                                     // still the cached value

        clock.Now = clock.Now.AddSeconds(61);
        Assert.Equal(1, await db.Options.Cache!.RefreshDueAsync());          // refreshed in the background
        var before = reads.Count;
        Assert.Equal(4L, await Count());                                     // the refreshed value, without a read
        Assert.Equal(before, reads.Count);

        clock.Now = clock.Now.AddMinutes(10);                                // no hits for many refresh periods
        await db.Options.Cache.RefreshDueAsync();
        Assert.Equal(0, db.Options.Cache.WarmQueries);
    }

    [SkippableFact]
    public async Task Shielded_warm_results_survive_writes_and_reload_at_the_next_tick()
    {
        RequireProvider();
        var (db, reads, clock) = Cached();
        await using var __ = db;
        await SeedAsync(db);
        var policy = new CachePolicy { Ttl = TimeSpan.FromMinutes(5), Refresh = TimeSpan.FromMinutes(5), InvalidateOnWrite = false, Tags = ["board"] };
        Task<long> Count() => db.Query<Product>().WithCache(policy).CountAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

        Assert.Equal(3L, await Count());
        (await db.Repository<Product>().InsertAsync(new Product { Sku = "S-1", Name = "s", Price = 1, Stock = 1 })).ValueOrThrow();
        var before = reads.Count;
        Assert.Equal(3L, await Count());                                     // the write did not drop it: no cold read
        Assert.Equal(before, reads.Count);

        // The write made it due now, long before its 5-minute period.
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(1, await db.Options.Cache!.RefreshDueAsync());
        Assert.Equal(4L, await Count());
        Assert.Equal(0, await db.Options.Cache.RefreshDueAsync());           // nothing written since: not due again

        // Its own policy tag still drops it.
        db.Options.Cache.Invalidate(["board"]);
        before = reads.Count;
        Assert.Equal(4L, await Count());
        Assert.Equal(before + 1, reads.Count);
    }

    [SkippableFact]
    public async Task Shielded_results_without_a_refresh_period_live_until_their_ttl()
    {
        RequireProvider();
        var (db, _, clock) = Cached();
        await using var __ = db;
        await SeedAsync(db);
        var policy = new CachePolicy { Ttl = TimeSpan.FromMinutes(1), InvalidateOnWrite = false };
        Task<List<string>> Skus() => db.Query<Product>().OrderBy(p => p.Sku).Select(p => p.Sku).WithCache(policy).ToListAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

        Assert.Equal(3, (await Skus()).Count);
        (await db.Repository<Product>().InsertAsync(new Product { Sku = "S-2", Name = "s", Price = 1, Stock = 1 })).ValueOrThrow();
        Assert.Equal(3, (await Skus()).Count);                               // stale on purpose
        Assert.Equal(1, await db.Options.Cache!.RefreshDueAsync());          // dirty: reloaded at the next tick
        Assert.Equal(4, (await Skus()).Count);

        clock.Now = clock.Now.AddMinutes(2);                                 // expired: no longer tracked
        await db.Options.Cache.RefreshDueAsync();
        Assert.Equal(0, db.Options.Cache.WarmQueries);
    }

    [SkippableFact]
    public async Task RefreshAsync_reloads_warm_results_in_place_and_drops_the_rest()
    {
        RequireProvider();
        var (db, reads, _) = Cached();
        await using var __ = db;
        await SeedAsync(db);
        var warm = new CachePolicy { Refresh = TimeSpan.FromMinutes(5), InvalidateOnWrite = false, Tags = ["stats"] };
        var plain = new CachePolicy { Tags = ["stats"] };
        Task<long> WarmCount() => db.Query<Product>().WithCache(warm).CountAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());
        Task<long> PlainCount() => db.Query<Product>().Where(p => p.Stock > 0).WithCache(plain).CountAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

        Assert.Equal(3L, await WarmCount());
        Assert.Equal(3L, await PlainCount());
        // A change CL.Data does not see (another process): only an explicit refresh can pick it up.
        (await Db.Repository<Product>().InsertAsync(new Product { Sku = "R-1", Name = "r", Price = 1, Stock = 1 })).ValueOrThrow();

        Assert.Equal(1, await db.Options.Cache!.RefreshAsync(["stats"]));
        var before = reads.Count;
        Assert.Equal(4L, await WarmCount());                                 // reloaded in place: a hit
        Assert.Equal(before, reads.Count);
        Assert.Equal(4L, await PlainCount());                                // dropped: read again
        Assert.Equal(before + 1, reads.Count);
    }

    [SkippableFact]
    public async Task WithCache_after_select_and_group_select_caches_the_projection()
    {
        RequireProvider();
        var (db, reads, _) = Cached();
        await using var __ = db;
        await SeedAsync(db);

        async Task<int> Groups() => (await db.Query<Product>().GroupBy(p => p.Stock).Select(g => new { g.Key, N = g.Count() })
            .WithCache().ToListAsync()).ValueOrThrow().Count;
        async Task<int> Skus() => (await db.Query<Product>().Select(p => p.Sku).WithCache().ToListAsync()).ValueOrThrow().Count;

        Assert.Equal(3, await Groups());
        Assert.Equal(3, await Skus());
        var before = reads.Count;
        Assert.Equal(3, await Groups());
        Assert.Equal(3, await Skus());
        Assert.Equal(before, reads.Count);                                   // both served from the cache
        (await db.Repository<Product>().InsertAsync(new Product { Sku = "G-1", Name = "g", Price = 1, Stock = 99 })).ValueOrThrow();
        Assert.Equal(4, await Groups());                                     // and invalidated by writes
        Assert.Equal(4, await Skus());
    }

    [SkippableFact]
    public async Task Raw_sql_with_tables_drops_only_those_tables()
    {
        RequireProvider();
        var (db, reads, _) = Cached();
        await using var __ = db;
        var rows = await SeedAsync(db);
        (await db.Repository<ProductTag>().InsertAsync(new ProductTag { ProductId = rows[0].Id, Tag = "t", Weight = 1 })).ValueOrThrow();
        Task<long> Tags() => db.Query<ProductTag>().WithCache().CountAsync().AsTask().ContinueWith(t => t.Result.ValueOrThrow());

        Assert.Equal(1L, await Tags());
        Assert.Equal(3, (await AllAsync(db)).Count);
        (await db.ExecuteSqlAsync($"UPDATE conf_products SET {Sql.Identifier("Name")} = {"raw"}", ["conf_products"])).ValueOrThrow();

        var before = reads.Count;
        Assert.Equal(1L, await Tags());                                      // other tables stay cached
        Assert.Equal(before, reads.Count);
        Assert.All(await AllAsync(db), p => Assert.Equal("raw", p.Name));    // the written table was dropped
    }

    [SkippableFact]
    public async Task Invalidations_reach_other_nodes_through_the_bus()
    {
        RequireProvider();
        await using var busA = new DatabaseCacheBus(Db);
        await using var busB = new DatabaseCacheBus(Db);
        var cacheA = new DataCache();
        var cacheB = new DataCache();
        cacheA.UseBus(busA);
        cacheB.UseBus(busB);
        // Two nodes: the same connection name, each with its own cache.
        await using var nodeA = Database.Create("node", Fixture.Provider, Db.Config, new DatabaseOptions { Cache = cacheA }).ValueOrThrow();
        await using var nodeB = Database.Create("node", Fixture.Provider, Db.Config, new DatabaseOptions { Cache = cacheB }).ValueOrThrow();
        (await busB.PollAsync()).ValueOrThrow();                             // B starts listening

        await SeedAsync(nodeA);
        Assert.Equal(3L, (await nodeB.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
        (await nodeA.Repository<Product>().InsertAsync(new Product { Sku = "B-1", Name = "b", Price = 1, Stock = 1 })).ValueOrThrow();
        Assert.Equal(3L, (await nodeB.Query<Product>().WithCache().CountAsync()).ValueOrThrow());   // B has not heard yet

        // Publishing is fire-and-forget (a write never waits for other nodes); poll until the invalidation arrives.
        var heard = 0;
        for (var i = 0; i < 50 && heard == 0; i++)
        {
            heard = (await busB.PollAsync()).ValueOrThrow();
            if (heard == 0) await Task.Delay(50);
        }
        Assert.True(heard >= 1);
        Assert.Equal(4L, (await nodeB.Query<Product>().WithCache().CountAsync()).ValueOrThrow());

        // The in-process bus links caches inside one process the same way, without polling.
        var channel = "conformance-" + Guid.NewGuid().ToString("N");
        await using var localBusC = new InProcessCacheBus(channel);
        await using var localBusD = new InProcessCacheBus(channel);
        var cacheC = new DataCache();
        var cacheD = new DataCache();
        cacheC.UseBus(localBusC);
        cacheD.UseBus(localBusD);
        await using var nodeC = Database.Create("local", Fixture.Provider, Db.Config, new DatabaseOptions { Cache = cacheC }).ValueOrThrow();
        await using var nodeD = Database.Create("local", Fixture.Provider, Db.Config, new DatabaseOptions { Cache = cacheD }).ValueOrThrow();
        Assert.Equal(4L, (await nodeD.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
        (await nodeC.Repository<Product>().InsertAsync(new Product { Sku = "L-1", Name = "l", Price = 1, Stock = 1 })).ValueOrThrow();
        Assert.Equal(5L, (await nodeD.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Hit_and_miss_events_are_opt_in()
    {
        RequireProvider();
        System.Collections.Concurrent.ConcurrentQueue<CL.Data.Events.DataEvent> events = new();
        var cache = new DataCache(new DataCacheConfig { PublishEvents = true });
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Cache = cache, Events = events.Enqueue });
        await AllAsync(db);
        await AllAsync(db);
        Assert.Single(events.OfType<CL.Data.Events.CacheMissEvent>());
        Assert.Single(events.OfType<CL.Data.Events.CacheHitEvent>());
        Assert.Equal("conf_products", events.OfType<CL.Data.Events.CacheHitEvent>().Single().Table);
    }
}
