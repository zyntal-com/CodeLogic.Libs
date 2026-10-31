using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Typed joins.</summary>
public abstract class JoinConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task<(Product A, Product B, Product C)> SeedAsync()
    {
        var a = new Product { Sku = "A", Name = "alpha", Price = 1m };
        var b = new Product { Sku = "B", Name = "bravo", Price = 2m };
        var c = new Product { Sku = "C", Name = "charlie", Price = 3m };
        (await Db.Repository<Product>().InsertManyAsync([a, b, c])).ValueOrThrow();
        (await Db.Repository<ProductTag>().InsertManyAsync([
            new ProductTag { ProductId = a.Id, Tag = "red", Weight = 1 },
            new ProductTag { ProductId = a.Id, Tag = "big", Weight = 2 },
            new ProductTag { ProductId = b.Id, Tag = "red", Weight = 3 },
        ])).ValueOrThrow();
        return (a, b, c);
    }

    [SkippableFact]
    public async Task Inner_join_with_projection_and_conditions_on_both_sides()
    {
        RequireProvider();
        await SeedAsync();
        var rows = (await Db.Query<Product>().Where(p => p.Price < 3m)
            .Join<ProductTag>((p, t) => t.ProductId == p.Id)
            .Where((p, t) => t.Tag == "red")
            .OrderBy((p, t) => p.Sku)
            .Select((p, t) => new { p.Sku, t.Tag, Score = p.Price * t.Weight })
            .ToListAsync()).ValueOrThrow();

        Assert.Equal([("A", "red", 1m), ("B", "red", 6m)], rows.Select(r => (r.Sku, r.Tag, r.Score)));
    }

    [SkippableFact]
    public async Task Left_join_returns_null_for_missing_rows()
    {
        RequireProvider();
        await SeedAsync();
        var rows = (await Db.Query<Product>()
            .LeftJoin<ProductTag>((p, t) => t.ProductId == p.Id && t.Tag == "big")
            .OrderBy((p, t) => p.Sku)
            .ToListAsync()).ValueOrThrow();

        Assert.Equal(3, rows.Count);
        Assert.Equal("big", rows[0].Item2!.Tag);
        Assert.Null(rows[1].Item2);
        Assert.Null(rows[2].Item2);
        Assert.Equal(3L, (await Db.Query<Product>().LeftJoin<ProductTag>((p, t) => t.ProductId == p.Id && t.Tag == "big").CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Three_tables_with_a_self_join()
    {
        RequireProvider();
        await SeedAsync();
        var pairs = (await Db.Query<Product>()
            .Join<ProductTag>((p, t1) => t1.ProductId == p.Id)
            .Join<ProductTag>((p, t1, t2) => t2.ProductId == p.Id && t2.Tag != t1.Tag)
            .OrderBy((p, t1, t2) => t1.Tag)
            .Select((p, t1, t2) => new { p.Sku, First = t1.Tag, Second = t2.Tag })
            .ToListAsync()).ValueOrThrow();

        Assert.Equal([("A", "big", "red"), ("A", "red", "big")], pairs.Select(x => (x.Sku, x.First, x.Second)));
    }

    [SkippableFact]
    public async Task Key_selector_joins_and_paging()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>().Join<ProductTag, long>(p => p.Id, t => t.ProductId).OrderBy((p, t) => t.Weight);
        Assert.Equal(3L, (await q.CountAsync()).ValueOrThrow());
        var page = (await q.Skip(1).Take(1).ToListAsync()).ValueOrThrow();
        Assert.Equal(("A", "big"), (page[0].Item1.Sku, page[0].Item2!.Tag));
    }

    [SkippableFact]
    public async Task Soft_deleted_rows_are_filtered_on_both_sides()
    {
        RequireProvider();
        var (a, _, _) = await SeedAsync();
        await Db.Repository<Product>().DeleteAsync(a);
        var rows = (await Db.Query<ProductTag>().Join<Product>((t, p) => p.Id == t.ProductId).Select((t, p) => p.Sku).ToListAsync()).ValueOrThrow();
        Assert.Equal(["B"], rows);
    }
}
