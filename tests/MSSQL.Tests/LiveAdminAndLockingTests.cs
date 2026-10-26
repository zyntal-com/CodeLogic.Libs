using CL.MSSQL;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Xunit;

namespace MSSQL.Tests;

// Database / table administration, row locking, AnyAsync, UpdateAndGetAsync, the
// expression upsert, and the parameterization of captured values in column expressions.

[Table(Name = "it_adm", Schema = "dbo")]
public sealed class AdmRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "sku", DataType = DataType.NVarChar, Size = 40, Unique = true, NotNull = true)]
    public string Sku { get; set; } = "";

    [Column(Name = "tokens", DataType = DataType.Int, NotNull = true)]
    public int Tokens { get; set; }

    [Column(Name = "note", DataType = DataType.NVarChar, Size = 80)]
    public string? Note { get; set; }
}

// SQL shapes that need no server: lock hints, AnyAsync and both UpdateAndGetAsync forms.
public sealed class LockingSqlShapeTests
{
    private static QueryBuilder<AdmRow> Query() => new(new ConnectionManager());

    [Fact]
    public void Any_renders_top_1_with_the_lock_hint_after_the_table()
    {
        var (sql, parms) = Query().Where(x => x.Tokens > 3).ForUpdate(LockWait.SkipLocked).BuildAnySql();
        Assert.Equal("SELECT TOP (1) 1 FROM [dbo].[it_adm] WITH (UPDLOCK, ROWLOCK, READPAST) WHERE ([tokens] > @qb_0)", sql);
        Assert.Equal(3, parms["@qb_0"]);
    }

    [Theory]
    [InlineData(true, LockWait.Wait, " WITH (UPDLOCK, ROWLOCK)")]
    [InlineData(true, LockWait.NoWait, " WITH (UPDLOCK, ROWLOCK, NOWAIT)")]
    [InlineData(false, LockWait.Wait, " WITH (HOLDLOCK, ROWLOCK)")]
    [InlineData(false, LockWait.NoWait, " WITH (HOLDLOCK, ROWLOCK, NOWAIT)")]
    [InlineData(false, LockWait.SkipLocked, " WITH (REPEATABLEREAD, ROWLOCK, READPAST)")]
    public void Lock_modes_render_their_table_hint(bool exclusive, LockWait wait, string hint)
    {
        var q = Query();
        if (exclusive) q.ForUpdate(wait); else q.ForShare(wait);
        Assert.Equal($"SELECT TOP (1) 1 FROM [dbo].[it_adm]{hint}", q.BuildAnySql().Sql);
    }

    [Fact]
    public void UpdateAndGet_without_take_is_one_update_with_output()
    {
        var cap = 10;
        var note = "refilled";
        var (sql, parms) = Query().Where(x => x.Sku == "a")
            .BuildUpdateAndGetSql(x => new AdmRow { Tokens = Math.Min(cap, x.Tokens + 1), Note = x.Tokens > 0 ? note : x.Note });
        Assert.StartsWith("UPDATE [dbo].[it_adm] SET [tokens] = ", sql);
        Assert.EndsWith(" OUTPUT INSERTED.* WHERE ([sku] = @qb_0);", sql);
        // Primitive numbers are inlined; the captured string is a parameter.
        Assert.Contains("@upd_0", sql);
        Assert.Equal(note, parms["@upd_0"]);
        Assert.Equal("a", parms["@qb_0"]);
    }

    [Fact]
    public void UpdateAndGet_with_take_updates_through_a_locked_cte()
    {
        var (sql, _) = Query().Where(x => x.Note == null).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked)
            .BuildUpdateAndGetSql(x => new AdmRow { Note = "claimed" });
        Assert.Equal(
            "WITH [__target] AS (SELECT TOP (1) * FROM [dbo].[it_adm] WITH (UPDLOCK, ROWLOCK, READPAST) " +
            "WHERE ([note] IS NULL) ORDER BY [id] ASC) UPDATE [__target] SET [note] = @upd_0 OUTPUT INSERTED.*;", sql);
    }

    [Fact]
    public void UpdateAndGet_rejects_offset()
    {
        Assert.Throws<NotSupportedException>(() =>
            Query().Skip(1).BuildUpdateAndGetSql(x => new AdmRow { Tokens = 0 }));
    }
}

[Collection("codelogic")]
public sealed class LiveAdminAndLockingTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public LiveAdminAndLockingTests(MSSQLRuntimeFixture fx) => _fx = fx;

    private async Task FreshAsync(params AdmRow[] rows)
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_adm]");
        Assert.True((await Mssql.SyncTableAsync<AdmRow>(createBackup: false)).IsSuccess);
        foreach (var row in rows)
            Assert.True((await Mssql.GetRepository<AdmRow>().InsertAsync(row)).IsSuccess);
    }

    [DbFact]
    public async Task Database_create_exists_drop_round_trip()
    {
        var name = $"cl_test_adm_{Guid.NewGuid():N}"[..24];

        Assert.True((await Mssql.CreateDatabaseAsync(name)).Value);
        Assert.False((await Mssql.CreateDatabaseAsync(name)).Value);
        Assert.True((await Mssql.CreateDatabaseAsync(name, ifNotExists: false)).IsFailure);
        Assert.True((await Mssql.DatabaseExistsAsync(name)).Value);

        Assert.True((await Mssql.DropDatabaseAsync(name)).Value);
        Assert.False((await Mssql.DatabaseExistsAsync(name)).Value);
        Assert.False((await Mssql.DropDatabaseAsync(name)).Value);
        Assert.True((await Mssql.DropDatabaseAsync(name, ifExists: false)).IsFailure);
    }

    [DbFact]
    public async Task Database_create_honours_a_collation()
    {
        var name = $"cl_test_col_{Guid.NewGuid():N}"[..24];
        try
        {
            Assert.True((await Mssql.CreateDatabaseAsync(name, collation: "Latin1_General_100_CS_AS")).Value);
            var collation = await Mssql.ConnectionManager.ExecuteOnServerAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(@n, 'Collation'))";
                cmd.Parameters.AddWithValue("@n", name);
                return (string?)await cmd.ExecuteScalarAsync();
            });
            Assert.Equal("Latin1_General_100_CS_AS", collation);
        }
        finally
        {
            await Mssql.DropDatabaseAsync(name);
        }
    }

    [DbFact]
    public void Database_admin_rejects_unsafe_collation()
    {
        Assert.Throws<ArgumentException>(() =>
            Mssql.CreateDatabaseAsync("cl_x", collation: "Latin1_General_CI_AS; DROP DATABASE cl_test").GetAwaiter().GetResult());
    }

    [DbFact]
    public async Task Table_create_truncate_drop_round_trip()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_adm]");
        await Mssql.SchemaState.RemoveStateAsync("dbo.it_adm");
        Assert.False((await Mssql.TableExistsAsync<AdmRow>()).Value);

        Assert.True((await Mssql.CreateTableAsync<AdmRow>()).Value);
        Assert.False((await Mssql.CreateTableAsync<AdmRow>()).Value);
        Assert.True((await Mssql.CreateTableAsync<AdmRow>(ifNotExists: false)).IsFailure);
        Assert.True((await Mssql.TableExistsAsync("it_adm")).Value);
        Assert.True((await Mssql.TableExistsAsync("dbo.it_adm")).Value);
        Assert.False((await Mssql.TableExistsAsync("nosuchschema.it_adm")).Value);
        Assert.NotNull(await Mssql.SchemaState.GetStateAsync("dbo.it_adm"));

        await Mssql.GetRepository<AdmRow>().InsertAsync(new AdmRow { Sku = "a", Tokens = 1 });
        Assert.True((await Mssql.TruncateTableAsync<AdmRow>()).IsSuccess);
        Assert.Equal(0, (await Mssql.Query<AdmRow>().CountAsync()).Value);

        Assert.True((await Mssql.DropTableAsync<AdmRow>()).Value);
        Assert.False((await Mssql.TableExistsAsync<AdmRow>()).Value);
        Assert.Null(await Mssql.SchemaState.GetStateAsync("dbo.it_adm"));
        Assert.False((await Mssql.DropTableAsync<AdmRow>()).Value);
        Assert.True((await Mssql.DropTableAsync<AdmRow>(ifExists: false)).IsFailure);
    }

    [DbFact]
    public async Task Captured_string_in_a_column_expression_is_stored_verbatim()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 1 });
        var hostile = "x' , [tokens] = 999 -- ]";

        var r = await Mssql.Query<AdmRow>().Where(x => x.Sku == "a")
            .UpdateAsync(x => new AdmRow { Note = x.Tokens > 0 ? hostile : x.Note });
        Assert.True(r.IsSuccess, r.Error?.ToString());

        var row = (await Mssql.Query<AdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(hostile, row.Note);
        Assert.Equal(1, row.Tokens);
    }

    [DbFact]
    public async Task UpdateAndGet_returns_the_clamped_row()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 8 }, new AdmRow { Sku = "b", Tokens = 1 });
        var cap = 10;
        var refill = 5;

        var r = await Mssql.Query<AdmRow>().Where(x => x.Sku == "a")
            .UpdateAndGetAsync(x => new AdmRow { Tokens = Math.Min(cap, x.Tokens + refill) });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        var row = Assert.Single(r.Value!);
        Assert.Equal(10, row.Tokens);
        Assert.Equal("a", row.Sku);
        Assert.Equal(1, (await Mssql.Query<AdmRow>().Where(x => x.Sku == "b").FirstOrDefaultAsync()).Value!.Tokens);

        var none = await Mssql.Query<AdmRow>().Where(x => x.Sku == "zzz")
            .UpdateAndGetAsync(x => new AdmRow { Tokens = 0 });
        Assert.True(none.IsSuccess, none.Error?.ToString());
        Assert.Empty(none.Value!);
    }

    [DbFact]
    public async Task UpdateAndGet_with_take_claims_rows_in_order()
    {
        await FreshAsync(
            new AdmRow { Sku = "j1", Tokens = 1 }, new AdmRow { Sku = "j2", Tokens = 1 },
            new AdmRow { Sku = "j3", Tokens = 1 });

        var r = await Mssql.Query<AdmRow>().Where(x => x.Note == null).OrderByDescending(x => x.Id).Take(2)
            .ForUpdate(LockWait.SkipLocked)
            .UpdateAndGetAsync(x => new AdmRow { Note = "claimed", Tokens = x.Tokens + 1 });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.Equal(["j3", "j2"], r.Value!.Select(x => x.Sku));
        Assert.All(r.Value!, x => Assert.Equal(2, x.Tokens));
        Assert.Null((await Mssql.Query<AdmRow>().Where(x => x.Sku == "j1").FirstOrDefaultAsync()).Value!.Note);
    }

    [DbFact]
    public async Task UpdateAndGet_skips_rows_locked_by_another_transaction()
    {
        await FreshAsync(new AdmRow { Sku = "j1" }, new AdmRow { Sku = "j2" });

        await using var tx1 = await Mssql.BeginTransactionAsync();
        var held = await Mssql.Query<AdmRow>(tx1).OrderBy(x => x.Id).Take(1).ForUpdate().ToListAsync();
        Assert.Equal("j1", Assert.Single(held.Value!).Sku);

        var claimed = await Mssql.Query<AdmRow>().OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked)
            .UpdateAndGetAsync(x => new AdmRow { Note = "claimed" });
        Assert.True(claimed.IsSuccess, claimed.Error?.ToString());
        Assert.Equal("j2", Assert.Single(claimed.Value!).Sku);

        await tx1.RollbackAsync();
    }

    [DbFact]
    public async Task UpdateAndGet_runs_on_the_query_transaction()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 1 });

        await using (var tx = await Mssql.BeginTransactionAsync())
        {
            var r = await Mssql.Query<AdmRow>(tx).Where(x => x.Sku == "a")
                .UpdateAndGetAsync(x => new AdmRow { Tokens = 50 });
            Assert.Equal(50, Assert.Single(r.Value!).Tokens);
            await tx.RollbackAsync();
        }

        Assert.Equal(1, (await Mssql.Query<AdmRow>().FirstOrDefaultAsync()).Value!.Tokens);
    }

    [DbFact]
    public async Task Expression_upsert_inserts_then_builds_on_the_current_row()
    {
        await FreshAsync();
        var repo = Mssql.GetRepository<AdmRow>();

        for (var i = 1; i <= 2; i++)
        {
            var r = await repo.UpsertAsync(
                new AdmRow { Sku = "ip", Tokens = 1, Note = $"try{i}" },
                (cur, inc) => new AdmRow { Tokens = cur.Tokens + 1, Note = inc.Note });
            Assert.True(r.IsSuccess, r.Error?.ToString());
            Assert.Equal(1, r.Value);
        }

        var row = (await Mssql.Query<AdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(2, row.Tokens);
        Assert.Equal("try2", row.Note);
    }

    [DbFact]
    public async Task Expression_upsert_sets_see_the_old_row_and_bind_captured_values()
    {
        await FreshAsync(new AdmRow { Sku = "k", Tokens = 5, Note = "old" });
        var repo = Mssql.GetRepository<AdmRow>();
        var hostile = "x'; DROP TABLE [dbo].[it_adm]; --";
        var cap = 7;

        // Tokens is assigned first, yet the Note condition still reads the pre-update value 5.
        var r = await repo.UpsertAsync(
            new AdmRow { Sku = "k", Tokens = 100 },
            (cur, inc) => new AdmRow
            {
                Tokens = Math.Min(cap, cur.Tokens + inc.Tokens),
                Note = cur.Tokens == 5 ? hostile : "changed",
            });
        Assert.True(r.IsSuccess, r.Error?.ToString());

        var row = (await Mssql.Query<AdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(7, row.Tokens);
        Assert.Equal(hostile, row.Note);
    }

    [DbFact]
    public async Task Expression_upsert_rejects_incoming_identity()
    {
        await FreshAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Mssql.GetRepository<AdmRow>().UpsertAsync(
                new AdmRow { Sku = "z" },
                (cur, inc) => new AdmRow { Tokens = (int)inc.Id }));
    }

    [DbFact]
    public async Task AnyAsync_reports_matches()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 3 });
        Assert.True((await Mssql.Query<AdmRow>().AnyAsync(x => x.Tokens == 3)).Value);
        Assert.False((await Mssql.Query<AdmRow>().AnyAsync(x => x.Tokens == 4)).Value);
    }

    [DbFact]
    public async Task SkipLocked_lets_two_transactions_claim_different_rows()
    {
        await FreshAsync(new AdmRow { Sku = "j1" }, new AdmRow { Sku = "j2" });

        await using var tx1 = await Mssql.BeginTransactionAsync();
        await using var tx2 = await Mssql.BeginTransactionAsync();

        var first = await Mssql.Query<AdmRow>(tx1).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();
        var second = await Mssql.Query<AdmRow>(tx2).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal("j1", Assert.Single(first.Value!).Sku);
        Assert.Equal("j2", Assert.Single(second.Value!).Sku);

        await tx1.RollbackAsync();
        await tx2.RollbackAsync();
    }

    [DbFact]
    public async Task NoWait_fails_fast_on_a_locked_row()
    {
        await FreshAsync(new AdmRow { Sku = "a" });

        await using var tx1 = await Mssql.BeginTransactionAsync();
        await using var tx2 = await Mssql.BeginTransactionAsync();

        var held = await Mssql.Query<AdmRow>(tx1).Where(x => x.Sku == "a").ForUpdate().FirstOrDefaultAsync();
        Assert.NotNull(held.Value);

        var blocked = await Mssql.Query<AdmRow>(tx2).Where(x => x.Sku == "a").ForUpdate(LockWait.NoWait).AnyAsync();
        Assert.True(blocked.IsFailure);

        await tx1.RollbackAsync();
        await tx2.RollbackAsync();
    }

    [DbFact]
    public async Task Shared_locks_and_projections_work_inside_a_transaction()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 4 }, new AdmRow { Sku = "b", Tokens = 6 });

        await using var tx = await Mssql.BeginTransactionAsync();
        var shared = await Mssql.Query<AdmRow>(tx).ForShare().ToListAsync();
        Assert.Equal(2, shared.Value!.Count);

        var skipShared = await Mssql.Query<AdmRow>(tx).ForShare(LockWait.SkipLocked).ToListAsync();
        Assert.True(skipShared.IsSuccess, skipShared.Error?.ToString());

        var projected = await Mssql.Query<AdmRow>(tx).Where(x => x.Sku == "b").ForUpdate()
            .Select(x => new { x.Sku, x.Tokens }).ToListAsync();
        Assert.True(projected.IsSuccess, projected.Error?.ToString());
        Assert.Equal(6, Assert.Single(projected.Value!).Tokens);
        await tx.RollbackAsync();
    }

    [DbFact]
    public async Task ForUpdate_outside_a_transaction_fails()
    {
        await FreshAsync(new AdmRow { Sku = "a" });
        var r = await Mssql.Query<AdmRow>().ForUpdate().ToListAsync();
        Assert.True(r.IsFailure);
        Assert.Contains("transaction", r.Error!.Message, StringComparison.OrdinalIgnoreCase);

        var count = await Mssql.Query<AdmRow>().ForUpdate().CountAsync();
        Assert.True(count.IsFailure);
    }
}
