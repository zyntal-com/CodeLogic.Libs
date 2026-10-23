using CL.MySQL2;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using CodeLogic.Core.Logging;
using Xunit;

namespace MySQL2.Tests;

[Table(Name = "it_event_ignore")]
public sealed class EventIgnoreRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "client_event_id", Size = 64, NotNull = true, Unique = true)]
    public string ClientEventId { get; set; } = "";

    [Column(Name = "payload", Size = 200)]
    public string? Payload { get; set; }
}

[Table(Name = "it_composite_drift")]
[CompositeIndex("ix_drift", "col_a", "col_b")]
public sealed class CompositeDriftV1
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "col_a", Size = 50, NotNull = true)]
    public string ColA { get; set; } = "";

    [Column(Name = "col_b", Size = 50, NotNull = true)]
    public string ColB { get; set; } = "";

    [Column(Name = "col_c", Size = 50, NotNull = true)]
    public string ColC { get; set; } = "";
}

[Table(Name = "it_composite_drift")]
[CompositeIndex("ix_drift", "col_a", "col_c")]
public sealed class CompositeDriftV2
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "col_a", Size = 50, NotNull = true)]
    public string ColA { get; set; } = "";

    [Column(Name = "col_b", Size = 50, NotNull = true)]
    public string ColB { get; set; } = "";

    [Column(Name = "col_c", Size = 50, NotNull = true)]
    public string ColC { get; set; } = "";
}

[Table(Name = "it_agg_nullable")]
public sealed class AggNullableRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "created_utc", NotNull = true)]
    public DateTime CreatedUtc { get; set; }

    [Column(Name = "amount", NotNull = true)]
    public int Amount { get; set; }
}

internal sealed class FullCapturingLogger : ILogger
{
    public readonly List<string> Warnings = [];
    public readonly List<string> Errors = [];
    public readonly List<string> Debugs = [];

    public void Trace(string message) { }
    public void Debug(string message) { lock (Debugs) Debugs.Add(message); }
    public void Debug(string message, params object?[] args) { lock (Debugs) Debugs.Add(string.Format(message, args)); }
    public void Info(string message) { }
    public void Info(string message, params object?[] args) { }
    public void Warning(string message) { lock (Warnings) Warnings.Add(message); }
    public void Warning(string message, params object?[] args) { lock (Warnings) Warnings.Add(string.Format(message, args)); }
    public void Error(string message, Exception? ex = null) { lock (Errors) Errors.Add(message); }
    public void Error(string message, params object?[] args) { lock (Errors) Errors.Add(string.Format(message, args)); }
    public void Critical(string message, Exception? ex = null) { lock (Errors) Errors.Add(message); }
}

[Collection("codelogic")]
public sealed class InsertIgnoreAndAggregateNullablesTests
{
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public InsertIgnoreAndAggregateNullablesTests(MySQL2RuntimeFixture fx) => _fx = fx;

    [DbFact]
    public async Task InsertIgnoreAsync_Inserts_With_Id_And_Duplicates_Return_Duplicate_Without_Exception()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<EventIgnoreRow>();

        var evt1 = new EventIgnoreRow { ClientEventId = "evt-100", Payload = "original" };
        var res1 = await repo.InsertIgnoreAsync(evt1);

        Assert.True(res1.IsSuccess);
        Assert.Equal(InsertOutcome.Inserted, res1.Value);
        Assert.True(evt1.Id > 0, "Generated Id should be set on the inserted entity");

        // Same unique key again
        var evtDup = new EventIgnoreRow { ClientEventId = "evt-100", Payload = "duplicate-attempt" };
        var resDup = await repo.InsertIgnoreAsync(evtDup);

        Assert.True(resDup.IsSuccess);
        Assert.Equal(InsertOutcome.Duplicate, resDup.Value);
        Assert.Equal(0, evtDup.Id); // Id should not be overwritten

        // Ensure database row was not overwritten
        var loaded = (await Mysql.Query<EventIgnoreRow>().Where(r => r.ClientEventId == "evt-100").FirstOrDefaultAsync()).Value;
        Assert.NotNull(loaded);
        Assert.Equal("original", loaded!.Payload);
    }

    [DbFact]
    public async Task InsertIgnoreAsync_Does_Not_Swallow_Data_Truncation_Error()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<EventIgnoreRow>();

        // client_event_id has Size = 64; provide a string longer than 64 characters
        var longKey = new string('x', 100);
        var evtTrunc = new EventIgnoreRow { ClientEventId = longKey, Payload = "data" };

        var res = await repo.InsertIgnoreAsync(evtTrunc);
        Assert.False(res.IsSuccess, "Data truncation should not be silently swallowed by InsertIgnoreAsync");
        Assert.Equal("mysql.insert_failed", res.Error?.Code);

        // INSERT IGNORE writes the truncated row before the warning is seen; the call must
        // roll it back so a failure Result means nothing was written.
        Assert.Equal(0, (await Mysql.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [DbFact]
    public async Task InsertManyIgnoreAsync_Truncation_Rolls_Back_The_Whole_Call()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<EventIgnoreRow>();
        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "ok-1", Payload = "a" },
            new EventIgnoreRow { ClientEventId = "ok-2", Payload = "b" },
            new EventIgnoreRow { ClientEventId = new string('y', 100), Payload = "too long key" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.False(res.IsSuccess);
        Assert.Equal("mysql.bulk_insert_failed", res.Error?.Code);
        Assert.Equal(0, (await Mysql.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [DbFact]
    public async Task InsertIgnore_Inside_Transaction_Rolls_Back_Only_Its_Own_Write()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        await using (var tx = await Mysql.BeginTransactionAsync())
        {
            var txRepo = Mysql.GetRepository<EventIgnoreRow>(tx);
            Assert.True((await txRepo.InsertAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "kept" })).IsSuccess);

            var dup = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "dup" });
            Assert.True(dup.IsSuccess);
            Assert.Equal(InsertOutcome.Duplicate, dup.Value);

            var bad = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = new string('z', 100) });
            Assert.False(bad.IsSuccess);

            var added = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "later", Payload = "kept" });
            Assert.Equal(InsertOutcome.Inserted, added.Value);
            await tx.CommitAsync();
        }

        // The failed call rolled back to its savepoint; the caller's earlier and later work survived.
        var ids = (await Mysql.Query<EventIgnoreRow>().OrderBy(r => r.Id).ToListAsync()).Value!
            .Select(r => r.ClientEventId).ToArray();
        Assert.Equal(new[] { "earlier", "later" }, ids);
    }

    [DbFact]
    public async Task InsertManyIgnoreAsync_With_Mix_Returns_Only_Inserted_Count()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<EventIgnoreRow>();
        await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-1", Payload = "existing" });

        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "evt-1", Payload = "dup" },
            new EventIgnoreRow { ClientEventId = "evt-2", Payload = "new-2" },
            new EventIgnoreRow { ClientEventId = "evt-3", Payload = "new-3" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Value); // 2 inserted, 1 ignored duplicate

        var count = (await Mysql.Query<EventIgnoreRow>().CountAsync()).Value;
        Assert.Equal(3, count);

        // Re-inserting the same batch returns 0 inserted
        var res2 = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(res2.IsSuccess);
        Assert.Equal(0, res2.Value);
    }

    [DbFact]
    public async Task InsertAsync_DuplicateKey_OptIn_Returns_DuplicateKey_And_Logs_Warning()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_event_ignore");
        Assert.True((await Mysql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);

        var loggerOff = new FullCapturingLogger();
        var repoOff = new Repository<EventIgnoreRow>(
            Mysql.ConnectionManager, loggerOff, "Default", 1000, 500, distinguishDuplicateKeyError: false);

        await repoOff.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup-test", Payload = "1" });

        // Duplicate with option OFF -> mysql.insert_failed and Error log
        var resOff = await repoOff.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup-test", Payload = "2" });
        Assert.False(resOff.IsSuccess);
        Assert.Equal("mysql.insert_failed", resOff.Error?.Code);
        Assert.Contains(loggerOff.Errors, m => m.Contains("InsertAsync failed"));
        Assert.Empty(loggerOff.Warnings);

        // Duplicate with option ON -> mysql.duplicate_key and Warning log
        var loggerOn = new FullCapturingLogger();
        var repoOn = new Repository<EventIgnoreRow>(
            Mysql.ConnectionManager, loggerOn, "Default", 1000, 500, distinguishDuplicateKeyError: true);

        var resOn = await repoOn.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup-test", Payload = "3" });
        Assert.False(resOn.IsSuccess);
        Assert.Equal("mysql.duplicate_key", resOn.Error?.Code);
        Assert.Contains(loggerOn.Warnings, m => m.Contains("InsertAsync duplicate key"));
        Assert.Empty(loggerOn.Errors);

        // InsertManyAsync with option ON -> mysql.duplicate_key and Warning log
        var batchDup = new[]
        {
            new EventIgnoreRow { ClientEventId = "evt-dup-test", Payload = "4" }
        };
        var resManyOn = await repoOn.InsertManyAsync(batchDup);
        Assert.False(resManyOn.IsSuccess);
        Assert.Equal("mysql.duplicate_key", resManyOn.Error?.Code);
        Assert.Contains(loggerOn.Warnings, m => m.Contains("InsertManyAsync duplicate key"));
    }

    [DbFact]
    public async Task SchemaSync_Logs_Warning_On_CompositeIndex_Column_Drift_And_No_Drop_At_Safe_Level()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_composite_drift");
        Assert.True((await Mysql.SyncTableAsync<CompositeDriftV1>(createBackup: false)).IsSuccess);

        var logger = new FullCapturingLogger();
        var analyzer = new SchemaAnalyzer(logger);

        await Mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            var statements = await analyzer.GenerateAlterStatementsAsync(
                typeof(CompositeDriftV2), conn, SchemaSyncLevel.Safe);

            // No DROP statements issued at Safe level
            Assert.DoesNotContain(statements, s => s.Contains("DROP", StringComparison.OrdinalIgnoreCase));

            // Warning logged with table, index name, existing columns, and model columns
            Assert.Contains(logger.Warnings, w =>
                w.Contains("it_composite_drift") &&
                w.Contains("ix_drift") &&
                w.Contains("col_a, col_b") &&
                w.Contains("col_a, col_c"));

            return true;
        }, "Default");
    }

    [DbFact]
    public async Task MaxAsync_MinAsync_SumAsync_Nullable_And_NonNullable_Behavior()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_agg_nullable");
        Assert.True((await Mysql.SyncTableAsync<AggNullableRow>(createBackup: false)).IsSuccess);

        var q = Mysql.Query<AggNullableRow>();

        // 1. Empty set: nullable returns null, non-nullable returns default(T)
        var maxNullableEmpty = await q.MaxAsync(r => (DateTime?)r.CreatedUtc);
        Assert.True(maxNullableEmpty.IsSuccess);
        Assert.Null(maxNullableEmpty.Value);

        var minNullableEmpty = await q.MinAsync(r => (DateTime?)r.CreatedUtc);
        Assert.True(minNullableEmpty.IsSuccess);
        Assert.Null(minNullableEmpty.Value);

        var sumNullableEmpty = await q.SumAsync(r => (int?)r.Amount);
        Assert.True(sumNullableEmpty.IsSuccess);
        Assert.Null(sumNullableEmpty.Value);

        // Non-nullable on empty set stays unchanged (default(DateTime) = DateTime.MinValue, default(int) = 0)
        var maxNonNullableEmpty = await q.MaxAsync(r => r.CreatedUtc);
        Assert.True(maxNonNullableEmpty.IsSuccess);
        Assert.Equal(default, maxNonNullableEmpty.Value);

        var sumNonNullableEmpty = await q.SumAsync(r => r.Amount);
        Assert.True(sumNonNullableEmpty.IsSuccess);
        Assert.Equal(0, sumNonNullableEmpty.Value);

        // 2. Populate rows and test aggregates
        var repo = Mysql.GetRepository<AggNullableRow>();
        var t1 = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 6, 20, 18, 30, 0, DateTimeKind.Utc);
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t1, Amount = 10 });
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t2, Amount = 25 });

        var maxNullable = await q.MaxAsync(r => (DateTime?)r.CreatedUtc);
        Assert.True(maxNullable.IsSuccess);
        Assert.Equal(t2, maxNullable.Value);

        var minNullable = await q.MinAsync(r => (DateTime?)r.CreatedUtc);
        Assert.True(minNullable.IsSuccess);
        Assert.Equal(t1, minNullable.Value);

        var sumNullable = await q.SumAsync(r => (int?)r.Amount);
        Assert.True(sumNullable.IsSuccess);
        Assert.Equal(35, sumNullable.Value);

        // Non-nullable on populated rows
        var maxNonNullable = await q.MaxAsync(r => r.CreatedUtc);
        Assert.True(maxNonNullable.IsSuccess);
        Assert.Equal(t2, maxNonNullable.Value);

        var sumNonNullable = await q.SumAsync(r => r.Amount);
        Assert.True(sumNonNullable.IsSuccess);
        Assert.Equal(35, sumNonNullable.Value);
    }
}
