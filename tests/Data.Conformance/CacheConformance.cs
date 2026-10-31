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
}
