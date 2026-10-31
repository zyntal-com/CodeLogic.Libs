using CL.Data.Execution;
using CL.Data.Query;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Global (user) filters, IgnoreFilters, and tenant-scoped database views.</summary>
public abstract class FilterConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static readonly CompiledQuery<Product, long> ById = CompiledQuery.For<Product, long>((q, id) => q.Where(p => p.Id == id));

    private Database Filtered() => Fixture.CreateDatabase(new DatabaseOptions
    {
        Filters = [DataFilter.For<IHasStock>(e => e.Stock > 0, "in-stock"), DataFilter.For<Product>(p => !p.Sku.StartsWith("HIDDEN"), "hidden")],
    });

    private async Task<List<Product>> SeedAsync()
    {
        List<Product> rows =
        [
            new() { Sku = "F-1", Name = "a", Price = 1, Stock = 5 },
            new() { Sku = "F-2", Name = "b", Price = 2, Stock = 0 },
            new() { Sku = "HIDDEN-3", Name = "c", Price = 3, Stock = 7 },
        ];
        (await Db.Repository<Product>().InsertManyAsync(rows)).ValueOrThrow();
        return rows;
    }

    [SkippableFact]
    public async Task User_filters_apply_to_every_read()
    {
        RequireProvider();
        var rows = await SeedAsync();
        await using var db = Filtered();

        Assert.Equal(["F-1"], (await db.Query<Product>().ToListAsync()).ValueOrThrow().Select(p => p.Sku));
        Assert.Equal(1L, (await db.Repository<Product>().CountAsync()).ValueOrThrow());
        Assert.Null((await db.Repository<Product>().GetByIdAsync(rows[1].Id)).ValueOrThrow());   // key lookups too
        Assert.Null((await ById.FirstOrDefaultAsync(db, rows[2].Id)).ValueOrThrow());            // and compiled queries

        Assert.Equal(3L, (await db.Query<Product>().IgnoreFilters().CountAsync()).ValueOrThrow());
        Assert.Equal(2L, (await db.Query<Product>().IgnoreFilters("in-stock").CountAsync()).ValueOrThrow());
        Assert.Equal(["F-1", "HIDDEN-3"], (await db.Query<Product>().IgnoreFilters("hidden").OrderBy(p => p.Sku).ToListAsync()).ValueOrThrow().Select(p => p.Sku));
    }

    [SkippableFact]
    public async Task Set_based_writes_only_touch_visible_rows()
    {
        RequireProvider();
        var rows = await SeedAsync();
        await using var db = Filtered();

        Assert.Equal(1, (await db.Query<Product>().DeleteAsync(hard: true)).ValueOrThrow());
        Assert.Equal(2L, (await Db.Query<Product>().CountAsync()).ValueOrThrow());

        // UpdateAndGet returns the updated rows even when they no longer match the filters.
        var restocked = (await db.Query<Product>().IgnoreFilters().Where(p => p.Id == rows[1].Id).UpdateAndGetAsync(p => new Product { Stock = 0 })).ValueOrThrow();
        Assert.Equal("F-2", Assert.Single(restocked).Sku);
    }

    [SkippableFact]
    public async Task Tenant_views_stamp_and_isolate()
    {
        RequireProvider();
        var one = Db.ForTenant(1);
        var two = Db.ForTenant(2);
        (await one.Repository<TenantNote>().InsertAsync(new TenantNote { Text = "for one" })).ValueOrThrow();
        (await two.Repository<TenantNote>().InsertAsync(new TenantNote { Text = "for two" })).ValueOrThrow();

        var ones = (await one.Query<TenantNote>().ToListAsync()).ValueOrThrow();
        Assert.Equal(("for one", 1), (Assert.Single(ones).Text, ones[0].TenantId));
        Assert.Equal("for two", Assert.Single((await two.Query<TenantNote>().ToListAsync()).ValueOrThrow()).Text);

        await one.DisposeAsync();   // a view: the shared pools stay open
        Assert.Single((await two.Query<TenantNote>().ToListAsync()).ValueOrThrow());
    }
}
