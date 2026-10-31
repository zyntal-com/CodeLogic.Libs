using CL.Data.Query;
using CL.Data.Writes;
using CodeLogic.Core.Results;
using Xunit;

namespace Data.Conformance;

/// <summary>Upserts, insert-ignore and atomic counters.</summary>
public abstract class UpsertConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static Product NewProduct(string sku, decimal price = 1m, int stock = 1) =>
        new() { Sku = sku, Name = sku, Price = price, Stock = stock };

    [SkippableFact]
    public async Task Upsert_inserts_then_updates_by_unique_key()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var options = new UpsertOptions { ConflictProperties = [nameof(Product.Sku)] };

        (await repo.UpsertAsync(NewProduct("UP", 5m), options)).ValueOrThrow();
        (await repo.UpsertAsync(NewProduct("UP", 7m), options)).ValueOrThrow();

        var rows = (await repo.FindAsync(p => p.Sku == "UP")).ValueOrThrow();
        var row = Assert.Single(rows);
        Assert.Equal(7m, row.Price);
        Assert.Equal(2, row.Version);                                            // the update bumped the token
    }

    [SkippableFact]
    public async Task Upsert_by_primary_key_and_many()
    {
        RequireProvider();
        var repo = Db.Repository<ProductTag>();
        (await repo.UpsertManyAsync([new ProductTag { ProductId = 1, Tag = "x", Weight = 1 }, new ProductTag { ProductId = 1, Tag = "y", Weight = 1 }])).ValueOrThrow();
        (await repo.UpsertManyAsync([new ProductTag { ProductId = 1, Tag = "x", Weight = 9 }, new ProductTag { ProductId = 2, Tag = "x", Weight = 3 }])).ValueOrThrow();

        var all = (await repo.GetAllAsync()).ValueOrThrow().OrderBy(t => t.ProductId).ThenBy(t => t.Tag).ToList();
        Assert.Equal([(1L, "x", 9), (1L, "y", 1), (2L, "x", 3)], all.Select(t => (t.ProductId, t.Tag, t.Weight)));
    }

    [SkippableFact]
    public async Task Upsert_with_increments_adds_to_the_stored_value()
    {
        RequireProvider();
        var repo = Db.Repository<ProductTag>();
        for (var i = 0; i < 3; i++)
            (await repo.UpsertWithIncrementsAsync(new ProductTag { ProductId = 5, Tag = "hits", Weight = 2 }, [t => t.Weight])).ValueOrThrow();
        Assert.Equal(6, (await repo.GetByKeyAsync(CL.Data.Mapping.EntityKey.Of(5L, "hits"))).ValueOrThrow()!.Weight);
    }

    [SkippableFact]
    public async Task Insert_ignore_reports_duplicates_without_changing_them()
    {
        RequireProvider();
        var repo = Db.Repository<ProductTag>();
        Assert.Equal(InsertOutcome.Inserted, (await repo.InsertIgnoreAsync(new ProductTag { ProductId = 1, Tag = "i", Weight = 1 })).ValueOrThrow());
        Assert.Equal(InsertOutcome.Duplicate, (await repo.InsertIgnoreAsync(new ProductTag { ProductId = 1, Tag = "i", Weight = 99 })).ValueOrThrow());
        Assert.Equal(1, (await repo.GetByKeyAsync(CL.Data.Mapping.EntityKey.Of(1L, "i"))).ValueOrThrow()!.Weight);

        var inserted = (await repo.InsertManyIgnoreAsync([
            new ProductTag { ProductId = 1, Tag = "i", Weight = 5 },
            new ProductTag { ProductId = 1, Tag = "j", Weight = 5 },
            new ProductTag { ProductId = 1, Tag = "k", Weight = 5 },
        ])).ValueOrThrow();
        Assert.Equal(2, inserted);
    }

    [SkippableFact]
    public async Task Counters_adjust_atomically()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = (await repo.InsertAsync(NewProduct("C", stock: 10))).ValueOrThrow();

        Assert.True((await repo.IncrementAsync(p.Id, x => x.Stock)).ValueOrThrow());
        Assert.True((await repo.IncrementAsync(p.Id, x => x.Stock, 5)).ValueOrThrow());
        Assert.True((await repo.DecrementAsync(p.Id, x => x.Stock, 3)).ValueOrThrow());
        Assert.True((await repo.AdjustAsync(p.Id, x => x.Price, 0.5m)).ValueOrThrow());

        var loaded = (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!;
        Assert.Equal(13, loaded.Stock);
        Assert.Equal(1.5m, loaded.Price);
        Assert.Equal(5, loaded.Version);                                         // every adjust bumped the token
        Assert.False((await repo.IncrementAsync(-1L, x => x.Stock)).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Concurrent_increments_are_not_lost()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = (await repo.InsertAsync(NewProduct("CC", stock: 0))).ValueOrThrow();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repo.IncrementAsync(p.Id, x => x.Stock).AsTask()));
        Assert.Equal(20, (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!.Stock);
    }
}
