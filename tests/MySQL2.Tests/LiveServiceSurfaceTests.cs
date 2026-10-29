using CL.MySQL2;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using Xunit;
using Xunit.Abstractions;

namespace MySQL2.Tests;

// The remaining service-level surface: SqlFn (none of the seventeen had executed), the
// upsert family, backup/restore, migration tracking, schema state, pools, retention and
// the advisory lock.

[Table(Name = "it_fn")]
public sealed class FnRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "at", DataType = DataType.DateTime, NotNull = true)] public DateTime At { get; set; }
    [Column(Name = "word", Size = 40, NotNull = true)] public string Word { get; set; } = "";
    [Column(Name = "other", Size = 40)] public string? Other { get; set; }
    [Column(Name = "amount", DataType = DataType.Double, NotNull = true)] public double Amount { get; set; }
}

[Table(Name = "it_svc")]
public sealed class SvcRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "sku", Size = 40, Unique = true, NotNull = true)] public string Sku { get; set; } = "";
    [Column(Name = "qty", NotNull = true)] public int Qty { get; set; }
    [Column(Name = "label", Size = 40)] public string? Label { get; set; }
}

[Table(Name = "it_svc_retain")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class SvcRetain
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)] public DateTime CreatedUtc { get; set; }
}

[Collection("codelogic")]
public sealed class LiveServiceSurfaceTests
{
    // 2026-03-14 15:09:26 — a Saturday, so .NET DayOfWeek is 6.
    private static readonly DateTime Sample = new(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);

    private readonly MySQL2RuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private MySQL2Library Mysql => _fx.Mysql;

    public LiveServiceSurfaceTests(MySQL2RuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task SeedFnAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_fn");
        Assert.True((await Mysql.SyncTableAsync<FnRow>(createBackup: false)).IsSuccess);
        await Mysql.GetRepository<FnRow>().InsertAsync(new FnRow
        {
            At = Sample, Word = "MiXeD", Other = null, Amount = 2.345,
        });
    }

    private async Task FreshSvcAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_svc");
        Assert.True((await Mysql.SyncTableAsync<SvcRow>(createBackup: false)).IsSuccess);
    }

    /// <summary>Groups by the expression under test and returns the single key produced.</summary>
    private async Task<TKey> KeyAsync<TKey>(System.Linq.Expressions.Expression<Func<FnRow, TKey>> keySelector)
    {
        var rows = await Mysql.Query<FnRow>()
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
    /// MySQL's DAYOFWEEK is 1-7 starting Sunday, so the translator subtracts one to match
    /// .NET's 0-6. An off-by-one here would silently shift every weekday bucket.
    /// </summary>
    [DbFact]
    public async Task DayOfWeek_matches_the_dotnet_convention()
    {
        await SeedFnAsync();
        Assert.Equal(6, (int)Sample.DayOfWeek);
        Assert.Equal((int)Sample.DayOfWeek, await KeyAsync(r => SqlFn.DayOfWeek(r.At)));
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
        var bucket = await KeyAsync(r => SqlFn.BucketUtc(r.At, 300));
        Assert.Equal(new DateTime(2026, 3, 14, 15, 5, 0), bucket);

        var hourly = await KeyAsync(r => SqlFn.BucketUtc(r.At, 3600));
        Assert.Equal(new DateTime(2026, 3, 14, 15, 0, 0), hourly);
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
        await FreshSvcAsync();
        var repo = Mysql.GetRepository<SvcRow>();
        foreach (var (sku, qty) in new[] { ("a", 1), ("b", 2), ("c", 3) })
            Assert.True((await repo.InsertAsync(new SvcRow { Sku = sku, Qty = qty, Label = "wide " + sku })).IsSuccess);

        var list = await Mysql.Query<SvcRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToListAsync();
        Assert.True(list.IsSuccess, list.Error?.ToString());
        Assert.Equal(["a", "b", "c"], list.Value!.Select(r => r.Sku));
        Assert.Equal([1, 2, 3], list.Value!.Select(r => r.Qty));
        Assert.All(list.Value!, r => Assert.Null(r.Label));

        var first = await Mysql.Query<SvcRow>().Where(r => r.Sku == "b").Exclude(r => r.Label, r => r.Qty).FirstOrDefaultAsync();
        Assert.Equal(("b", 0, (string?)null), (first.Value!.Sku, first.Value.Qty, first.Value.Label));

        var paged = await Mysql.Query<SvcRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToPagedListAsync(1, 2);
        Assert.Equal((3L, 2), (paged.Value!.TotalItems, paged.Value.Items.Count));
        Assert.All(paged.Value.Items, r => Assert.Null(r.Label));

        var cursor = await Mysql.Query<SvcRow>().Exclude(r => r.Label).OrderBy(r => r.Sku).ToCursorPagedListAsync(2);
        Assert.Equal(["a", "b"], cursor.Value!.Items.Select(r => r.Sku));
        Assert.All(cursor.Value.Items, r => Assert.Null(r.Label));

        var updated = await Mysql.Query<SvcRow>().Where(r => r.Sku == "c").Exclude(r => r.Label).UpdateAndGetAsync(r => new SvcRow { Qty = r.Qty + 10 });
        Assert.Equal((13, (string?)null), (updated.Value!.Single().Qty, updated.Value!.Single().Label));

        // Nothing was written: the label is still there for a normal read.
        Assert.Equal("wide c", (await Mysql.Query<SvcRow>().Where(r => r.Sku == "c").FirstOrDefaultAsync()).Value!.Label);
    }

    [Fact]
    public void Exclude_refuses_unknown_or_all_columns()
    {
        Assert.Throws<ArgumentException>(() => Mysql.Query<SvcRow>().Exclude(r => r.Id, r => r.Sku, r => r.Qty, r => r.Label));
        Assert.ThrowsAny<Exception>(() => Mysql.Query<SvcRow>().Exclude(r => r.Sku.Length));
    }

    // ── Upserts ──────────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Upsert_inserts_then_updates_on_the_unique_key()
    {
        await FreshSvcAsync();
        var repo = Mysql.GetRepository<SvcRow>();

        Assert.True((await repo.UpsertAsync(new SvcRow { Sku = "U1", Qty = 1, Label = "first" })).IsSuccess);
        Assert.True((await repo.UpsertAsync(new SvcRow { Sku = "U1", Qty = 7, Label = "second" })).IsSuccess);

        var rows = await repo.GetAllAsync();
        Assert.Single(rows.Value!);
        Assert.Equal("second", rows.Value![0].Label);
        Assert.Equal(7, rows.Value![0].Qty);
    }

    [DbFact]
    public async Task UpsertMany_merges_a_batch()
    {
        await FreshSvcAsync();
        var repo = Mysql.GetRepository<SvcRow>();

        await repo.UpsertManyAsync([
            new SvcRow { Sku = "B1", Qty = 1 },
            new SvcRow { Sku = "B2", Qty = 2 },
        ]);
        await repo.UpsertManyAsync([
            new SvcRow { Sku = "B2", Qty = 22 },
            new SvcRow { Sku = "B3", Qty = 3 },
        ]);

        var rows = await repo.GetAllAsync();
        Assert.Equal(3, rows.Value!.Count);
        Assert.Equal(22, rows.Value!.Single(r => r.Sku == "B2").Qty);
    }

    [DbFact]
    public async Task UpsertWithIncrements_accumulates()
    {
        await FreshSvcAsync();
        var repo = Mysql.GetRepository<SvcRow>();
        var seed = new SvcRow { Sku = "INC", Qty = 5 };

        await repo.UpsertWithIncrementsAsync(seed, [nameof(SvcRow.Qty)]);
        await repo.UpsertWithIncrementsAsync(seed, [nameof(SvcRow.Qty)]);
        await repo.UpsertWithIncrementsAsync(seed, [nameof(SvcRow.Qty)]);

        var rows = await repo.GetAllAsync();
        Assert.Single(rows.Value!);
        Assert.Equal(15, rows.Value![0].Qty);
    }

    // ── Transactions ─────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Transaction_commits_and_rolls_back()
    {
        await FreshSvcAsync();

        await using (var tx = await Mysql.BeginTransactionAsync())
        {
            await Mysql.GetRepository<SvcRow>(tx).InsertAsync(new SvcRow { Sku = "TX1", Qty = 1 });
            // no commit -> disposal rolls back
        }
        Assert.Equal(0, (await Mysql.GetRepository<SvcRow>().CountAsync()).Value);

        await using (var tx = await Mysql.BeginTransactionAsync())
        {
            await Mysql.GetRepository<SvcRow>(tx).InsertAsync(new SvcRow { Sku = "TX2", Qty = 1 });
            await tx.CommitAsync();
        }
        Assert.Equal(1, (await Mysql.GetRepository<SvcRow>().CountAsync()).Value);
    }

    // ── Schema state / migrations ────────────────────────────────────────────────

    [DbFact]
    public async Task SchemaStateStore_round_trips_a_sentinel_row()
    {
        var store = Mysql.SchemaState;
        Assert.True(await store.EnsureStateTableAsync());

        const string key = "it_svc_probe";
        await store.RemoveStateAsync(key);

        Assert.True(await store.UpsertStateAsync(
            key, "deadbeef", SchemaSyncStatus.Synced, "Production", "9.9.9", "model-info"));

        var record = await store.GetStateAsync(key);
        Assert.NotNull(record);
        Assert.Equal("deadbeef", record!.SchemaCrc);

        // Upsert updates in place rather than duplicating.
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
        var tracker = Mysql.TableSync.GetMigrationTracker();
        Assert.True(await tracker.EnsureMigrationsTableAsync());

        const string id = "it-probe-migration";
        await tracker.RemoveMigrationRecordAsync(id);
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));

        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));
        Assert.True(await tracker.HasMigrationBeenAppliedAsync(id));
        // Recording twice must not duplicate.
        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));
        Assert.Single(await tracker.GetAppliedMigrationsAsync(), m => m.MigrationId == id);

        Assert.True(await tracker.RemoveMigrationRecordAsync(id));
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));
    }

    [DbFact]
    public async Task Migration_registration_is_idempotent()
    {
        var runner = Mysql.Migrations;
        // Registering by hand and then scanning the same assembly must not hold two copies:
        // the apply pass filters against a snapshot, so both would run.
        runner.Register(new ProbeMigration());
        runner.RegisterFrom(typeof(LiveServiceSurfaceTests).Assembly);
        runner.Register(new ProbeMigration());

        var pending = await runner.GetPendingAsync();
        Assert.Equal(pending.Select(p => p.MigrationId).Distinct().Count(), pending.Count);
    }

    // ── TableSyncService / backup ────────────────────────────────────────────────

    [DbFact]
    public async Task TableSync_accessors_and_batch_sync()
    {
        var sync = Mysql.TableSync;
        Assert.NotNull(sync.GetBackupManager());
        Assert.NotNull(sync.GetMigrationTracker());
        Assert.NotNull(sync.GetSchemaStateStore());

        await _fx.Exec("DROP TABLE IF EXISTS it_svc");
        var result = await sync.SyncTablesAsync([typeof(SvcRow), typeof(FnRow)]);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));
    }

    [DbFact]
    public async Task Backup_restore_and_cleanup()
    {
        await FreshSvcAsync();
        await Mysql.GetRepository<SvcRow>().InsertAsync(new SvcRow { Sku = "B", Qty = 1 });

        Assert.True((await Mysql.BackupManager.BackupTableSchemaAsync("it_svc")).Value);
        var file = Mysql.BackupManager.GetLatestBackupFile("it_svc");
        Assert.NotNull(file);
        Assert.Contains("CREATE TABLE", await File.ReadAllTextAsync(file!));

        var all = await Mysql.BackupManager.BackupDatabaseSchemaAsync();
        Assert.True(all.IsSuccess, all.Error?.ToString());

        // Restore rebuilds from DDL only, so the rows are gone.
        Assert.True((await Mysql.BackupManager.RestoreTableSchemaAsync("it_svc", file)).Value);
        Assert.Equal(0, (await Mysql.GetRepository<SvcRow>().CountAsync()).Value);

        var cleaned = await Mysql.BackupManager.CleanupOldBackupsAsync(olderThanDays: 0);
        Assert.True(cleaned.IsSuccess, cleaned.Error?.ToString());
        Assert.True(cleaned.Value >= 1);
    }

    [DbFact]
    public async Task GenerateAlterStatements_reports_a_pending_change()
    {
        await FreshSvcAsync();
        await _fx.Exec("ALTER TABLE it_svc DROP COLUMN label");

        var analyzer = new SchemaAnalyzer();
        var statements = await Mysql.ConnectionManager.ExecuteWithConnectionAsync(
            conn => analyzer.GenerateAlterStatementsAsync(typeof(SvcRow), conn, SchemaSyncLevel.Safe));

        _out.WriteLine(string.Join("\n", statements));
        Assert.Contains(statements, s => s.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
                                      && s.Contains("label", StringComparison.OrdinalIgnoreCase));
    }

    // ── Advisory lock ────────────────────────────────────────────────────────────

    [DbFact]
    public async Task Advisory_lock_is_exclusive_then_released()
    {
        var cm = Mysql.ConnectionManager;

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
        await Mysql.GetRepository<SvcRow>().InsertAsync(new SvcRow { Sku = "P", Qty = 1 });

        var name = $"it-pool-{Guid.NewGuid():N}";
        var pool = Mysql.RegisterCachePool(name, TimeSpan.FromMinutes(10));

        var warmed = 0;
        pool.WarmUp(() => { Interlocked.Increment(ref warmed); return Task.CompletedTask; });

        await Mysql.Query<SvcRow>().SmartCache(name).ToListAsync();
        Assert.True(pool.HasEntriesForTable("it_svc"));
        Assert.False(pool.HasEntriesForTable("some_other_table"));

        var stats = pool.GetStats();
        Assert.Equal(name, stats.Name);
        Assert.True(stats.EntryCount > 0);
        Assert.Contains(Mysql.GetCachePoolStats(), p => p.Name == name);

        await Mysql.RefreshCachePoolAsync(name);
        Assert.True(pool.GetStats().TicksFired >= 1);

        await pool.RefreshNowAsync();
    }

    [DbFact]
    public async Task Retention_prunes_rows_past_the_window()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_svc_retain");
        Assert.True((await Mysql.SyncTableAsync<SvcRetain>(createBackup: false)).IsSuccess);

        await Mysql.GetRepository<SvcRetain>().InsertManyAsync([
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-60) },
            new SvcRetain { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        await using var worker = new RetentionWorker(
            Mysql.ConnectionManager, null, [typeof(SvcRetain)]);
        Assert.True(worker.HasWork);
        Assert.Equal(2, await worker.RunOnceAsync());

        var left = await Mysql.GetRepository<SvcRetain>().GetAllAsync();
        Assert.Single(left.Value!);
    }
    // ── Remaining entry points ───────────────────────────────────────────────────

    [DbFact]
    public async Task RestoreSchemaAsync_rebuilds_and_clears_state()
    {
        await FreshSvcAsync();
        await Mysql.GetRepository<SvcRow>().InsertAsync(new SvcRow { Sku = "R", Qty = 1 });
        Assert.True((await Mysql.BackupManager.BackupTableSchemaAsync("it_svc")).Value);

        // The library wrapper also clears the CRC sentinel, unlike BackupManager's own.
        var restored = await Mysql.RestoreSchemaAsync("it_svc");
        Assert.True(restored.IsSuccess, restored.Error?.ToString());
        Assert.Equal(0, (await Mysql.GetRepository<SvcRow>().CountAsync()).Value);
        Assert.Null(await Mysql.SchemaState.GetStateAsync("it_svc"));
    }

    [DbFact]
    public async Task RegisterConfiguration_adds_a_connection_at_runtime()
    {
        var cm = Mysql.ConnectionManager;
        var existing = cm.GetConfiguration()!;

        Assert.False(cm.HasConfiguration("Runtime"));
        cm.RegisterConfiguration(existing, "Runtime");
        Assert.True(cm.HasConfiguration("Runtime"));
        Assert.Contains("Runtime", cm.GetConnectionIds());

        // The new id is usable, not just registered.
        Assert.True(await cm.TestConnectionAsync("Runtime"));
        var rows = await Mysql.Query<SvcRow>("Runtime").ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
    }

    [DbFact]
    public async Task Joined_query_paging()
    {
        await FreshSvcAsync();
        await _fx.Exec("DROP TABLE IF EXISTS it_fn");
        Assert.True((await Mysql.SyncTableAsync<FnRow>(createBackup: false)).IsSuccess);

        await Mysql.GetRepository<SvcRow>().InsertManyAsync(Enumerable.Range(0, 6)
            .Select(i => new SvcRow { Sku = $"S{i}", Qty = 1 }).ToList());
        await Mysql.GetRepository<FnRow>().InsertAsync(new FnRow
        {
            At = Sample, Word = "w", Amount = 1,
        });

        var page = await Mysql.Query<SvcRow>()
            .Join<FnRow, int, JoinRow>(s => s.Qty, f => (int)f.Amount,
                (s, f) => new JoinRow { Sku = s.Sku, Word = f.Word })
            .OrderBy((s, f) => s.Sku)
            .Offset(2).Limit(2)
            .ToListAsync();

        Assert.True(page.IsSuccess, page.Error?.ToString());
        Assert.Equal(2, page.Value!.Count);
        Assert.Equal("S2", page.Value[0].Sku);
    }
}

public sealed class JoinRow
{
    [Column(Name = "sku")] public string Sku { get; set; } = "";
    [Column(Name = "word")] public string Word { get; set; } = "";
}

public sealed class ProbeMigration : Migration
{
    public ProbeMigration() : base("0.0.1", 1, "probe") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) => Task.CompletedTask;
}
