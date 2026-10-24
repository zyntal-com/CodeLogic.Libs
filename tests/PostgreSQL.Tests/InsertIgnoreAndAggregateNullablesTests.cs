using CL.PostgreSQL;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

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

[Table(Name = "it_index_drift")]
[CompositeIndex("ix_drift_ab", "a", "b")]
public sealed class IndexDriftRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "a", NotNull = true)] public int A { get; set; }
    [Column(Name = "b", NotNull = true)] public int B { get; set; }
}

// Same table, same index name, columns declared in the opposite order.
[Table(Name = "it_index_drift")]
[CompositeIndex("ix_drift_ab", "b", "a")]
public sealed class IndexDriftRowReordered
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "a", NotNull = true)] public int A { get; set; }
    [Column(Name = "b", NotNull = true)] public int B { get; set; }
}

// Mirrors MySQL2.Tests / MSSQL.Tests InsertIgnoreAndAggregateNullablesTests so all providers keep
// the same observable behaviour for InsertIgnore, the duplicate-key opt-in and nullable aggregates.
// The non-duplicate failure case uses a NOT NULL violation (SQLSTATE 23502).
[Collection("codelogic")]
public sealed class InsertIgnoreAndAggregateNullablesTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public InsertIgnoreAndAggregateNullablesTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task ResetEventsAsync()
    {
        await Lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_event_ignore\" CASCADE");
        Assert.True((await Lib.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertIgnoreAsync_Inserts_With_Id_And_Duplicates_Return_Duplicate_Without_Exception()
    {
        await ResetEventsAsync();
        var repo = Lib.GetRepository<EventIgnoreRow>();

        var first = new EventIgnoreRow { ClientEventId = "evt-100", Payload = "original" };
        var inserted = await repo.InsertIgnoreAsync(first);
        Assert.True(inserted.IsSuccess);
        Assert.Equal(InsertOutcome.Inserted, inserted.Value);
        Assert.True(first.Id > 0, "Generated Id should be set on the inserted entity");

        var dup = new EventIgnoreRow { ClientEventId = "evt-100", Payload = "duplicate-attempt" };
        var duplicate = await repo.InsertIgnoreAsync(dup);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(InsertOutcome.Duplicate, duplicate.Value);
        Assert.Equal(0, dup.Id);

        var loaded = (await Lib.Query<EventIgnoreRow>().Where(r => r.ClientEventId == "evt-100").FirstOrDefaultAsync()).Value;
        Assert.Equal("original", loaded!.Payload);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertIgnoreAsync_Fails_On_Non_Duplicate_Error_And_Writes_Nothing()
    {
        await ResetEventsAsync();
        var repo = Lib.GetRepository<EventIgnoreRow>();

        var res = await repo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = null!, Payload = "data" });
        Assert.False(res.IsSuccess);
        Assert.Equal("postgresql.insert_failed", res.Error?.Code);
        Assert.Equal(0, (await Lib.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertManyIgnoreAsync_With_Mix_Returns_Only_Inserted_Count()
    {
        await ResetEventsAsync();
        var repo = Lib.GetRepository<EventIgnoreRow>();
        await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-1", Payload = "existing" });

        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "evt-1", Payload = "dup" },
            new EventIgnoreRow { ClientEventId = "evt-2", Payload = "new-2" },
            new EventIgnoreRow { ClientEventId = "evt-3", Payload = "new-3" },
            new EventIgnoreRow { ClientEventId = "evt-3", Payload = "dup-within-batch" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Value);
        Assert.Equal(3, (await Lib.Query<EventIgnoreRow>().CountAsync()).Value);

        var again = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(again.IsSuccess);
        Assert.Equal(0, again.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertManyIgnoreAsync_Non_Duplicate_Error_Rolls_Back_The_Whole_Call()
    {
        await ResetEventsAsync();
        var repo = new CL.PostgreSQL.Services.Repository<EventIgnoreRow>(
            Lib.ConnectionManager, logger: null, maxBatchInsertSize: 2);
        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "ok-1", Payload = "a" },
            new EventIgnoreRow { ClientEventId = "ok-2", Payload = "b" },
            new EventIgnoreRow { ClientEventId = null!, Payload = "NOT NULL violation in the second batch" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.False(res.IsSuccess);
        Assert.Equal("postgresql.bulk_insert_failed", res.Error?.Code);
        Assert.Equal(0, (await Lib.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertIgnore_Inside_Transaction_Rolls_Back_Only_Its_Own_Write()
    {
        await ResetEventsAsync();

        await using (var tx = await Lib.BeginTransactionAsync())
        {
            var txRepo = Lib.GetRepository<EventIgnoreRow>(tx);
            Assert.True((await txRepo.InsertAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "kept" })).IsSuccess);

            var dup = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "dup" });
            Assert.Equal(InsertOutcome.Duplicate, dup.Value);

            var bad = await txRepo.InsertManyIgnoreAsync(new[]
            {
                new EventIgnoreRow { ClientEventId = "rolled-back", Payload = "x" },
                new EventIgnoreRow { ClientEventId = null! }
            });
            Assert.False(bad.IsSuccess);

            // PostgreSQL aborts a transaction on any error; the savepoint keeps this one usable.
            var added = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "later", Payload = "kept" });
            Assert.Equal(InsertOutcome.Inserted, added.Value);
            await tx.CommitAsync();
        }

        var ids = (await Lib.Query<EventIgnoreRow>().OrderBy(r => r.Id).ToListAsync()).Value!
            .Select(r => r.ClientEventId).ToArray();
        Assert.Equal(new[] { "earlier", "later" }, ids);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertAsync_DuplicateKey_OptIn_Returns_DuplicateKey()
    {
        await ResetEventsAsync();
        var repo = Lib.GetRepository<EventIgnoreRow>();
        await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });

        var defaultResult = await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });
        Assert.Equal("postgresql.insert_failed", defaultResult.Error?.Code);

        repo.DistinguishDuplicateKeyError = true;
        var single = await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });
        Assert.Equal("postgresql.duplicate_key", single.Error?.Code);
        var many = await repo.InsertManyAsync(new[] { new EventIgnoreRow { ClientEventId = "evt-dup" } });
        Assert.Equal("postgresql.duplicate_key", many.Error?.Code);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task MaxAsync_MinAsync_SumAsync_Nullable_And_NonNullable_Behavior()
    {
        await Lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_agg_nullable\" CASCADE");
        Assert.True((await Lib.SyncTableAsync<AggNullableRow>(createBackup: false)).IsSuccess);

        Assert.Null((await Lib.Query<AggNullableRow>().MaxAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Null((await Lib.Query<AggNullableRow>().SumAsync(r => (int?)r.Amount)).Value);
        Assert.Equal(DateTime.MinValue, (await Lib.Query<AggNullableRow>().MaxAsync(r => r.CreatedUtc)).Value);

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddDays(3);
        var repo = Lib.GetRepository<AggNullableRow>();
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t1, Amount = 5 });
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t2, Amount = 7 });

        Assert.Equal(t2, (await Lib.Query<AggNullableRow>().MaxAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Equal(t1, (await Lib.Query<AggNullableRow>().MinAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Equal(12, (await Lib.Query<AggNullableRow>().SumAsync(r => (int?)r.Amount)).Value);
        Assert.Equal(12, (await Lib.Query<AggNullableRow>().SumAsync(r => r.Amount)).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Composite_Index_Drift_Is_Reported_Not_Dropped_At_Safe_Level()
    {
        await Lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_index_drift\" CASCADE");
        Assert.True((await Lib.SyncTableAsync<IndexDriftRow>(createBackup: false)).IsSuccess);

        var analyzer = new SchemaAnalyzer();
        var stmts = await Lib.ConnectionManager.ExecuteWithConnectionAsync(
            conn => analyzer.GenerateAlterStatementsAsync(typeof(IndexDriftRowReordered), conn, SchemaSyncLevel.Safe),
            "Default");

        Assert.DoesNotContain(stmts, s => s.Contains("DROP", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stmts, s => s.Contains("ix_drift_ab", StringComparison.OrdinalIgnoreCase));
    }
}
