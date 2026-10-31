// CL.Data benchmarks against MySQL: CL.Data (regular and compiled queries), Dapper and raw ADO.NET (the floor).
// The 4.x baseline (CodeLogic.MySQL2 4.9) runs the same scenarios in benchmarks/MySql2.Baseline (its own process).
//
//   set CL_DATA_MYSQL_CS=Server=...;Database=...;User ID=...;Password=...
//   dotnet run -c Release --project benchmarks/Data.Benchmarks -- --filter *
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using CL.Data;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.MySql;
using CL.Data.Query;
using CodeLogic.Core.Results;
using Dapper;
using MySqlConnector;

var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(10).WithLaunchCount(1))
    .WithOrderer(new BenchmarkDotNet.Order.DefaultOrderer(BenchmarkDotNet.Order.SummaryOrderPolicy.Declared))
    .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory);
BenchmarkSwitcher.FromAssembly(typeof(MySqlBenchmarks).Assembly).Run(args, config);

/// <summary>The benchmark row (same shape as the 4.x baseline's).</summary>
[Table(Name = "bench_items")]
public sealed class BenchItem
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 40)] public string Sku { get; set; } = "";
    [Column(Size = 100)] public string Name { get; set; } = "";
    [Column(Precision = 18, Scale = 2)] public decimal Price { get; set; }
    public int Stock { get; set; }
    public DateTime CreatedAt { get; set; }
}

[CategoriesColumn]
public class MySqlBenchmarks
{
    private static readonly CompiledQuery<BenchItem, long> ById = CompiledQuery.For<BenchItem, long>((q, id) => q.Where(i => i.Id == id));
    private static readonly CompiledQuery<BenchItem, decimal> Above = CompiledQuery.For<BenchItem, decimal>((q, min) => q.Where(i => i.Price > min).OrderBy(i => i.Id).Take(100));

    private Database _db = null!;
    private Database _cached = null!;
    private MySqlDataSource _source = null!;
    private long _id;
    private BenchItem _row = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var cs = Environment.GetEnvironmentVariable("CL_DATA_MYSQL_CS") ?? throw new InvalidOperationException("Set CL_DATA_MYSQL_CS.");
        _db = Database.Create("bench", MySqlDataProvider.Instance, new DataConnectionConfig { Provider = "mysql", ConnectionString = cs }).ValueOrThrow();
        _source = new MySqlDataSource(cs);
        _cached = Database.Create("bench-cached", MySqlDataProvider.Instance, new DataConnectionConfig { Provider = "mysql", ConnectionString = cs },
            new DatabaseOptions { Cache = new CL.Data.Caching.DataCache(new DataCacheConfig { EntityCache = true }) }).ValueOrThrow();
        (await _db.Schema.SyncAsync<BenchItem>(new CL.Data.Schema.SchemaSyncOptions { Force = true })).ValueOrThrow();
        (await _db.Admin.TruncateAsync<BenchItem>()).ThrowIfFailure();
        var rows = Enumerable.Range(1, 1000).Select(i => new BenchItem { Sku = $"SKU-{i}", Name = $"Item {i}", Price = i % 200, Stock = i, CreatedAt = DateTime.UtcNow }).ToList();
        (await _db.Repository<BenchItem>().InsertManyAsync(rows)).ValueOrThrow();
        _id = rows[500].Id;
        _row = rows[500];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _cached.DisposeAsync();
        await _source.DisposeAsync();
    }

    private static BenchItem[] NewRows(int count) =>
        Enumerable.Range(0, count).Select(i => new BenchItem { Sku = $"N-{i}", Name = "new", Price = 1, Stock = i, CreatedAt = DateTime.UtcNow }).ToArray();

    // ── Get by key ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("GetById")]
    public async Task<BenchItem?> Ado_GetById()
    {
        await using var c = await _source.OpenConnectionAsync();
        await using var cmd = new MySqlCommand("SELECT Id, Sku, Name, Price, Stock, CreatedAt FROM bench_items WHERE Id = @id", c);
        cmd.Parameters.AddWithValue("@id", _id);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync()
            ? new BenchItem { Id = r.GetInt64(0), Sku = r.GetString(1), Name = r.GetString(2), Price = r.GetDecimal(3), Stock = r.GetInt32(4), CreatedAt = r.GetDateTime(5) }
            : null;
    }

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<BenchItem?> Dapper_GetById()
    {
        await using var c = await _source.OpenConnectionAsync();
        return await c.QueryFirstOrDefaultAsync<BenchItem>("SELECT Id, Sku, Name, Price, Stock, CreatedAt FROM bench_items WHERE Id = @id", new { id = _id });
    }

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<BenchItem?> CLData_GetById() => (await _db.Repository<BenchItem>().GetByIdAsync(_id)).Value;

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<BenchItem?> CLData_Compiled_GetById() => (await ById.FirstOrDefaultAsync(_db, _id)).Value;

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<BenchItem?> CLData_Cached_GetById() => (await _cached.Repository<BenchItem>().GetByIdAsync(_id)).Value;

    // ── Filtered list of 100 ────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Filter100")]
    public async Task<int> Dapper_Filter100()
    {
        await using var c = await _source.OpenConnectionAsync();
        return (await c.QueryAsync<BenchItem>("SELECT Id, Sku, Name, Price, Stock, CreatedAt FROM bench_items WHERE Price > @min ORDER BY Id LIMIT 100", new { min = 10m })).AsList().Count;
    }

    [Benchmark, BenchmarkCategory("Filter100")]
    public async Task<int> CLData_Filter100()
    {
        var min = 10m;
        return (await _db.Query<BenchItem>().Where(i => i.Price > min).OrderBy(i => i.Id).Take(100).ToListAsync()).Value!.Count;
    }

    [Benchmark, BenchmarkCategory("Filter100")]
    public async Task<int> CLData_Compiled_Filter100() => (await Above.ToListAsync(_db, 10m)).Value!.Count;

    [Benchmark, BenchmarkCategory("Filter100")]
    public async Task<int> CLData_Cached_Filter100()
    {
        var min = 10m;
        return (await _cached.Query<BenchItem>().WithCache().Where(i => i.Price > min).OrderBy(i => i.Id).Take(100).ToListAsync()).Value!.Count;
    }

    // ── Insert one ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Insert")]
    public async Task<long> Dapper_Insert()
    {
        await using var c = await _source.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<long>("INSERT INTO bench_items (Sku, Name, Price, Stock, CreatedAt) VALUES (@Sku, @Name, @Price, @Stock, @CreatedAt); SELECT LAST_INSERT_ID();", NewRows(1)[0]);
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> CLData_Insert() => (await _db.Repository<BenchItem>().InsertAsync(NewRows(1)[0])).Value!.Id;

    // ── Insert 100 ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("InsertMany100")]
    public async Task<int> Dapper_InsertMany100()
    {
        await using var c = await _source.OpenConnectionAsync();
        return await c.ExecuteAsync("INSERT INTO bench_items (Sku, Name, Price, Stock, CreatedAt) VALUES (@Sku, @Name, @Price, @Stock, @CreatedAt)", NewRows(100));
    }

    [Benchmark, BenchmarkCategory("InsertMany100")]
    public async Task<int> CLData_InsertMany100() => (await _db.Repository<BenchItem>().InsertManyAsync(NewRows(100))).Value;

    // ── Update one ──────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Update")]
    public async Task<int> Dapper_Update()
    {
        _row.Stock++;
        await using var c = await _source.OpenConnectionAsync();
        return await c.ExecuteAsync("UPDATE bench_items SET Sku = @Sku, Name = @Name, Price = @Price, Stock = @Stock, CreatedAt = @CreatedAt WHERE Id = @Id", _row);
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<BenchItem> CLData_Update()
    {
        _row.Stock++;
        return (await _db.Repository<BenchItem>().UpdateAsync(_row)).Value!;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<BenchItem> CLData_UpdateChanged() => (await _db.Repository<BenchItem>().UpdateAsync(_row, r => r.Stock++)).Value!;

    // ── Count ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Count")]
    public async Task<long> Dapper_Count()
    {
        await using var c = await _source.OpenConnectionAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM bench_items WHERE Price > @min", new { min = 10m });
    }

    [Benchmark, BenchmarkCategory("Count")]
    public async Task<long> CLData_Count()
    {
        var min = 10m;
        return (await _db.Query<BenchItem>().Where(i => i.Price > min).CountAsync()).Value;
    }
}
