using CL.MSSQL;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Xunit;
using Xunit.Abstractions;

namespace MSSQL.Tests;

// The remaining service-level surface: SqlFn (none of the seventeen had executed), schema
// state, migration tracking, pools, retention and the application lock.

[Table(Name = "it_fn", Schema = "dbo")]
public sealed class FnRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "at", DataType = DataType.DateTime2, NotNull = true)]
    public DateTime At { get; set; }

    [Column(Name = "word", DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Word { get; set; } = "";

    [Column(Name = "other", DataType = DataType.NVarChar, Size = 40)]
    public string? Other { get; set; }

    [Column(Name = "amount", DataType = DataType.Float, NotNull = true)]
    public double Amount { get; set; }
}

[Table(Name = "it_svc", Schema = "dbo")]
public sealed class SvcRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "sku", DataType = DataType.NVarChar, Size = 40, Unique = true, NotNull = true)]
    public string Sku { get; set; } = "";

    [Column(Name = "qty", DataType = DataType.Int, NotNull = true)]
    public int Qty { get; set; }
}

[Table(Name = "it_excl", Schema = "dbo")]
public sealed class ExclRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "sku", DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Sku { get; set; } = "";

    [Column(Name = "qty", DataType = DataType.Int, NotNull = true)]
    public int Qty { get; set; }

    [Column(Name = "label", DataType = DataType.NVarChar, Size = 400)]
    public string? Label { get; set; }
}

[Table(Name = "it_svc_retain", Schema = "dbo")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class SvcRetain
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "created_utc", DataType = DataType.DateTime2, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}

[Collection("codelogic")]
public sealed class LiveServiceSurfaceTests
{
    // 2026-03-14 15:09:26 — a Saturday, so .NET DayOfWeek is 6.
    private static readonly DateTime Sample = new(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    private readonly MSSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public LiveServiceSurfaceTests(MSSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task SeedFnAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fn]");
        Assert.True((await Mssql.SyncTableAsync<FnRow>(createBackup: false)).IsSuccess);
        await Mssql.GetRepository<FnRow>().InsertAsync(new FnRow
        {
            At = Sample, Word = "MiXeD", Other = null, Amount = 2.345,
        });
    }

    private async Task FreshSvcAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_svc]");
        Assert.True((await Mssql.SyncTableAsync<SvcRow>(createBackup: false)).IsSuccess);
    }

    private async Task<TKey> KeyAsync<TKey>(System.Linq.Expressions.Expression<Func<FnRow, TKey>> keySelector)
    {
        var rows = await Mssql.Query<FnRow>()
            .GroupBy(keySelector)
            .Select(g => new { K = g.Key })
            .ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);
        return rows.Value![0].K;
    }

    // ── SqlFn ────────────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Date_part_functions_extract_the_right_components()
    {
        await SeedFnAsync();
        Assert.Equal(2026, await KeyAsync(r => SqlFn.Year(r.At)));
        Assert.Equal(3, await KeyAsync(r => SqlFn.Month(r.At)));
        Assert.Equal(14, await KeyAsync(r => SqlFn.Day(r.At)));
        Assert.Equal(15, await KeyAsync(r => SqlFn.Hour(r.At)));
        Assert.Equal(9, await KeyAsync(r => SqlFn.Minute(r.At)));
    }

    /// <summary>
    /// The translation deliberately avoids DATEPART(weekday), whose result depends on the
    /// session's SET DATEFIRST, and counts days from a known Sunday instead. This pins that
    /// it matches .NET's 0-6-from-Sunday convention.
    /// </summary>
    [DbFact]
    public async Task DayOfWeek_matches_the_dotnet_convention_regardless_of_datefirst()
    {
        await SeedFnAsync();
        Assert.Equal(6, (int)Sample.DayOfWeek);
        Assert.Equal((int)Sample.DayOfWeek, await KeyAsync(r => SqlFn.DayOfWeek(r.At)));

        // Change DATEFIRST for this session; the answer must not move.
        await Mssql.ExecuteSqlAsync("SET DATEFIRST 3");
        Assert.Equal((int)Sample.DayOfWeek, await KeyAsync(r => SqlFn.DayOfWeek(r.At)));
        await Mssql.ExecuteSqlAsync("SET DATEFIRST 7");
    }

    [DbFact]
    public async Task Date_truncates_the_time_component()
    {
        await SeedFnAsync();
        var day = await KeyAsync(r => SqlFn.Date(r.At));
        Assert.Equal(new DateTime(2026, 3, 14), day.Date);
        Assert.Equal(TimeSpan.Zero, day.TimeOfDay);
    }

    [DbFact]
    public async Task BucketUtc_rounds_down_to_the_window()
    {
        await SeedFnAsync();
        Assert.Equal(new DateTime(2026, 3, 14, 15, 5, 0), await KeyAsync(r => SqlFn.BucketUtc(r.At, 300)));
        Assert.Equal(new DateTime(2026, 3, 14, 15, 0, 0), await KeyAsync(r => SqlFn.BucketUtc(r.At, 3600)));
    }

    [DbFact]
    public async Task IfNull_and_Coalesce_substitute_for_null()
    {
        await SeedFnAsync();
        Assert.Equal("fallback", await KeyAsync(r => SqlFn.IfNull(r.Other, "fallback")));
        Assert.Equal("MiXeD", await KeyAsync(r => SqlFn.IfNull(r.Word, "unused")));
        Assert.Equal("MiXeD", await KeyAsync(r => SqlFn.Coalesce(r.Other, r.Word, "last")));
    }

    [DbFact]
    public async Task String_functions_behave()
    {
        await SeedFnAsync();
        Assert.Equal("mixed", await KeyAsync(r => SqlFn.Lower(r.Word)));
        Assert.Equal("MIXED", await KeyAsync(r => SqlFn.Upper(r.Word)));
        Assert.Equal("MiXeD!", await KeyAsync(r => SqlFn.Concat(r.Word, "!")));
    }

    [DbFact]
    public async Task Like_honours_an_explicit_pattern()
    {
        await SeedFnAsync();
        Assert.True(await KeyAsync(r => SqlFn.Like(r.Word, "Mi%")));
        Assert.False(await KeyAsync(r => SqlFn.Like(r.Word, "zz%")));
    }

    [DbFact]
    public async Task Rounding_functions_behave()
    {
        await SeedFnAsync();   // amount = 2.345
        Assert.Equal(2.0, await KeyAsync(r => SqlFn.Floor(r.Amount)));
        Assert.Equal(3.0, await KeyAsync(r => SqlFn.Ceiling(r.Amount)));
        Assert.Equal(2.35, await KeyAsync(r => SqlFn.Round(r.Amount, 2)), 3);
    }

    [Fact]
    public void Calling_a_SqlFn_outside_a_query_throws_a_clear_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqlFn.Lower("x"));
        Assert.Contains("Lower", ex.Message, StringComparison.Ordinal);
    }

    // ── Exclude ──────────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Exclude_leaves_columns_out_of_every_entity_read()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_excl]");
        Assert.True((await Mssql.SyncTableAsync<ExclRow>(createBackup: false)).IsSuccess);
        var repo = Mssql.GetRepository<ExclRow>();
        foreach (var (sku, qty) in new[] { ("a", 1), ("b", 2), ("c", 3) })
            Assert.True((await repo.InsertAsync(new ExclRow { Sku = sku, Qty = qty, Label = "wide " + sku })).IsSuccess);

        var list = await Mssql.Query<ExclRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToListAsync();
        Assert.True(list.IsSuccess, list.Error?.ToString());
        Assert.Equal(["a", "b", "c"], list.Value!.Select(r => r.Sku));
        Assert.Equal([1, 2, 3], list.Value!.Select(r => r.Qty));
        Assert.All(list.Value!, r => Assert.Null(r.Label));

        var first = await Mssql.Query<ExclRow>().Where(r => r.Sku == "b").Exclude(r => r.Label, r => r.Qty).FirstOrDefaultAsync();
        Assert.Equal(("b", 0, (string?)null), (first.Value!.Sku, first.Value.Qty, first.Value.Label));

        var paged = await Mssql.Query<ExclRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToPagedListAsync(1, 2);
        Assert.Equal((3L, 2), (paged.Value!.TotalItems, paged.Value.Items.Count));
        Assert.All(paged.Value.Items, r => Assert.Null(r.Label));

        var cursor = await Mssql.Query<ExclRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToCursorPagedListAsync(2);
        Assert.Equal(["a", "b"], cursor.Value!.Items.Select(r => r.Sku));
        Assert.All(cursor.Value.Items, r => Assert.Null(r.Label));

        var updated = await Mssql.Query<ExclRow>().Where(r => r.Sku == "c").Exclude(r => r.Label).UpdateAndGetAsync(r => new ExclRow { Qty = r.Qty + 10 });
        Assert.Equal((13, (string?)null), (updated.Value!.Single().Qty, updated.Value!.Single().Label));

        // Nothing was written: the label is still there for a normal read.
        Assert.Equal("wide c", (await Mssql.Query<ExclRow>().Where(r => r.Sku == "c").FirstOrDefaultAsync()).Value!.Label);
    }

    [Fact]
    public void Exclude_refuses_unknown_or_all_columns()
    {
        Assert.Throws<ArgumentException>(() => Mssql.Query<ExclRow>().Exclude(r => r.Id, r => r.Sku, r => r.Qty, r => r.Label));
        Assert.ThrowsAny<Exception>(() => Mssql.Query<ExclRow>().Exclude(r => r.Sku.Length));
    }

    // ── Transactions ─────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Transaction_commits_and_rolls_back()
    {
        await FreshSvcAsync();

        await using (var tx = await Mssql.BeginTransactionAsync())
        {
            await Mssql.GetRepository<SvcRow>(tx).InsertAsync(new SvcRow { Sku = "TX1", Qty = 1 });
            // no commit -> disposal rolls back
        }
        Assert.Equal(0, (await Mssql.GetRepository<SvcRow>().CountAsync()).Value);

        await using (var tx = await Mssql.BeginTransactionAsync())
        {
            await Mssql.GetRepository<SvcRow>(tx).InsertAsync(new SvcRow { Sku = "TX2", Qty = 1 });
            await tx.CommitAsync();
        }
        Assert.Equal(1, (await Mssql.GetRepository<SvcRow>().CountAsync()).Value);
    }

    // ── Schema state / migrations ────────────────────────────────────────────────

    [DbFact]
    public async Task SchemaStateStore_round_trips_a_sentinel_row()
    {
        var store = Mssql.SchemaState;
        Assert.True(await store.EnsureStateTableAsync());

        const string key = "dbo.it_svc_probe";
        await store.RemoveStateAsync(key);

        Assert.True(await store.UpsertStateAsync(
            key, "deadbeef", SchemaSyncStatus.Synced, "Production", "9.9.9", "model-info"));

        var record = await store.GetStateAsync(key);
        Assert.NotNull(record);
        Assert.Equal("deadbeef", record!.SchemaCrc);

        Assert.True(await store.UpsertStateAsync(
            key, "cafebabe", SchemaSyncStatus.Synced, "Production", "9.9.9", "model-info"));
        Assert.Equal("cafebabe", (await store.GetStateAsync(key))!.SchemaCrc);

        Assert.Contains(await store.GetAllAsync(), r => r.TableName == key);
        Assert.True(await store.RemoveStateAsync(key));
        Assert.Null(await store.GetStateAsync(key));
    }

    [DbFact]
    public async Task MigrationTracker_records_and_removes()
    {
        var tracker = Mssql.TableSync.GetMigrationTracker();
        Assert.True(await tracker.EnsureMigrationsTableAsync());

        const string id = "it-probe-migration";
        await tracker.RemoveMigrationRecordAsync(id);
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));

        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));
        Assert.True(await tracker.HasMigrationBeenAppliedAsync(id));
        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));
        Assert.Single(await tracker.GetAppliedMigrationsAsync(), m => m.MigrationId == id);

        Assert.True(await tracker.RemoveMigrationRecordAsync(id));
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));
    }

    [DbFact]
    public async Task Migration_registration_is_idempotent()
    {
        var runner = Mssql.Migrations;
        // Registering by hand then scanning the same assembly must not hold two copies: the
        // apply pass filters against a snapshot, so both would run.
        runner.Register(new ProbeMigration());
        runner.RegisterFrom(typeof(LiveServiceSurfaceTests).Assembly);
        runner.Register(new ProbeMigration());

        var pending = await runner.GetPendingAsync();
        Assert.Equal(pending.Select(p => p.MigrationId).Distinct().Count(), pending.Count);
    }

    // ── TableSyncService ─────────────────────────────────────────────────────────

    [DbFact]
    public async Task TableSync_accessors_and_batch_sync()
    {
        var sync = Mssql.TableSync;
        Assert.NotNull(sync.GetBackupManager());
        Assert.NotNull(sync.GetMigrationTracker());
        Assert.NotNull(sync.GetSchemaStateStore());

        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_svc]");
        var result = await sync.SyncTablesAsync([typeof(SvcRow), typeof(FnRow)]);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));
    }

    [DbFact]
    public async Task GenerateAlterStatements_reports_a_pending_change()
    {
        await FreshSvcAsync();
        await _fx.Exec("ALTER TABLE [dbo].[it_svc] DROP COLUMN [qty]");

        var analyzer = new SchemaAnalyzer();
        var statements = await Mssql.ConnectionManager.ExecuteWithConnectionAsync(
            conn => analyzer.GenerateAlterStatementsAsync(typeof(SvcRow), conn, SchemaSyncLevel.Safe));

        _out.WriteLine(string.Join("\n", statements));
        Assert.Contains(statements, s => s.Contains("ADD", StringComparison.OrdinalIgnoreCase)
                                      && s.Contains("qty", StringComparison.OrdinalIgnoreCase));
    }

    [DbFact]
    public async Task RestoreSchemaAsync_rebuilds_and_clears_state()
    {
        await FreshSvcAsync();
        await Mssql.GetRepository<SvcRow>().InsertAsync(new SvcRow { Sku = "R", Qty = 1 });
        Assert.True((await Mssql.BackupManager.BackupTableSchemaAsync("dbo.it_svc")).Value);

        var restored = await Mssql.RestoreSchemaAsync("dbo.it_svc");
        Assert.True(restored.IsSuccess, restored.Error?.ToString());
        Assert.Equal(0, (await Mssql.GetRepository<SvcRow>().CountAsync()).Value);
        Assert.Null(await Mssql.SchemaState.GetStateAsync("dbo.it_svc"));
    }

    // ── Application lock ─────────────────────────────────────────────────────────

    [DbFact]
    public async Task Application_lock_is_exclusive_then_released()
    {
        var cm = Mssql.ConnectionManager;

        await using (var first = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 5))
        {
            Assert.True(first.Acquired);
            await using var second = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 1);
            Assert.False(second.Acquired, "a second holder must not get the lock");
        }

        await using var third = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 5);
        Assert.True(third.Acquired, "the lock must be released with the holder's scope");
    }

    // ── Pools and retention ──────────────────────────────────────────────────────

    [DbFact]
    public async Task Pool_warmup_refresh_and_membership()
    {
        await FreshSvcAsync();
        await Mssql.GetRepository<SvcRow>().InsertAsync(new SvcRow { Sku = "P", Qty = 1 });

        var name = $"it-pool-{Guid.NewGuid():N}";
        var pool = Mssql.RegisterCachePool(name, TimeSpan.FromMinutes(10));

        var warmed = 0;
        pool.WarmUp(() => { Interlocked.Increment(ref warmed); return Task.CompletedTask; });

        await Mssql.Query<SvcRow>().SmartCache(name).ToListAsync();
        Assert.True(pool.HasEntriesForTable("it_svc"));
        Assert.False(pool.HasEntriesForTable("some_other_table"));

        var stats = pool.GetStats();
        Assert.Equal(name, stats.Name);
        Assert.True(stats.EntryCount > 0);
        Assert.Contains(Mssql.GetCachePoolStats(), p => p.Name == name);

        await Mssql.RefreshCachePoolAsync(name);
        Assert.True(pool.GetStats().TicksFired >= 1);
        await pool.RefreshNowAsync();
    }

    [DbFact]
    public async Task Retention_prunes_rows_past_the_window()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_svc_retain]");
        Assert.True((await Mssql.SyncTableAsync<SvcRetain>(createBackup: false)).IsSuccess);

        await Mssql.GetRepository<SvcRetain>().InsertManyAsync([
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-60) },
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        await using var worker = new RetentionWorker(
            Mssql.ConnectionManager, null, [typeof(SvcRetain)]);
        Assert.True(worker.HasWork);
        Assert.Equal(2, await worker.RunOnceAsync());

        Assert.Single((await Mssql.GetRepository<SvcRetain>().GetAllAsync()).Value!);
    }

    // ── Health ───────────────────────────────────────────────────────────────────

    [DbFact]
    public async Task HealthCheck_reports_healthy_with_connection_data()
    {
        var status = await Mssql.HealthCheckAsync();
        Assert.NotNull(status);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
        Assert.NotNull(status.Data);
        Assert.True(status.Data!.ContainsKey("totalDatabases"));
    }
}

public sealed class ProbeMigration : Migration
{
    public ProbeMigration() : base("0.0.1", 1, "probe") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) => Task.CompletedTask;
}
