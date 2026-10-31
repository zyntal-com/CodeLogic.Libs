using CL.Data;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>GROUP BY, HAVING and group aggregates.</summary>
public abstract class GroupingConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task SeedAsync() =>
        (await Db.Repository<Product>().InsertManyAsync([
            new Product { Sku = "a1", Name = "a", Price = 10m, Stock = 1, Status = ProductStatus.Live },
            new Product { Sku = "a2", Name = "a", Price = 20m, Stock = 2, Status = ProductStatus.Live },
            new Product { Sku = "a3", Name = "a", Price = 30m, Stock = 0, Status = ProductStatus.Draft },
            new Product { Sku = "b1", Name = "b", Price = 5m, Stock = 4, Status = ProductStatus.Live },
            new Product { Sku = "c1", Name = "c", Price = 7m, Stock = 3, Status = ProductStatus.Retired, Active = false },
        ])).ValueOrThrow();

    [SkippableFact]
    public async Task Group_aggregates_and_ordering()
    {
        RequireProvider();
        await SeedAsync();
        var rows = (await Db.Query<Product>()
            .GroupBy(p => p.Name)
            .OrderByDescending(g => g.Sum(p => p.Price))
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Total = g.Sum(p => p.Price),
                Avg = g.Average(p => p.Stock),
                Max = g.Max(p => p.Price),
                InStock = g.Count(p => p.Stock > 0),
                Statuses = g.CountDistinct(p => p.Status),
            })
            .ToListAsync()).ValueOrThrow();

        Assert.Equal(["a", "c", "b"], rows.Select(r => r.Name));
        var a = rows[0];
        Assert.Equal((3, 60m, 1.0, 30m, 2, 2), (a.Count, a.Total, a.Avg, a.Max, a.InStock, a.Statuses));   // AVG(1,2,0) = 1.0, not truncated
    }

    [SkippableFact]
    public async Task Having_filters_groups_and_where_filters_rows()
    {
        RequireProvider();
        await SeedAsync();
        var names = (await Db.Query<Product>().Where(p => p.Active)
            .GroupBy(p => p.Name)
            .Having(g => g.Sum(p => p.Price) > 6m && g.Any(p => p.Stock > 1))
            .OrderBy(g => g.Key)
            .Select(g => g.Key)
            .ToListAsync()).ValueOrThrow();
        Assert.Equal(["a"], names);
    }

    [SkippableFact]
    public async Task Composite_and_computed_keys()
    {
        RequireProvider();
        await SeedAsync();
        var rows = (await Db.Query<Product>()
            .GroupBy(p => new { p.Name, p.Status })
            .OrderBy(g => g.Key.Name).ThenBy(g => g.Key.Status)
            .Select(g => new { g.Key.Name, g.Key.Status, N = g.Count() })
            .ToListAsync()).ValueOrThrow();
        Assert.Equal(4, rows.Count);
        Assert.Equal(("a", ProductStatus.Draft, 1), (rows[0].Name, rows[0].Status, rows[0].N));
        Assert.Equal(("a", ProductStatus.Live, 2), (rows[1].Name, rows[1].Status, rows[1].N));

        var buckets = (await Db.Query<Product>().GroupBy(p => p.Price > 8m)
            .Select(g => new { Expensive = g.Key, N = g.Count() }).ToListAsync()).ValueOrThrow();
        Assert.Equal([(false, 2), (true, 3)], buckets.Select(b => (b.Expensive, b.N)).OrderBy(b => b.Expensive));
    }

    [SkippableFact]
    public async Task Paged_groups_and_first()
    {
        RequireProvider();
        await SeedAsync();
        var second = (await Db.Query<Product>().GroupBy(p => p.Name).OrderBy(g => g.Key).Skip(1).Take(1)
            .Select(g => new { g.Key, N = g.LongCount() }).ToListAsync()).ValueOrThrow();
        Assert.Equal(("b", 1L), (second.Single().Key, second.Single().N));

        var top = (await Db.Query<Product>().GroupBy(p => p.Name).OrderByDescending(g => g.Count())
            .Select(g => g.Key).FirstOrDefaultAsync()).ValueOrThrow();
        Assert.Equal("a", top);
    }
}
