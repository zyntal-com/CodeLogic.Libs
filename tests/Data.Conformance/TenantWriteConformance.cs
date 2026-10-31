using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>
/// Tenant isolation on every write path, and identity write-back for mixed inserts (audit 2026-10-31, findings 1, 2 and 4).
/// </summary>
public abstract class TenantWriteConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private async Task<TenantNote> SeedForTenantOneAsync()
    {
        var one = Db.ForTenant(1);
        return (await one.Repository<TenantNote>().InsertAsync(new TenantNote { Text = "one", Hits = 1 })).ValueOrThrow();
    }

    private async Task<TenantNote> ReadAsTenantOneAsync(Guid id) =>
        (await Db.ForTenant(1).Repository<TenantNote>().GetByIdAsync(id)).ValueOrThrow()!;

    [SkippableFact]
    public async Task Writes_without_a_tenant_are_refused_like_reads()
    {
        RequireProvider();
        var note = await SeedForTenantOneAsync();
        var unscoped = Db.Repository<TenantNote>();

        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.DeleteByIdAsync(note.Id)).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.HardDeleteAsync(note)).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.DeleteAsync(note)).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.IncrementAsync(note.Id, n => n.Hits)).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.DecrementAsync(note.Id, n => n.Hits)).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.AdjustAsync(note.Id, n => n.Hits, 5)).Error!.Code);
        note.Text = "changed without a tenant";
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.UpdateAsync(note)).Error!.Code);

        var stored = await ReadAsTenantOneAsync(note.Id);
        Assert.Equal(("one", 1), (stored.Text, stored.Hits));                // untouched
    }

    [SkippableFact]
    public async Task Inserts_upserts_and_bulk_copy_without_a_tenant_are_refused_even_with_a_tenant_value_set()
    {
        RequireProvider();
        var unscoped = Db.Repository<TenantNote>();
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.InsertAsync(new TenantNote { TenantId = 7, Text = "forged" })).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.InsertManyAsync([new TenantNote { TenantId = 7, Text = "forged" }])).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await unscoped.UpsertAsync(new TenantNote { TenantId = 7, Text = "forged" })).Error!.Code);
        Assert.Equal(DataErrors.TenantMissingCode, (await Db.Bulk<TenantNote>().InsertAsync([new TenantNote { TenantId = 7, Text = "forged" }])).Error!.Code);
        Assert.Empty((await Db.ForTenant(7).Query<TenantNote>().ToListAsync()).ValueOrThrow());

        // Writing for a specific tenant goes through a tenant view.
        var note = (await Db.ForTenant(7).Repository<TenantNote>().InsertAsync(new TenantNote { Text = "for seven" })).ValueOrThrow();
        Assert.Equal(7, note.TenantId);
    }

    [SkippableFact]
    public async Task Another_tenant_cannot_change_a_row_by_key()
    {
        RequireProvider();
        var note = await SeedForTenantOneAsync();
        var two = Db.ForTenant(2).Repository<TenantNote>();

        Assert.False((await two.DeleteByIdAsync(note.Id)).ValueOrThrow());
        Assert.False((await two.IncrementAsync(note.Id, n => n.Hits)).ValueOrThrow());
        Assert.Equal(1, (await ReadAsTenantOneAsync(note.Id)).Hits);
    }

    [SkippableFact]
    public async Task Upserts_never_change_another_tenants_row()
    {
        RequireProvider();
        var note = await SeedForTenantOneAsync();

        var hijack = new TenantNote { Id = note.Id, Text = "tenant two was here" };
        var result = await Db.ForTenant(2).Repository<TenantNote>().UpsertAsync(hijack);
        Assert.Equal(DataErrors.ConflictCode, result.Error!.Code);
        Assert.Equal("one", (await ReadAsTenantOneAsync(note.Id)).Text);

        // The same tenant still upserts its own row.
        (await Db.ForTenant(1).Repository<TenantNote>().UpsertAsync(new TenantNote { Id = note.Id, Text = "one, updated" })).ValueOrThrow();
        Assert.Equal("one, updated", (await ReadAsTenantOneAsync(note.Id)).Text);
    }

    [SkippableFact]
    public async Task The_sql_guard_alone_keeps_another_tenants_row_unchanged()
    {
        RequireProvider();
        var note = await SeedForTenantOneAsync();

        // Run only the upsert statement, without the pre-check, as if the other tenant's row appeared in between.
        var two = Db.ForTenant(2);
        var accessor = EntityAccessorRegistry.Get<TenantNote>().ValueOrThrow();
        List<TenantNote> rows = [new TenantNote { Id = note.Id, Text = "raced in" }];
        var plan = WritePlanner.Upsert(accessor, rows, two.WriteContext).ValueOrThrow();
        var upsertOnly = plan.Where(c => c.Check != RowCheck.Empty).ToList();
        Assert.Single(upsertOnly);
        (await two.ExecuteWritesAsync(upsertOnly, rows, accessor, CancellationToken.None)).ValueOrThrow();

        Assert.Equal("one", (await ReadAsTenantOneAsync(note.Id)).Text);
    }

    [SkippableFact]
    public async Task Mixed_identity_inserts_write_generated_ids_to_the_right_entities()
    {
        RequireProvider();
        var explicitId = 900_000 + Random.Shared.Next(1, 99_999);
        var first = new Product { Sku = "MIX-1", Name = "generated 1" };
        var middle = new Product { Id = explicitId, Sku = "MIX-2", Name = "explicit" };
        var last = new Product { Sku = "MIX-3", Name = "generated 2" };

        (await Db.Repository<Product>().InsertManyAsync([first, middle, last])).ValueOrThrow();

        Assert.Equal(explicitId, middle.Id);                                 // the explicit id is kept
        Assert.True(first.Id > 0 && last.Id > 0 && first.Id != last.Id && first.Id != explicitId && last.Id != explicitId);
        foreach (var product in new[] { first, middle, last })
            Assert.Equal(product.Sku, (await Db.Repository<Product>().GetByIdAsync(product.Id)).ValueOrThrow()!.Sku);
    }

    [SkippableFact]
    public async Task Mixed_identity_upserts_keep_the_explicit_identities()
    {
        RequireProvider();
        var explicitId = 800_000 + Random.Shared.Next(1, 99_999);
        var generated = new Product { Sku = "UMIX-1", Name = "generated" };
        var withId = new Product { Id = explicitId, Sku = "UMIX-2", Name = "explicit" };

        (await Db.Repository<Product>().UpsertManyAsync([generated, withId])).ValueOrThrow();

        var stored = (await Db.Repository<Product>().GetByIdAsync((long)explicitId)).ValueOrThrow();
        Assert.Equal("UMIX-2", stored?.Sku);                                 // inserted under its own identity
        Assert.Equal(1L, (await Db.Query<Product>().Where(p => p.Sku == "UMIX-1").CountAsync()).ValueOrThrow());
    }
}
