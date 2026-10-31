using CL.Data.Execution;
using CL.Data.Pipeline;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Updates that write only changed columns, and send nothing when nothing changed.</summary>
public abstract class ChangedUpdateConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private sealed class Recorder : IDataInterceptor
    {
        public readonly List<string> Commands = [];

        public ValueTask<CodeLogic.Core.Results.Error?> OnExecutingAsync(CommandContext context)
        {
            lock (Commands) Commands.Add(context.Spec.Sql);
            return ValueTask.FromResult<CodeLogic.Core.Results.Error?>(null);
        }
    }

    private async Task<Product> SeedAsync(Database db) =>
        (await db.Repository<Product>().InsertAsync(new Product { Sku = "CU-1", Name = "before", Price = 10m, Stock = 1 })).ValueOrThrow();

    [SkippableFact]
    public async Task Only_changed_columns_are_written()
    {
        RequireProvider();
        var recorder = new Recorder();
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Interceptors = [recorder] });
        var product = await SeedAsync(db);

        // Someone else changes the price (without touching the version); a name-only update must not overwrite it.
        (await db.ExecuteSqlAsync($"UPDATE conf_products SET {Sql.Identifier("Price")} = {99m} WHERE {Sql.Identifier("Id")} = {product.Id}")).ValueOrThrow();
        recorder.Commands.Clear();
        var version = product.Version;

        (await db.Repository<Product>().UpdateAsync(product, p => p.Name = "after")).ValueOrThrow();

        Assert.True(recorder.Commands.Count == 1, string.Join(" || ", recorder.Commands));
        var update = recorder.Commands[0];
        Assert.DoesNotContain("Price", update, StringComparison.Ordinal);
        Assert.DoesNotContain("Stock", update, StringComparison.Ordinal);
        var stored = (await db.Query<Product>().FirstOrDefaultAsync(p => p.Id == product.Id)).ValueOrThrow()!;
        Assert.Equal(("after", 99m), (stored.Name, stored.Price));
        Assert.Equal(version + 1, stored.Version);   // the concurrency token still advances
    }

    [SkippableFact]
    public async Task Nothing_changed_sends_nothing()
    {
        RequireProvider();
        var recorder = new Recorder();
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Interceptors = [recorder] });
        var product = await SeedAsync(db);
        recorder.Commands.Clear();
        var version = product.Version;

        var result = (await db.Repository<Product>().UpdateAsync(product, p => p.Name = "before")).ValueOrThrow();   // same value
        Assert.Same(product, result);
        Assert.True(recorder.Commands.Count == 0, string.Join(" || ", recorder.Commands));
        Assert.Equal(version, product.Version);
    }

    [SkippableFact]
    public async Task Detached_copies_and_snapshots_update_their_differences()
    {
        RequireProvider();
        var repo = Db.Repository<Product>();
        var product = await SeedAsync(Db);

        var original = (await Db.Query<Product>().FirstOrDefaultAsync(p => p.Id == product.Id)).ValueOrThrow()!;
        var edited = (await Db.Query<Product>().FirstOrDefaultAsync(p => p.Id == product.Id)).ValueOrThrow()!;
        edited.Stock = 42;
        (await repo.UpdateChangedAsync(original, edited)).ValueOrThrow();
        Assert.Equal(42, (await Db.Query<Product>().FirstOrDefaultAsync(p => p.Id == product.Id)).ValueOrThrow()!.Stock);

        var snapshot = repo.Snapshot(edited).ValueOrThrow();
        edited.Status = ProductStatus.Live;   // a converter column (stored as text)
        Assert.Equal([nameof(Product.Status)], snapshot.ChangedProperties(edited, CL.Data.Mapping.EntityAccessorRegistry.Get<Product>().ValueOrThrow().Map));
        (await repo.UpdateAsync(edited, snapshot)).ValueOrThrow();
        Assert.Equal(ProductStatus.Live, (await Db.Query<Product>().FirstOrDefaultAsync(p => p.Id == product.Id)).ValueOrThrow()!.Status);
    }
}
