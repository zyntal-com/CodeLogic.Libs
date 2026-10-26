using CL.MySQL2;
using CL.MySQL2.Models;
using Xunit;

namespace MySQL2.Tests;

// Database / table administration, row locking, AnyAsync, UpdateAndGetAsync, the
// expression upsert, and the parameterization of captured values in column expressions.

[Table(Name = "it_adm")]
public sealed class AdmRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "sku", Size = 40, Unique = true, NotNull = true)] public string Sku { get; set; } = "";
    [Column(Name = "tokens", NotNull = true)] public int Tokens { get; set; }
    [Column(Name = "note", Size = 80)] public string? Note { get; set; }
}

[Collection("codelogic")]
public sealed class LiveAdminAndLockingTests
{
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public LiveAdminAndLockingTests(MySQL2RuntimeFixture fx) => _fx = fx;

    private async Task FreshAsync(params AdmRow[] rows)
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_adm");
        Assert.True((await Mysql.SyncTableAsync<AdmRow>(createBackup: false)).IsSuccess);
        foreach (var row in rows)
            Assert.True((await Mysql.GetRepository<AdmRow>().InsertAsync(row)).IsSuccess);
    }

    [DbFact]
    public async Task Database_create_exists_drop_round_trip()
    {
        var name = $"cl_test_adm_{Guid.NewGuid():N}"[..24];

        Assert.True((await Mysql.CreateDatabaseAsync(name)).Value);
        Assert.False((await Mysql.CreateDatabaseAsync(name)).Value);
        Assert.True((await Mysql.CreateDatabaseAsync(name, ifNotExists: false)).IsFailure);
        Assert.True((await Mysql.DatabaseExistsAsync(name)).Value);

        Assert.True((await Mysql.DropDatabaseAsync(name)).Value);
        Assert.False((await Mysql.DatabaseExistsAsync(name)).Value);
        Assert.False((await Mysql.DropDatabaseAsync(name)).Value);
        Assert.True((await Mysql.DropDatabaseAsync(name, ifExists: false)).IsFailure);
    }

    [DbFact]
    public void Database_admin_rejects_unsafe_charset()
    {
        Assert.Throws<ArgumentException>(() =>
            Mysql.CreateDatabaseAsync("cl_x", charset: "utf8mb4; DROP DATABASE cl_test").GetAwaiter().GetResult());
    }

    [DbFact]
    public async Task Table_create_truncate_drop_round_trip()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_adm");
        Assert.False((await Mysql.TableExistsAsync<AdmRow>()).Value);

        Assert.True((await Mysql.CreateTableAsync<AdmRow>()).Value);
        Assert.False((await Mysql.CreateTableAsync<AdmRow>()).Value);
        Assert.True((await Mysql.TableExistsAsync("it_adm")).Value);
        Assert.NotNull(await Mysql.SchemaState.GetStateAsync("it_adm"));

        await Mysql.GetRepository<AdmRow>().InsertAsync(new AdmRow { Sku = "a", Tokens = 1 });
        Assert.True((await Mysql.TruncateTableAsync<AdmRow>()).IsSuccess);
        Assert.Equal(0, (await Mysql.Query<AdmRow>().CountAsync()).Value);

        Assert.True((await Mysql.DropTableAsync<AdmRow>()).Value);
        Assert.False((await Mysql.TableExistsAsync<AdmRow>()).Value);
        Assert.Null(await Mysql.SchemaState.GetStateAsync("it_adm"));
        Assert.False((await Mysql.DropTableAsync<AdmRow>()).Value);
    }

    [DbFact]
    public async Task Captured_string_in_a_column_expression_is_stored_verbatim()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 1 });
        var hostile = "x\\' , `tokens` = 999 -- \\";

        var r = await Mysql.Query<AdmRow>().Where(x => x.Sku == "a")
            .UpdateAsync(x => new AdmRow { Note = x.Tokens > 0 ? hostile : x.Note });
        Assert.True(r.IsSuccess, r.Error?.ToString());

        var row = (await Mysql.Query<AdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(hostile, row.Note);
        Assert.Equal(1, row.Tokens);
    }

    [DbFact]
    public async Task UpdateAndGet_returns_the_clamped_row()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 8 }, new AdmRow { Sku = "b", Tokens = 1 });
        var cap = 10;
        var refill = 5;

        var r = await Mysql.Query<AdmRow>().Where(x => x.Sku == "a")
            .UpdateAndGetAsync(x => new AdmRow { Tokens = Math.Min(cap, x.Tokens + refill) });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        var row = Assert.Single(r.Value!);
        Assert.Equal(10, row.Tokens);
        Assert.Equal(1, (await Mysql.Query<AdmRow>().Where(x => x.Sku == "b").FirstOrDefaultAsync()).Value!.Tokens);

        var none = await Mysql.Query<AdmRow>().Where(x => x.Sku == "zzz")
            .UpdateAndGetAsync(x => new AdmRow { Tokens = 0 });
        Assert.Empty(none.Value!);
    }

    [DbFact]
    public async Task Expression_upsert_inserts_then_builds_on_the_current_row()
    {
        await FreshAsync();
        var repo = Mysql.GetRepository<AdmRow>();

        for (var i = 1; i <= 2; i++)
        {
            var r = await repo.UpsertAsync(
                new AdmRow { Sku = "ip", Tokens = 1, Note = $"try{i}" },
                (cur, inc) => new AdmRow { Tokens = cur.Tokens + 1, Note = inc.Note });
            Assert.True(r.IsSuccess, r.Error?.ToString());
        }

        var row = (await Mysql.Query<AdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(2, row.Tokens);
        Assert.Equal("try2", row.Note);
    }

    [DbFact]
    public async Task AnyAsync_reports_matches()
    {
        await FreshAsync(new AdmRow { Sku = "a", Tokens = 3 });
        Assert.True((await Mysql.Query<AdmRow>().AnyAsync(x => x.Tokens == 3)).Value);
        Assert.False((await Mysql.Query<AdmRow>().AnyAsync(x => x.Tokens == 4)).Value);
    }

    [DbFact]
    public async Task SkipLocked_lets_two_transactions_claim_different_rows()
    {
        await FreshAsync(new AdmRow { Sku = "j1" }, new AdmRow { Sku = "j2" });

        await using var tx1 = await Mysql.BeginTransactionAsync();
        await using var tx2 = await Mysql.BeginTransactionAsync();

        var first = await Mysql.Query<AdmRow>(tx1).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();
        var second = await Mysql.Query<AdmRow>(tx2).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal("j1", Assert.Single(first.Value!).Sku);
        Assert.Equal("j2", Assert.Single(second.Value!).Sku);

        await tx1.RollbackAsync();
        await tx2.RollbackAsync();
    }

    [DbFact]
    public async Task ForUpdate_outside_a_transaction_fails()
    {
        await FreshAsync(new AdmRow { Sku = "a" });
        var r = await Mysql.Query<AdmRow>().ForUpdate().ToListAsync();
        Assert.True(r.IsFailure);
        Assert.Contains("transaction", r.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }
}
