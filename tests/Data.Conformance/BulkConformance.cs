using CL.Data.Bulk;
using CL.Data.Caching;
using CL.Data.Configuration;
using CL.Data.Execution;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Bulk copy: native paths x4, the batched fallback, stamping, conversion, transactions and cache invalidation.</summary>
public abstract class BulkConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static IEnumerable<Product> Rows(int count, string prefix, Func<int, long>? id = null) =>
        Enumerable.Range(0, count).Select(i => new Product
        {
            Id = id?.Invoke(i) ?? 0,
            Sku = $"{prefix}-{i:D6}",
            Name = $"bulk {i}",
            Description = i % 3 == 0 ? null : "d",
            Price = 10.25m + i,
            Stock = i,
            Active = i % 2 == 0,
            Status = (ProductStatus)(i % 3),
        });

    private Database Copy(Action<DataConnectionConfig>? configure = null, DataCache? cache = null)
    {
        var config = new DataConnectionConfig
        {
            Provider = Db.Config.Provider,
            ConnectionString = Db.Config.ConnectionString,
            Database = Db.Config.Database,
            MySql = Db.Config.MySql,
            PostgreSql = Db.Config.PostgreSql,
            SqlServer = Db.Config.SqlServer,
            Sqlite = Db.Config.Sqlite,
        };
        configure?.Invoke(config);
        return Database.Create(Db.Name, Fixture.Provider, config, new DatabaseOptions { Cache = cache }).ValueOrThrow();
    }

    [SkippableFact]
    public async Task Ten_thousand_rows_copy_natively_with_stamping_and_conversion()
    {
        RequireProvider();
        var source = Rows(10_000, "B").ToList();
        var receipt = (await Db.Bulk<Product>().InsertAsync(source)).ValueOrThrow();

        Assert.Equal(10_000, receipt.Rows);
        // MySQL's native path (LOAD DATA LOCAL INFILE) also needs the server's local_infile, which MySQL 8.4 ships off;
        // without it CL.Data falls back to batched inserts, which must give the same result.
        var nativeExpected = Db.Provider.Name != "mysql"
            || (await Db.SqlScalarAsync<long>($"SELECT @@local_infile")).ValueOrThrow() == 1;
        Assert.Equal(nativeExpected, receipt.Native);
        Assert.Equal(10_000L, (await Db.Query<Product>().Where(p => p.Sku.StartsWith("B-")).CountAsync()).ValueOrThrow());

        var sample = (await Db.Query<Product>().Where(p => p.Sku == "B-004242").FirstOrDefaultAsync()).ValueOrThrow()!;
        var expected = source[4242];
        Assert.Equal((expected.Name, expected.Price, expected.Stock, expected.Active, expected.Status, expected.Ref),
            (sample.Name, sample.Price, sample.Stock, sample.Active, sample.Status, sample.Ref));
        Assert.Equal(expected.Description, sample.Description);
        Assert.NotEqual(default, sample.CreatedAt);                          // audit columns stamped like an insert
        Assert.Equal(1, sample.Version);                                     // concurrency token starts at 1
        Assert.Null(sample.DeletedAt);
    }

    [SkippableFact]
    public async Task Streams_copy_in_chunks_and_transactions_roll_back()
    {
        RequireProvider();
        static async IAsyncEnumerable<Product> Stream()
        {
            foreach (var p in Rows(2_500, "S")) { await Task.Yield(); yield return p; }
        }
        var receipt = (await Db.Bulk<Product>().InsertAsync(Stream(), new BulkOptions { ChunkSize = 1_000 })).ValueOrThrow();
        Assert.Equal(2_500, receipt.Rows);

        var tx = (await Db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx)
        {
            Assert.Equal(300, (await tx.Db.Bulk<Product>().InsertAsync(Rows(300, "T"))).ValueOrThrow().Rows);
            Assert.Equal(300L, (await tx.Db.Query<Product>().Where(p => p.Sku.StartsWith("T-")).CountAsync()).ValueOrThrow());
        }   // disposed without commit: rolled back
        Assert.Equal(0L, (await Db.Query<Product>().Where(p => p.Sku.StartsWith("T-")).CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Bulk_copy_drops_cached_results_of_the_table()
    {
        RequireProvider();
        await using var db = Copy(cache: new DataCache());
        Assert.Equal(0L, (await db.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
        (await db.Bulk<Product>().InsertAsync(Rows(50, "C"))).ValueOrThrow();
        Assert.Equal(50L, (await db.Query<Product>().WithCache().CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task InsertMany_switches_to_bulk_only_when_nothing_is_read_back()
    {
        RequireProvider();
        await using var db = Copy(c => c.BulkThreshold = 100);

        var generated = Rows(150, "G").ToList();                             // identities come from the server: no bulk
        Assert.Equal(150, (await db.Repository<Product>().InsertManyAsync(generated)).ValueOrThrow());
        Assert.All(generated, p => Assert.True(p.Id > 0));
        Assert.Equal(150, generated.Select(p => p.Id).Distinct().Count());

        var start = generated.Max(p => p.Id) + 1000;
        var explicitIds = Rows(150, "E", i => start + i).ToList();           // ids already known: bulk path
        Assert.Equal(150, (await db.Repository<Product>().InsertManyAsync(explicitIds)).ValueOrThrow());
        Assert.Equal("E-000007", (await db.Repository<Product>().GetByIdAsync(start + 7)).ValueOrThrow()!.Sku);
    }

    [SkippableFact]
    public async Task Without_a_native_path_rows_are_batched()
    {
        RequireProvider();
        Skip.IfNot(Db.Provider.Name == "mysql", "Only MySQL has a switchable native path (LOAD DATA LOCAL INFILE).");
        await using var db = Copy(c => c.MySql = new MySqlOptions { AllowLoadLocalInfile = false });
        var receipt = (await db.Bulk<Product>().InsertAsync(Rows(1_200, "F"))).ValueOrThrow();
        Assert.Equal((1_200L, false), (receipt.Rows, receipt.Native));
        Assert.Equal(1_200L, (await db.Query<Product>().Where(p => p.Sku.StartsWith("F-")).CountAsync()).ValueOrThrow());
    }
}
