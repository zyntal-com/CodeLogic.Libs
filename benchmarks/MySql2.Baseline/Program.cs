// The 4.x baseline for benchmarks/Data.Benchmarks: the same scenarios on the published CodeLogic.MySQL2 4.9
// (.NET 10, CodeLogic 4), in its own process. Uses the table bench_items_v4.
//
//   set CL_DATA_MYSQL_CS=Server=...;Port=...;Database=...;User ID=...;Password=...
//   dotnet run -c Release --project benchmarks/MySql2.Baseline -- --filter *
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using CL.MySQL2;
using CL.MySQL2.Models;
using CodeLogic;
using MySqlConnector;

var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(3).WithIterationCount(10).WithLaunchCount(1))
    .WithOrderer(new BenchmarkDotNet.Order.DefaultOrderer(BenchmarkDotNet.Order.SummaryOrderPolicy.Declared))
    .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByCategory);
BenchmarkSwitcher.FromAssembly(typeof(MySql2Benchmarks).Assembly).Run(args, config);

/// <summary>The benchmark row (same shape as the 5.x benchmark's).</summary>
[Table(Name = "bench_items_v4")]
public class BenchItem
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(DataType = DataType.VarChar, Size = 40, NotNull = true)] public string Sku { get; set; } = "";
    [Column(DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Name { get; set; } = "";
    [Column(DataType = DataType.Decimal, Precision = 18, Scale = 2, NotNull = true)] public decimal Price { get; set; }
    [Column(DataType = DataType.Int, NotNull = true)] public int Stock { get; set; }
    [Column(DataType = DataType.DateTime, NotNull = true)] public DateTime CreatedAt { get; set; }
}

[CategoriesColumn]
public class MySql2Benchmarks
{
    private MySQL2Library _mysql = null!;
    private long _id;
    private BenchItem _row = null!;

    // CodeLogic 4 initializes once per process; BenchmarkDotNet's in-process runs call GlobalSetup per benchmark.
    private static MySQL2Library? _booted;

    [GlobalSetup]
    public async Task Setup()
    {
        _mysql = _booted ??= await BootAsync();
        await _mysql.SyncTableAsync<BenchItem>();
        await _mysql.ExecuteSqlAsync("TRUNCATE TABLE bench_items_v4");
        var rows = Enumerable.Range(1, 1000).Select(i => new BenchItem { Sku = $"SKU-{i}", Name = $"Item {i}", Price = i % 200, Stock = i, CreatedAt = DateTime.UtcNow }).ToList();
        var inserted = await _mysql.GetRepository<BenchItem>().InsertManyAsync(rows);
        if (!inserted.IsSuccess) throw new InvalidOperationException(inserted.Error?.ToString());
        var all = await _mysql.Query<BenchItem>().OrderBy(i => i.Id).Take(1000).ToListAsync();
        _row = all.Value![500];
        _id = _row.Id;
    }

    private static async Task<MySQL2Library> BootAsync()
    {
        var builder = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CL_DATA_MYSQL_CS") ?? throw new InvalidOperationException("Set CL_DATA_MYSQL_CS."));
        var root = Path.Combine(Path.GetTempPath(), "cl-mysql2-bench-" + Guid.NewGuid().ToString("N")[..8]);
        var init = await CodeLogic.CodeLogic.InitializeAsync(o => { o.FrameworkRootPath = root; o.AppVersion = "1.0.0"; o.HandleShutdownSignals = false; });
        if (!init.Success) throw new InvalidOperationException(init.Message);
        var dir = Path.Combine(root, "Libraries", "CL.MySQL2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.mysql.json"), $$"""
            { "databases": { "Default": { "enabled": true, "host": "{{builder.Server}}", "port": {{builder.Port}}, "database": "{{builder.Database}}",
              "username": "{{builder.UserID}}", "password": "{{builder.Password}}", "syncMode": "developer", "characterSet": "utf8mb4" } } }
            """);
        await Libraries.LoadAsync<MySQL2Library>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();
        return Libraries.Get<MySQL2Library>() ?? throw new InvalidOperationException("MySQL2Library not available.");
    }

    private static BenchItem[] NewRows(int count) =>
        Enumerable.Range(0, count).Select(i => new BenchItem { Sku = $"N-{i}", Name = "new", Price = 1, Stock = i, CreatedAt = DateTime.UtcNow }).ToArray();

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<BenchItem?> MySQL2_GetById() => (await _mysql.GetRepository<BenchItem>().GetByIdAsync(_id)).Value;

    [Benchmark, BenchmarkCategory("Filter100")]
    public async Task<int> MySQL2_Filter100()
    {
        var min = 10m;
        return (await _mysql.Query<BenchItem>().Where(i => i.Price > min).OrderBy(i => i.Id).Take(100).ToListAsync()).Value!.Count;
    }

    [Benchmark, BenchmarkCategory("Insert")]
    public async Task<long> MySQL2_Insert() => (await _mysql.GetRepository<BenchItem>().InsertAsync(NewRows(1)[0])).Value!.Id;

    [Benchmark, BenchmarkCategory("InsertMany100")]
    public async Task<int> MySQL2_InsertMany100() => (await _mysql.GetRepository<BenchItem>().InsertManyAsync(NewRows(100))).Value;

    [Benchmark, BenchmarkCategory("Update")]
    public async Task<BenchItem> MySQL2_Update()
    {
        _row.Stock++;
        return (await _mysql.GetRepository<BenchItem>().UpdateAsync(_row)).Value!;
    }

    [Benchmark, BenchmarkCategory("Count")]
    public async Task<long> MySQL2_Count()
    {
        var min = 10m;
        return (await _mysql.Query<BenchItem>().Where(i => i.Price > min).CountAsync()).Value;
    }
}
