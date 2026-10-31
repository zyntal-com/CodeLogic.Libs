using System.Collections.Concurrent;
using CL.Data.Errors;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Transactions, savepoints, locking reads and UpdateAndGet.</summary>
public abstract class TransactionConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static Product New(string sku, int stock = 0) => new() { Sku = sku, Name = sku, Price = 1m, Stock = stock };

    [SkippableFact]
    public async Task Commit_makes_changes_visible_and_rollback_discards_them()
    {
        RequireProvider();
        await using (var tx = (await Db.BeginTransactionAsync()).ValueOrThrow())
        {
            await tx.Repository<Product>().InsertAsync(New("T1"));
            Assert.Equal(1L, (await tx.Query<Product>().CountAsync()).ValueOrThrow());   // visible inside
            Assert.Equal(0L, (await Db.Query<Product>().CountAsync()).ValueOrThrow());   // not outside yet
            (await tx.CommitAsync()).ThrowIfFailure();
        }
        Assert.Equal(1L, (await Db.Query<Product>().CountAsync()).ValueOrThrow());

        await using (var tx = (await Db.BeginTransactionAsync()).ValueOrThrow())
        {
            await tx.Repository<Product>().InsertAsync(New("T2"));
            (await tx.RollbackAsync()).ThrowIfFailure();
        }
        await using (var tx = (await Db.BeginTransactionAsync()).ValueOrThrow())
        {
            await tx.Db.ExecuteSqlAsync($"DELETE FROM conf_products");
            // disposed without commit
        }
        Assert.Equal(["T1"], (await Db.Query<Product>().Select(p => p.Sku).ToListAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task In_transaction_commits_on_success_and_rolls_back_on_failure()
    {
        RequireProvider();
        var ok = await Db.InTransactionAsync(async (tx, ct) =>
        {
            await tx.Repository<Product>().InsertAsync(New("OK"), ct);
            return await tx.Query<Product>().CountAsync(ct);
        });
        Assert.Equal(1L, ok.ValueOrThrow());

        var failed = await Db.InTransactionAsync<int>(async (tx, ct) =>
        {
            await tx.Repository<Product>().InsertAsync(New("GONE"), ct);
            return await tx.Repository<Product>().InsertManyAsync([New("OK")], ct);   // duplicate SKU
        });
        Assert.Equal(DataErrors.DuplicateKeyCode, failed.Error!.Code);
        Assert.Equal(["OK"], (await Db.Query<Product>().Select(p => p.Sku).ToListAsync()).ValueOrThrow());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Db.InTransactionAsync<int>(async (tx, ct) =>
            {
                await tx.Repository<Product>().InsertAsync(New("BOOM"), ct);
                throw new InvalidOperationException("bug");
            }));
        Assert.Equal(1L, (await Db.Query<Product>().CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Nested_units_join_the_outer_transaction_and_savepoints_roll_back_parts()
    {
        RequireProvider();
        await using var tx = (await Db.BeginTransactionAsync()).ValueOrThrow();
        var nested = await tx.Db.InTransactionAsync(async (inner, ct) =>
        {
            Assert.Same(tx, inner);
            return await inner.Repository<Product>().InsertAsync(New("N1"), ct);
        });
        Assert.True(nested.IsSuccess);

        (await tx.SavepointAsync("sp1")).ThrowIfFailure();
        await tx.Repository<Product>().InsertAsync(New("N2"));
        (await tx.RollbackToSavepointAsync("sp1")).ThrowIfFailure();
        (await tx.CommitAsync()).ThrowIfFailure();

        Assert.Equal(["N1"], (await Db.Query<Product>().Select(p => p.Sku).ToListAsync()).ValueOrThrow());
        Assert.Equal(DataErrors.ValidationCode, (await tx.Db.BeginTransactionAsync()).Error!.Code);
    }

    [SkippableFact]
    public async Task Locking_reads_need_a_transaction()
    {
        RequireProvider();
        await Db.Repository<Product>().InsertAsync(New("L"));
        Assert.Equal(DataErrors.ValidationCode, (await Db.Query<Product>().ForUpdate().ToListAsync()).Error!.Code);

        var locked = await Db.InTransactionAsync((tx, ct) => tx.Query<Product>().Where(p => p.Sku == "L").ForUpdate().ToListAsync(ct));
        Assert.Single(locked.ValueOrThrow());
    }

    [SkippableFact]
    public async Task Update_and_get_returns_the_updated_rows()
    {
        RequireProvider();
        await Db.Repository<Product>().InsertManyAsync([New("U1", 1), New("U2", 2), New("U3", 3)]);
        var updated = (await Db.Query<Product>().Where(p => p.Stock >= 2).UpdateAndGetAsync(p => new Product { Stock = p.Stock * 10 })).ValueOrThrow();

        Assert.Equal([20, 30], updated.Select(p => p.Stock).Order());
        Assert.All(updated, p => Assert.Equal(2, p.Version));
        Assert.Empty((await Db.Query<Product>().Where(p => p.Stock > 100).UpdateAndGetAsync(p => new Product { Stock = 0 })).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Concurrent_queue_claims_never_take_the_same_row()
    {
        RequireProvider();
        await Db.Repository<Product>().InsertManyAsync(Enumerable.Range(0, 12).Select(i => New($"J{i:00}")).ToList());
        ConcurrentBag<long> claimed = [];

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            while (true)
            {
                var jobs = await Db.Query<Product>().Where(p => p.Stock == 0).OrderBy(p => p.Id).Take(1)
                    .ForUpdate(LockWait.SkipLocked).UpdateAndGetAsync(p => new Product { Stock = 1 });
                if (jobs.IsFailure)
                {
                    if (jobs.Error!.Code is DataErrors.LockTimeoutCode or DataErrors.DeadlockCode) continue;
                    throw new InvalidOperationException(jobs.Error.ToString());
                }
                if (jobs.Value!.Count == 0) return;
                claimed.Add(jobs.Value[0].Id);
            }
        }));

        Assert.Equal(12, claimed.Count);
        Assert.Equal(12, claimed.Distinct().Count());
    }
}
