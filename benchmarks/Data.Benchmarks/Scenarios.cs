// Scenarios beyond the one-call-at-a-time MySqlBenchmarks: throughput under concurrency, a read-mostly workload on the
// entity cache, and PostgreSQL (CL_DATA_PG_CS). They use only public API, so the same code measures before and after a
// change.
//
//   dotnet run -c Release --project benchmarks/Data.Benchmarks -- --filter *Concurrency* *CacheMix* *PostgreSql*
using BenchmarkDotNet.Attributes;
using CL.Data;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.MySql;
using CL.Data.PostgreSql;
using CodeLogic.Core.Results;
using Dapper;
using MySqlConnector;
using Npgsql;

namespace Data.Benchmarks;

/// <summary>Creates and fills <c>bench_items</c> with 1,000 rows; returns their ids.</summary>
internal static class BenchSeed
{
    public static async Task<long[]> SeedAsync(Database db)
    {
        (await db.Schema.SyncAsync<BenchItem>(new CL.Data.Schema.SchemaSyncOptions { Force = true })).ValueOrThrow();
        (await db.Admin.TruncateAsync<BenchItem>()).ThrowIfFailure();
        var rows = Enumerable.Range(1, 1000)
            .Select(i => new BenchItem { Sku = $"SKU-{i}", Name = $"Item {i}", Price = i % 200, Stock = i, CreatedAt = DateTime.UtcNow })
            .ToList();
        (await db.Repository<BenchItem>().InsertManyAsync(rows)).ValueOrThrow();
        return [.. rows.Select(r => r.Id)];
    }

    public static string MySqlConnectionString() =>
        Environment.GetEnvironmentVariable("CL_DATA_MYSQL_CS") ?? throw new InvalidOperationException("Set CL_DATA_MYSQL_CS.");
}

/// <summary>
/// 32 workers at once, each doing 25 operations: throughput and allocations under load, where garbage collection and pool
/// contention show up. The time is per operation (OperationsPerInvoke).
/// </summary>
[MemoryDiagnoser]
public class ConcurrencyBenchmarks
{
    private const int Workers = 32;
    private const int PerWorker = 25;
    private const int Operations = Workers * PerWorker;

    private Database _db = null!;
    private MySqlDataSource _source = null!;
    private long[] _ids = [];

    [GlobalSetup]
    public async Task Setup()
    {
        var cs = BenchSeed.MySqlConnectionString();
        _db = Database.Create("bench-concurrency", MySqlDataProvider.Instance, new DataConnectionConfig
        {
            Provider = "mysql", ConnectionString = cs, MaxPoolSize = 64,
        }).ValueOrThrow();
        _source = new MySqlDataSource(new MySqlConnectionStringBuilder(cs) { ConnectionReset = false, MaximumPoolSize = 64 }.ConnectionString);
        _ids = await BenchSeed.SeedAsync(_db);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _source.DisposeAsync();
    }

    private Task RunAsync(Func<long, Task> operation) =>
        Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < PerWorker; i++) await operation(_ids[(worker * PerWorker + i) % _ids.Length]);
        })));

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations), BenchmarkCategory("Parallel32")]
    public Task Dapper_GetById_Parallel() => RunAsync(async id =>
    {
        await using var c = await _source.OpenConnectionAsync();
        await c.QueryFirstOrDefaultAsync<BenchItem>("SELECT Id, Sku, Name, Price, Stock, CreatedAt FROM bench_items WHERE Id = @id", new { id });
    });

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("Parallel32")]
    public Task CLData_GetById_Parallel() => RunAsync(async id => await _db.Repository<BenchItem>().GetByIdAsync(id));

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("Parallel32")]
    public Task CLData_Filter100_Parallel() => RunAsync(async id =>
    {
        var min = (decimal)(id % 100);
        await _db.Query<BenchItem>().Where(i => i.Price > min).OrderBy(i => i.Id).Take(100).ToListAsync();
    });

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("Parallel32")]
    public Task CLData_Insert_Parallel() => RunAsync(async id =>
        await _db.Repository<BenchItem>().InsertAsync(new BenchItem { Sku = "P", Name = "parallel", Price = 1, Stock = (int)id, CreatedAt = DateTime.UtcNow }));
}

/// <summary>
/// A read-mostly workload on the entity cache: 95 key lookups and 5 updates per 100 operations, on a hot set of 100 rows.
/// How much the cache helps depends on what an update evicts.
/// </summary>
[MemoryDiagnoser]
public class CacheMixBenchmarks
{
    private const int Operations = 100;
    private Database _db = null!;
    private BenchItem[] _hot = [];
    private int _round;

    [GlobalSetup]
    public async Task Setup()
    {
        _db = Database.Create("bench-cachemix", MySqlDataProvider.Instance,
            new DataConnectionConfig { Provider = "mysql", ConnectionString = BenchSeed.MySqlConnectionString() },
            new DatabaseOptions { Cache = new CL.Data.Caching.DataCache(new DataCacheConfig { EntityCache = true }) }).ValueOrThrow();
        var ids = await BenchSeed.SeedAsync(_db);
        var repo = _db.Repository<BenchItem>();
        List<BenchItem> hot = [];
        foreach (var id in ids.Take(100)) hot.Add((await repo.GetByIdAsync(id)).ValueOrThrow()!);
        _hot = [.. hot];
    }

    [GlobalCleanup]
    public Task Cleanup() => _db.DisposeAsync().AsTask();

    [Benchmark(OperationsPerInvoke = Operations), BenchmarkCategory("CacheMix")]
    public async Task CLData_Mixed95_5()
    {
        var repo = _db.Repository<BenchItem>();
        var round = _round++;
        for (var i = 0; i < Operations; i++)
        {
            var row = _hot[(round * 37 + i * 13) % _hot.Length];
            if (i % 20 == 0)
            {
                row.Stock++;
                (await repo.UpdateAsync(row)).ValueOrThrow();
            }
            else
            {
                (await repo.GetByIdAsync(row.Id)).ValueOrThrow();
            }
        }
    }
}

/// <summary>PostgreSQL key lookups (needs CL_DATA_PG_CS): Npgsql's reset on close against no reset, and CL.Data.</summary>
[MemoryDiagnoser]
public class PostgreSqlBenchmarks
{
    private const string ById = "SELECT \"Id\", \"Sku\", \"Name\", \"Price\", \"Stock\", \"CreatedAt\" FROM bench_items WHERE \"Id\" = @id";
    private Database _db = null!;
    private NpgsqlDataSource _reset = null!;
    private NpgsqlDataSource _noReset = null!;
    private long _id;

    [GlobalSetup]
    public async Task Setup()
    {
        var cs = Environment.GetEnvironmentVariable("CL_DATA_PG_CS") ?? throw new InvalidOperationException("Set CL_DATA_PG_CS.");
        _db = Database.Create("bench-pg", PostgreSqlDataProvider.Instance, new DataConnectionConfig { Provider = "postgresql", ConnectionString = cs })
            .ValueOrThrow();
        _reset = NpgsqlDataSource.Create(cs);
        _noReset = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(cs) { NoResetOnClose = true }.ConnectionString);
        var ids = await BenchSeed.SeedAsync(_db);
        _id = ids[500];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _reset.DisposeAsync();
        await _noReset.DisposeAsync();
    }

    private static async Task<BenchItem?> ReadAsync(NpgsqlDataSource source, long id)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(ById, c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? new BenchItem { Id = r.GetInt64(0), Sku = r.GetString(1), Name = r.GetString(2), Price = r.GetDecimal(3), Stock = r.GetInt32(4), CreatedAt = r.GetDateTime(5) }
            : null;
    }

    [Benchmark(Baseline = true), BenchmarkCategory("PgGetById")]
    public Task<BenchItem?> Ado_GetById_ResetOnClose() => ReadAsync(_reset, _id);

    [Benchmark, BenchmarkCategory("PgGetById")]
    public Task<BenchItem?> Ado_GetById_NoResetOnClose() => ReadAsync(_noReset, _id);

    [Benchmark, BenchmarkCategory("PgGetById")]
    public async Task<BenchItem?> CLData_GetById() => (await _db.Repository<BenchItem>().GetByIdAsync(_id)).Value;
}

/// <summary>
/// SQLite on a local file: no network, so a call costs microseconds and CL.Data's own work (and what it does when a pooled
/// connection is opened) is most of it. Raw ADO.NET with the same pooled connection string is the floor.
/// </summary>
[CategoriesColumn]
public class SqliteBenchmarks
{
    private string _path = null!;
    private string _cs = null!;
    private Database _db = null!;
    private long _id;

    [GlobalSetup]
    public async Task Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"cl-data-bench-{Guid.NewGuid():N}.db");
        _db = Database.Create("bench-sqlite", CL.Data.Sqlite.SqliteDataProvider.Instance, new DataConnectionConfig { Provider = "sqlite", Database = _path })
            .ValueOrThrow();
        _cs = _db.Primary.DataSource.ConnectionString;
        var ids = await BenchSeed.SeedAsync(_db);
        _id = ids[500];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SqliteGetById")]
    public async Task<BenchItem?> Ado_GetById()
    {
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection(_cs);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT \"Id\", \"Sku\", \"Name\", \"Price\", \"Stock\", \"CreatedAt\" FROM \"bench_items\" WHERE \"Id\" = @id";
        cmd.Parameters.AddWithValue("@id", _id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? new BenchItem { Id = r.GetInt64(0), Sku = r.GetString(1), Name = r.GetString(2), Price = r.GetDecimal(3), Stock = r.GetInt32(4), CreatedAt = r.GetDateTime(5) }
            : null;
    }

    [Benchmark, BenchmarkCategory("SqliteGetById")]
    public async Task<BenchItem?> CLData_GetById() => (await _db.Repository<BenchItem>().GetByIdAsync(_id)).Value;

    [Benchmark, BenchmarkCategory("SqliteGetById")]
    public async Task<int> CLData_Filter100()
    {
        var min = 10m;
        return (await _db.Query<BenchItem>().Where(i => i.Price > min).OrderBy(i => i.Id).Take(100).ToListAsync()).Value!.Count;
    }

    [Benchmark, BenchmarkCategory("SqliteGetById")]
    public async Task<long> CLData_Insert() =>
        (await _db.Repository<BenchItem>().InsertAsync(new BenchItem { Sku = "N-1", Name = "new", Price = 1, Stock = 1, CreatedAt = DateTime.UtcNow })).Value!.Id;
}
