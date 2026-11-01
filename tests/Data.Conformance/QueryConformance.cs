using CL.Data.Errors;
using CodeLogic.Core.Results;
using Xunit;

namespace Data.Conformance;

/// <summary>QueryBuilder behavior every provider must show.</summary>
public abstract class QueryConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task SeedAsync()
    {
        var products = new[]
        {
            new Product { Sku = "A1", Name = "Alpha", Price = 10m, Stock = 5, Status = ProductStatus.Live },
            new Product { Sku = "B2", Name = "Bravo", Price = 20m, Stock = 0, Status = ProductStatus.Live },
            new Product { Sku = "C3", Name = "Charlie", Price = 30m, Stock = 7, Status = ProductStatus.Draft, Active = false },
            new Product { Sku = "D4", Name = "Delta", Price = 20m, Stock = 2, Status = ProductStatus.Retired, Description = "50% off_now" },
        };
        (await Db.Repository<Product>().InsertManyAsync(products)).ValueOrThrow();
        (await Db.Repository<ProductTag>().InsertManyAsync([
            new ProductTag { ProductId = products[0].Id, Tag = "new", Weight = 1 },
            new ProductTag { ProductId = products[2].Id, Tag = "new", Weight = 1 },
            new ProductTag { ProductId = products[2].Id, Tag = "sale", Weight = 1 },
        ])).ValueOrThrow();
    }

    [SkippableFact]
    public async Task Where_order_take_skip()
    {
        RequireProvider();
        await SeedAsync();
        var query = Db.Query<Product>().Where(p => p.Price >= 20m).OrderByDescending(p => p.Price).ThenBy(p => p.Sku);

        Assert.Equal(["C3", "B2", "D4"], (await query.ToListAsync()).ValueOrThrow().Select(p => p.Sku));
        Assert.Equal(["B2", "D4"], (await query.Skip(1).Take(2).ToListAsync()).ValueOrThrow().Select(p => p.Sku));
        Assert.Equal("C3", (await query.FirstOrDefaultAsync()).ValueOrThrow()!.Sku);
        Assert.Equal(3, (await query.ToListAsync()).ValueOrThrow().Count);       // builders are immutable: Take did not stick
    }

    [SkippableFact]
    public async Task Conditions_translate_portably()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>();
        string[] skus = ["A1", "D4", "ZZ"];

        Assert.Equal(["A1", "D4"], Sorted(await q.Where(p => skus.Contains(p.Sku)).ToListAsync()));
        Assert.Equal(["C3"], Sorted(await q.Where(p => !p.Active).ToListAsync()));
        Assert.Equal(["D4"], Sorted(await q.Where(p => p.Description != null && p.Description.Contains("% off_")).ToListAsync()));
        Assert.Equal(["B2", "D4"], Sorted(await q.Where(p => p.Price * 2 == 40m).ToListAsync()));
        Assert.Equal(["C3"], Sorted(await q.Where(p => p.Status == ProductStatus.Draft).ToListAsync()));
        Assert.Equal(["A1", "B2"], Sorted(await q.Where(p => p.Name.StartsWith("A") || p.Name.EndsWith("vo")).ToListAsync()));
        Assert.Equal(["A1", "C3"], Sorted(await q.Where(p => p.Stock > 0 && p.Stock < 10 && p.Price != 20m).ToListAsync()));
    }

    [SkippableFact]
    public async Task Aggregates()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>();

        Assert.Equal(4L, (await q.CountAsync()).ValueOrThrow());
        Assert.Equal(2L, (await q.Where(p => p.Price == 20m).CountAsync()).ValueOrThrow());
        Assert.True((await q.AnyAsync(p => p.Stock == 0)).ValueOrThrow());
        Assert.False((await q.AnyAsync(p => p.Stock > 100)).ValueOrThrow());
        Assert.Equal(30m, (await q.MaxAsync(p => p.Price)).ValueOrThrow());
        Assert.Equal(0, (await q.MinAsync(p => p.Stock)).ValueOrThrow());
        Assert.Equal(14, (await q.SumAsync(p => p.Stock)).ValueOrThrow());
        Assert.Equal(20m, (await q.AverageAsync(p => p.Price)).ValueOrThrow());
        Assert.Equal(0, (await q.Where(p => p.Price > 1000m).SumAsync(p => p.Stock)).ValueOrThrow());
        Assert.Null((await q.Where(p => p.Price > 1000m).MaxAsync(p => p.Price)).ValueOrThrow());
        Assert.Null((await q.Where(p => p.Price > 1000m).MinAsync(p => p.Stock)).ValueOrThrow());
        Assert.Null((await q.Where(p => p.Price > 1000m).AverageAsync(p => p.Price)).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Queries_over_the_parameter_limit_fail_clearly_before_sending()
    {
        RequireProvider();
        var ids = Enumerable.Range(1, Db.Provider.Capabilities.ParameterLimit + 1).Select(i => (long)i).ToArray();
        var result = await Db.Query<Product>().Where(p => ids.Contains(p.Id)).ToListAsync();
        Assert.Equal(DataErrors.ParameterLimitCode, result.Error!.Code);
    }

    [SkippableFact]
    public async Task Set_based_updates_refuse_key_columns()
    {
        RequireProvider();
        await SeedAsync();
        var result = await Db.Query<Product>().Where(p => p.Stock > 0).UpdateAsync(p => new Product { Id = 999 });
        Assert.Equal(DataErrors.ValidationCode, result.Error!.Code);
        Assert.DoesNotContain(999L, (await Db.Query<Product>().ToListAsync()).ValueOrThrow().Select(p => p.Id));
    }

    [SkippableFact]
    public async Task Exclude_leaves_properties_at_their_defaults()
    {
        RequireProvider();
        await SeedAsync();
        var row = (await Db.Query<Product>().Where(p => p.Sku == "D4").Exclude(p => p.Description, p => p.Name).FirstOrDefaultAsync()).ValueOrThrow()!;
        Assert.Null(row.Description);
        Assert.Equal("", row.Name);
        Assert.Equal(20m, row.Price);
        Assert.Equal(DataErrors.ValidationCode, (await Db.Query<Product>().Exclude(p => p.Id).ToListAsync()).Error!.Code);
    }

    [SkippableFact]
    public async Task Exists_and_in_subqueries()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>();

        Assert.Equal(["A1", "C3"], Sorted(await q.WhereExists<ProductTag>((p, t) => t.ProductId == p.Id && t.Tag == "new").ToListAsync()));
        Assert.Equal(["B2", "D4"], Sorted(await q.WhereNotExists<ProductTag>((p, t) => t.ProductId == p.Id).ToListAsync()));
        Assert.Equal(["C3"], Sorted(await q.WhereIn<ProductTag, long>(p => p.Id, t => t.ProductId, t => t.Tag == "sale").ToListAsync()));
        Assert.Equal(["A1", "B2", "D4"], Sorted(await q.WhereNotIn<ProductTag, long>(p => p.Id, t => t.ProductId, t => t.Tag == "sale").ToListAsync()));
        Assert.Equal(1L, (await q.WhereExists<ProductTag>((p, t) => t.ProductId == p.Id && t.Tag == "sale").CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Set_based_update_and_delete()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>();

        Assert.Equal(2, (await q.Where(p => p.Price == 20m).UpdateAsync(p => new Product { Stock = p.Stock + 10, Name = "Twenty" })).ValueOrThrow());
        var twenty = (await q.Where(p => p.Price == 20m).ToListAsync()).ValueOrThrow();
        Assert.All(twenty, p => Assert.Equal("Twenty", p.Name));
        Assert.Equal([10, 12], twenty.Select(p => p.Stock).Order());
        Assert.All(twenty, p => Assert.Equal(2, p.Version));                     // bulk update bumped the token

        Assert.Equal(1, (await q.Where(p => p.Sku == "A1").DeleteAsync()).ValueOrThrow());         // soft
        Assert.Equal(3L, (await q.CountAsync()).ValueOrThrow());
        Assert.Equal(4L, (await q.IncludeDeleted().CountAsync()).ValueOrThrow());
        Assert.Equal(1, (await q.IncludeDeleted().Where(p => p.Sku == "A1").DeleteAsync(hard: true)).ValueOrThrow());
        Assert.Equal(3L, (await q.IncludeDeleted().CountAsync()).ValueOrThrow());

        Assert.Equal(1, (await q.WhereExists<ProductTag>((p, t) => t.ProductId == p.Id && t.Tag == "sale")
            .UpdateAsync(p => new Product { Stock = 0 })).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Ordered_limited_delete_removes_a_batch_in_order()
    {
        RequireProvider();
        await SeedAsync();
        var q = Db.Query<Product>().IncludeDeleted();

        // Batched prune: the two highest SKUs go first, the rest stay.
        Assert.Equal(2, (await q.OrderByDescending(p => p.Sku).Take(2).DeleteAsync(hard: true)).ValueOrThrow());
        Assert.Equal(["A1", "B2"], Sorted(await q.ToListAsync()));
        Assert.Equal(1, (await q.Where(p => p.Price > 0m).OrderBy(p => p.Sku).Take(1).DeleteAsync(hard: true)).ValueOrThrow());
        Assert.Equal(["B2"], Sorted(await q.ToListAsync()));

        // Never silently widened: what cannot be honoured is refused.
        Assert.Equal(DataErrors.ValidationCode, (await q.OrderBy(p => p.Sku).Skip(1).Take(1).DeleteAsync(hard: true)).Error!.Code);
        Assert.Equal(DataErrors.ValidationCode, (await Db.Query<Product>().Take(1).DeleteAsync()).Error!.Code);        // soft delete
        Assert.Equal(DataErrors.ValidationCode, (await q.Take(1).UpdateAsync(p => new Product { Stock = 1 })).Error!.Code);
        Assert.Equal(["B2"], Sorted(await q.ToListAsync()));
    }

    [SkippableFact]
    public async Task Untranslatable_lambdas_fail_the_terminal_call()
    {
        RequireProvider();
#pragma warning disable CLD101 // intentionally untranslatable
        var result = await Db.Query<Product>().Where(p => p.Name.GetHashCode() == 1).ToListAsync();
#pragma warning restore CLD101
        Assert.Equal(DataErrors.TranslationFailedCode, result.Error!.Code);
    }

    private static IEnumerable<string> Sorted(Result<List<Product>> rows) => rows.ValueOrThrow().Select(p => p.Sku).Order();
}
