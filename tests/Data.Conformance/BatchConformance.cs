using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Batches: several queries in one round trip.</summary>
public abstract class BatchConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private sealed class Counter : IDataInterceptor
    {
        public int Commands;

        public ValueTask<Error?> OnExecutingAsync(CommandContext context)
        {
            Interlocked.Increment(ref Commands);
            return ValueTask.FromResult<Error?>(null);
        }
    }

    [SkippableFact]
    public async Task Several_queries_run_as_one_command()
    {
        RequireProvider();
        (await Db.Repository<Product>().InsertManyAsync(
            Enumerable.Range(0, 6).Select(i => new Product { Sku = $"B{i}", Name = $"n{i}", Price = i, Stock = i }).ToList())).ValueOrThrow();

        var counter = new Counter();
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Interceptors = [counter] });
        var batch = db.Batch();
        var cheap = batch.Add(db.Query<Product>().Where(p => p.Price < 3m).OrderBy(p => p.Sku));
        var total = batch.Count(db.Query<Product>());
        var expensive = batch.Count(db.Query<Product>().Where(p => p.Price >= 3m));
        var names = batch.Sql<string>($"SELECT Name FROM conf_products WHERE Stock = {5}");

        (await batch.ExecuteAsync()).Ensure();

        Assert.Equal(1, counter.Commands);
        Assert.Equal(["B0", "B1", "B2"], cheap.Value.Select(p => p.Sku));
        Assert.Equal(6L, total.Value);
        Assert.Equal(3L, expensive.Value);
        Assert.Equal(["n5"], names.Value);
    }

    [SkippableFact]
    public async Task A_planning_error_runs_nothing()
    {
        RequireProvider();
        var batch = Db.Batch();
        var ok = batch.Count(Db.Query<Product>());
        batch.Add(Db.Query<Product>().Where(p => p.Name.GetHashCode() == 1));
        var result = await batch.ExecuteAsync();
        Assert.Equal(DataErrors.TranslationFailedCode, result.Error!.Code);
        Assert.Throws<InvalidOperationException>(() => ok.Value);
    }
}
