using CL.Data.Errors;
using CL.Data.Query;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Compiled queries return what the equivalent regular query returns, for changing arguments.</summary>
public abstract class CompiledQueryConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static readonly CompiledQuery<Product, long> ById =
        CompiledQuery.For<Product, long>((q, id) => q.Where(p => p.Id == id));

    private static readonly CompiledQuery<Product, (string? Name, decimal Min)> Search =
        CompiledQuery.For<Product, (string? Name, decimal Min)>((q, a) => q.Where(p => (a.Name == null || p.Name == a.Name) && p.Price >= a.Min).OrderBy(p => p.Sku));

    private static readonly CompiledQuery<Product, long[]> InIds =
        CompiledQuery.For<Product, long[]>((q, ids) => q.Where(p => ids.Contains(p.Id)).OrderBy(p => p.Id));

    private static readonly CompiledQuery<Product, string> SkuPrefix =
        CompiledQuery.For<Product, string>((q, prefix) => q.Where(p => p.Sku.StartsWith(prefix)).Take(2).OrderBy(p => p.Sku));

    private static readonly CompiledQuery<Product, int> BadTake =
        CompiledQuery.For<Product, int>((q, n) => q.Take(n));

    private async Task<List<Product>> SeedAsync()
    {
        var rows = Enumerable.Range(1, 6).Select(i => new Product { Sku = $"CQ-{i}", Name = i % 2 == 0 ? "even" : "odd", Price = i * 10m, Stock = i }).ToList();
        (await Db.Repository<Product>().InsertManyAsync(rows)).ValueOrThrow();
        return rows;
    }

    [SkippableFact]
    public async Task Compiled_queries_match_regular_queries()
    {
        RequireProvider();
        var rows = await SeedAsync();

        foreach (var row in rows)
            Assert.Equal(row.Sku, (await ById.FirstOrDefaultAsync(Db, row.Id)).ValueOrThrow()!.Sku);
        Assert.Null((await ById.FirstOrDefaultAsync(Db, -1)).ValueOrThrow());

        foreach (var (name, min) in new (string?, decimal)[] { ("even", 0m), (null, 30m), ("odd", 20m), (null, 0m), ("none", 0m) })
        {
            var expected = (await Db.Query<Product>().Where(p => (name == null || p.Name == name) && p.Price >= min).OrderBy(p => p.Sku).ToListAsync()).ValueOrThrow();
            var compiled = (await Search.ToListAsync(Db, (name, min))).ValueOrThrow();
            Assert.Equal(expected.Select(p => p.Sku), compiled.Select(p => p.Sku));
            Assert.Equal(expected.Count, (await Search.CountAsync(Db, (name, min))).ValueOrThrow());
            Assert.Equal(expected.Count > 0, (await Search.AnyAsync(Db, (name, min))).ValueOrThrow());
        }

        foreach (var ids in new long[][] { [rows[0].Id, rows[2].Id], [rows[1].Id], [], [rows[0].Id, rows[1].Id, rows[5].Id] })
            Assert.Equal(ids.Order(), (await InIds.ToListAsync(Db, ids)).ValueOrThrow().Select(p => p.Id));

        Assert.Equal(["CQ-1"], (await SkuPrefix.ToListAsync(Db, "CQ-1")).ValueOrThrow().Select(p => p.Sku));
        Assert.Equal(2, (await SkuPrefix.ToListAsync(Db, "CQ-")).ValueOrThrow().Count);
    }

    [SkippableFact]
    public async Task Standing_filters_apply_and_bad_chains_are_reported()
    {
        RequireProvider();
        var rows = await SeedAsync();
        (await Db.Repository<Product>().DeleteAsync(rows[0])).ValueOrThrow();   // soft delete
        Assert.Null((await ById.FirstOrDefaultAsync(Db, rows[0].Id)).ValueOrThrow());
        Assert.Equal(5, (await Search.CountAsync(Db, (null, 0m))).ValueOrThrow());

        var bad = await BadTake.ToListAsync(Db, 3);
        Assert.True(bad.IsFailure);
        Assert.Equal(DataErrors.UnsupportedCode, bad.Error!.Code);
    }
}
