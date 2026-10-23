using CL.MSSQL;
using CL.MSSQL.Models;
using Xunit;

namespace MSSQL.Tests;

[Table(Name = "it_event_ignore", Schema = "dbo")]
public sealed class EventIgnoreRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "client_event_id", DataType = DataType.NVarChar, Size = 64, NotNull = true, Unique = true)]
    public string ClientEventId { get; set; } = "";

    [Column(Name = "payload", DataType = DataType.NVarChar, Size = 200)]
    public string? Payload { get; set; }
}

[Table(Name = "it_agg_nullable", Schema = "dbo")]
public sealed class AggNullableRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "created_utc", DataType = DataType.DateTime2, NotNull = true)]
    public DateTime CreatedUtc { get; set; }

    [Column(Name = "amount", DataType = DataType.Int, NotNull = true)]
    public int Amount { get; set; }
}

// Mirrors MySQL2.Tests.InsertIgnoreAndAggregateNullablesTests so both providers keep the
// same observable behaviour for InsertIgnore, the duplicate-key opt-in and nullable aggregates.
// The non-duplicate failure case uses a NOT NULL violation (error 515) rather than an over-long
// string: CL.MSSQL sizes NVARCHAR parameters from [Column(Size)], so SqlClient truncates long
// values client-side and SQL Server never reports truncation.
[Collection("codelogic")]
public sealed class InsertIgnoreAndAggregateNullablesTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public InsertIgnoreAndAggregateNullablesTests(MSSQLRuntimeFixture fx) => _fx = fx;

    private async Task ResetEventsAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_event_ignore]");
        Assert.True((await Mssql.SyncTableAsync<EventIgnoreRow>(createBackup: false)).IsSuccess);
    }

    [DbFact]
    public async Task InsertIgnoreAsync_Inserts_With_Id_And_Duplicates_Return_Duplicate_Without_Exception()
    {
        await ResetEventsAsync();
        var repo = Mssql.GetRepository<EventIgnoreRow>();

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

        var loaded = (await Mssql.Query<EventIgnoreRow>().Where(r => r.ClientEventId == "evt-100").FirstOrDefaultAsync()).Value;
        Assert.Equal("original", loaded!.Payload);
    }

    [DbFact]
    public async Task InsertIgnoreAsync_Fails_On_Non_Duplicate_Error_And_Writes_Nothing()
    {
        await ResetEventsAsync();
        var repo = Mssql.GetRepository<EventIgnoreRow>();

        var res = await repo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = null!, Payload = "data" });
        Assert.False(res.IsSuccess);
        Assert.Equal("mssql.insert_failed", res.Error?.Code);
        Assert.Equal(0, (await Mssql.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [DbFact]
    public async Task InsertManyIgnoreAsync_With_Mix_Returns_Only_Inserted_Count()
    {
        await ResetEventsAsync();
        var repo = Mssql.GetRepository<EventIgnoreRow>();
        await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-1", Payload = "existing" });

        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "evt-1", Payload = "dup" },
            new EventIgnoreRow { ClientEventId = "evt-2", Payload = "new-2" },
            new EventIgnoreRow { ClientEventId = "evt-3", Payload = "new-3" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(res.IsSuccess);
        Assert.Equal(2, res.Value);
        Assert.Equal(3, (await Mssql.Query<EventIgnoreRow>().CountAsync()).Value);

        var again = await repo.InsertManyIgnoreAsync(batch);
        Assert.True(again.IsSuccess);
        Assert.Equal(0, again.Value);
    }

    [DbFact]
    public async Task InsertManyIgnoreAsync_Non_Duplicate_Error_Rolls_Back_The_Whole_Call()
    {
        await ResetEventsAsync();
        var repo = Mssql.GetRepository<EventIgnoreRow>();
        var batch = new[]
        {
            new EventIgnoreRow { ClientEventId = "ok-1", Payload = "a" },
            new EventIgnoreRow { ClientEventId = "ok-2", Payload = "b" },
            new EventIgnoreRow { ClientEventId = null!, Payload = "NOT NULL violation" }
        };

        var res = await repo.InsertManyIgnoreAsync(batch);
        Assert.False(res.IsSuccess);
        Assert.Equal("mssql.bulk_insert_failed", res.Error?.Code);
        Assert.Equal(0, (await Mssql.Query<EventIgnoreRow>().CountAsync()).Value);
    }

    [DbFact]
    public async Task InsertIgnore_Inside_Transaction_Rolls_Back_Only_Its_Own_Write()
    {
        await ResetEventsAsync();

        await using (var tx = await Mssql.BeginTransactionAsync())
        {
            var txRepo = Mssql.GetRepository<EventIgnoreRow>(tx);
            Assert.True((await txRepo.InsertAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "kept" })).IsSuccess);

            var dup = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "earlier", Payload = "dup" });
            Assert.Equal(InsertOutcome.Duplicate, dup.Value);

            var bad = await txRepo.InsertManyIgnoreAsync(new[]
            {
                new EventIgnoreRow { ClientEventId = "rolled-back", Payload = "x" },
                new EventIgnoreRow { ClientEventId = null! }
            });
            Assert.False(bad.IsSuccess);

            var added = await txRepo.InsertIgnoreAsync(new EventIgnoreRow { ClientEventId = "later", Payload = "kept" });
            Assert.Equal(InsertOutcome.Inserted, added.Value);
            await tx.CommitAsync();
        }

        var ids = (await Mssql.Query<EventIgnoreRow>().OrderBy(r => r.Id).ToListAsync()).Value!
            .Select(r => r.ClientEventId).ToArray();
        Assert.Equal(new[] { "earlier", "later" }, ids);
    }

    [DbFact]
    public async Task InsertAsync_DuplicateKey_OptIn_Returns_DuplicateKey()
    {
        await ResetEventsAsync();
        var repo = Mssql.GetRepository<EventIgnoreRow>();
        await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });

        var defaultResult = await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });
        Assert.Equal("mssql.insert_failed", defaultResult.Error?.Code);

        repo.DistinguishDuplicateKeyError = true;
        var single = await repo.InsertAsync(new EventIgnoreRow { ClientEventId = "evt-dup" });
        Assert.Equal("mssql.duplicate_key", single.Error?.Code);
        var many = await repo.InsertManyAsync(new[] { new EventIgnoreRow { ClientEventId = "evt-dup" } });
        Assert.Equal("mssql.duplicate_key", many.Error?.Code);
    }

    [DbFact]
    public async Task MaxAsync_MinAsync_SumAsync_Nullable_And_NonNullable_Behavior()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_agg_nullable]");
        Assert.True((await Mssql.SyncTableAsync<AggNullableRow>(createBackup: false)).IsSuccess);

        Assert.Null((await Mssql.Query<AggNullableRow>().MaxAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Null((await Mssql.Query<AggNullableRow>().SumAsync(r => (int?)r.Amount)).Value);
        Assert.Equal(DateTime.MinValue, (await Mssql.Query<AggNullableRow>().MaxAsync(r => r.CreatedUtc)).Value);

        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddDays(3);
        var repo = Mssql.GetRepository<AggNullableRow>();
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t1, Amount = 5 });
        await repo.InsertAsync(new AggNullableRow { CreatedUtc = t2, Amount = 7 });

        Assert.Equal(t2, (await Mssql.Query<AggNullableRow>().MaxAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Equal(t1, (await Mssql.Query<AggNullableRow>().MinAsync(r => (DateTime?)r.CreatedUtc)).Value);
        Assert.Equal(12, (await Mssql.Query<AggNullableRow>().SumAsync(r => (int?)r.Amount)).Value);
        Assert.Equal(12, (await Mssql.Query<AggNullableRow>().SumAsync(r => r.Amount)).Value);
    }
}
