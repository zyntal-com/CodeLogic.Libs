using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Writes;
using CodeLogic.Core.Results;
using Xunit;

namespace Data.Conformance;

/// <summary>Repository CRUD behavior every provider must show.</summary>
public abstract class RepositoryConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static Product NewProduct(string sku, decimal price = 9.99m, int stock = 5) =>
        new() { Sku = sku, Name = $"Product {sku}", Price = price, Stock = stock, Status = ProductStatus.Live };

    [SkippableFact]
    public async Task Insert_writes_back_identity_audit_and_token()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var a = (await repo.InsertAsync(NewProduct("A"))).ValueOrThrow();
        var b = (await repo.InsertAsync(NewProduct("B"))).ValueOrThrow();

        Assert.True(a.Id > 0);
        Assert.True(b.Id > a.Id);
        Assert.Equal(1, a.Version);
        Assert.NotEqual(default, a.CreatedAt);
        Assert.Equal(a.CreatedAt, a.UpdatedAt);
    }

    [SkippableFact]
    public async Task Insert_many_fills_every_identity()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var products = Enumerable.Range(0, 25).Select(i => NewProduct($"M{i:00}", i)).ToList();
        Assert.Equal(25, (await repo.InsertManyAsync(products)).ValueOrThrow());
        Assert.All(products, p => Assert.True(p.Id > 0));
        Assert.Equal(25, products.Select(p => p.Id).Distinct().Count());

        var loaded = (await repo.GetByIdAsync(products[17].Id)).ValueOrThrow();
        Assert.Equal("M17", loaded!.Sku);                                        // identities match their rows
    }

    [SkippableFact]
    public async Task Get_by_id_round_trips_every_type()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = NewProduct("RT", 1234.56m, 42);
        p.Description = "with 'quotes' and ünïcode ✓";
        p.Active = false;
        p.Status = ProductStatus.Retired;
        await repo.InsertAsync(p);

        var loaded = (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!;
        Assert.Equal(p.Sku, loaded.Sku);
        Assert.Equal(p.Description, loaded.Description);
        Assert.Equal(1234.56m, loaded.Price);
        Assert.Equal(42, loaded.Stock);
        Assert.False(loaded.Active);
        Assert.Equal(ProductStatus.Retired, loaded.Status);
        Assert.Equal(p.Ref, loaded.Ref);
        Assert.Equal(p.CreatedAt.ToUnixTimeMilliseconds(), loaded.CreatedAt.ToUnixTimeMilliseconds());
        Assert.Null(loaded.DeletedAt);

        Assert.Null((await repo.GetByIdAsync(-1L)).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Find_count_and_all()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        await repo.InsertManyAsync([NewProduct("F1", 5), NewProduct("F2", 15), NewProduct("F3", 25)]);
        var min = 10m;

        var expensive = (await repo.FindAsync(p => p.Price > min && p.Active)).ValueOrThrow();
        Assert.Equal(["F2", "F3"], expensive.Select(p => p.Sku).Order());
        Assert.Equal(3, (await repo.GetAllAsync()).ValueOrThrow().Count);
        Assert.Equal(3L, (await repo.CountAsync()).ValueOrThrow());
        Assert.Equal(1L, (await repo.CountAsync(p => p.Sku.StartsWith("F3"))).ValueOrThrow());
        Assert.Single((await repo.FindAsync(p => p.Status == ProductStatus.Live && p.Sku == "F1")).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Paging_reports_has_more_and_optional_total()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        await repo.InsertManyAsync(Enumerable.Range(0, 7).Select(i => NewProduct($"P{i}")).ToList());

        var first = (await repo.GetPagedAsync(1, 3)).ValueOrThrow();
        Assert.Equal(3, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Null(first.TotalCount);

        var last = (await repo.GetPagedAsync(3, 3, withTotal: true)).ValueOrThrow();
        Assert.Single(last.Items);
        Assert.False(last.HasMore);
        Assert.Equal(7L, last.TotalCount);
        Assert.Equal(3, last.TotalPages);
    }

    [SkippableFact]
    public async Task Update_bumps_the_token_and_detects_stale_copies()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = (await repo.InsertAsync(NewProduct("U"))).ValueOrThrow();
        var stale = (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!;

        p.Price = 20m;
        Assert.True((await repo.UpdateAsync(p)).IsSuccess);
        Assert.Equal(2, p.Version);
        Assert.Equal(20m, (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!.Price);

        stale.Price = 30m;
        var conflict = await repo.UpdateAsync(stale);
        Assert.Equal(DataErrors.ConcurrencyCode, conflict.Error!.Code);
        Assert.Equal(20m, (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!.Price);
    }

    [SkippableFact]
    public async Task Update_of_selected_properties_leaves_the_rest()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = (await repo.InsertAsync(NewProduct("S", 5m, 1))).ValueOrThrow();
        p.Stock = 99;
        p.Price = 77m;                                                           // not in the update list
        (await repo.UpdateAsync(p, [x => x.Stock])).ValueOrThrow();

        var loaded = (await repo.GetByIdAsync(p.Id)).ValueOrThrow()!;
        Assert.Equal(99, loaded.Stock);
        Assert.Equal(5m, loaded.Price);
    }

    [SkippableFact]
    public async Task Soft_delete_hides_rows_and_hard_delete_removes_them()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var p = (await repo.InsertAsync(NewProduct("D"))).ValueOrThrow();

        Assert.True((await repo.DeleteAsync(p)).ValueOrThrow());
        Assert.NotNull(p.DeletedAt);
        Assert.Null((await repo.GetByIdAsync(p.Id)).ValueOrThrow());
        Assert.Equal(0L, (await repo.CountAsync()).ValueOrThrow());
        Assert.Equal(1L, (await Db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM conf_products")).ValueOrThrow());

        Assert.True((await repo.DeleteByKeyAsync(EntityKey.Of(p.Id), hard: true)).ValueOrThrow());
        Assert.Equal(0L, (await Db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM conf_products")).ValueOrThrow());
        Assert.False((await repo.DeleteByIdAsync(p.Id)).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Duplicate_unique_values_are_conflicts()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        await repo.InsertAsync(NewProduct("DUP"));
        var result = await repo.InsertAsync(NewProduct("DUP"));
        Assert.Equal(DataErrors.DuplicateKeyCode, result.Error!.Code);
    }

    [SkippableFact]
    public async Task Composite_keys()
    {
        RequireProvider();
        var repo = Db.Repository<ProductTag>();
        await repo.InsertManyAsync([new ProductTag { ProductId = 1, Tag = "a", Weight = 1 }, new ProductTag { ProductId = 1, Tag = "b", Weight = 2 }]);

        var tag = (await repo.GetByKeyAsync(EntityKey.Of(1L, "b"))).ValueOrThrow()!;
        Assert.Equal(2, tag.Weight);
        tag.Weight = 5;
        (await repo.UpdateAsync(tag)).ValueOrThrow();
        Assert.Equal(5, (await repo.GetByKeyAsync(EntityKey.Of(1L, "b"))).ValueOrThrow()!.Weight);
        Assert.True((await repo.DeleteByKeyAsync(EntityKey.Of(1L, "a"))).ValueOrThrow());
        Assert.Equal(1L, (await repo.CountAsync()).ValueOrThrow());
        Assert.Equal(DataErrors.ValidationCode, (await repo.GetByKeyAsync(EntityKey.Of(1L))).Error!.Code);
    }

    [SkippableFact]
    public async Task Tenant_scoped_entities_need_a_tenant_and_stay_separate()
    {
        RequireProvider();
        Assert.Equal(DataErrors.TenantMissingCode, (await Db.Repository<TenantNote>().GetAllAsync()).Error!.Code);

        await using var tenant1 = Fixture.CreateDatabase(new DatabaseOptions { TenantAccessor = new FixedTenant(1) });
        await using var tenant2 = Fixture.CreateDatabase(new DatabaseOptions { TenantAccessor = new FixedTenant(2) });
        var note = (await tenant1.Repository<TenantNote>().InsertAsync(new TenantNote { Text = "one" })).ValueOrThrow();
        await tenant2.Repository<TenantNote>().InsertAsync(new TenantNote { Text = "two" });

        Assert.Equal(1, note.TenantId);
        Assert.Equal(["one"], (await tenant1.Repository<TenantNote>().GetAllAsync()).ValueOrThrow().Select(n => n.Text));
        Assert.Null((await tenant2.Repository<TenantNote>().GetByIdAsync(note.Id)).ValueOrThrow());
        Assert.False((await tenant2.Repository<TenantNote>().DeleteByIdAsync(note.Id)).ValueOrThrow());
    }

    private sealed class FixedTenant(int tenant) : IDataTenantAccessor
    {
        public object? CurrentTenant => tenant;
    }
}
