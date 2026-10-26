using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// Database / table administration, row locking, AnyAsync, UpdateAndGetAsync, the
// expression upsert, and the parameterization of captured values in column expressions.

// No [Table(Schema)]: lands in the connection's DefaultSchema ("public").
[Table(Name = "it_adm")]
public sealed class PgAdmRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "sku", Size = 40, Unique = true, NotNull = true)] public string Sku { get; set; } = "";
    [Column(Name = "tokens", NotNull = true)] public int Tokens { get; set; }
    [Column(Name = "hits", NotNull = true)] public int Hits { get; set; }
    [Column(Name = "note", Size = 80)] public string? Note { get; set; }
}

[Table(Name = "it_adm_scoped", Schema = "cl_adm_schema")]
public sealed class PgAdmScopedRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 40)] public string? Label { get; set; }
}

[Collection("codelogic")]
public sealed class LiveAdminAndLockingTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Pg => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveAdminAndLockingTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task FreshAsync(params PgAdmRow[] rows)
    {
        Assert.True((await Pg.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_adm\" CASCADE")).IsSuccess);
        var sync = await Pg.SyncTableAsync<PgAdmRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        foreach (var row in rows)
        {
            var ins = await Pg.GetRepository<PgAdmRow>().InsertAsync(row);
            Assert.True(ins.IsSuccess, ins.Error?.Message);
        }
    }

    private static string NewDbName() => $"cl_test_adm_{Guid.NewGuid():N}"[..24];

    // ── Databases ─────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Database_create_exists_drop_round_trip()
    {
        var name = NewDbName();

        Assert.True((await Pg.CreateDatabaseAsync(name)).Value);
        Assert.False((await Pg.CreateDatabaseAsync(name)).Value);
        Assert.True((await Pg.CreateDatabaseAsync(name, ifNotExists: false)).IsFailure);
        Assert.True((await Pg.DatabaseExistsAsync(name)).Value);

        Assert.True((await Pg.DropDatabaseAsync(name)).Value);
        Assert.False((await Pg.DatabaseExistsAsync(name)).Value);
        Assert.False((await Pg.DropDatabaseAsync(name)).Value);
        Assert.True((await Pg.DropDatabaseAsync(name, ifExists: false)).IsFailure);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Database_create_with_encoding_and_collation_uses_template0()
    {
        var name = NewDbName();
        try
        {
            var created = await Pg.CreateDatabaseAsync(name, encoding: "UTF8", collation: "C");
            Assert.True(created.IsSuccess, created.Error?.ToString());
            Assert.True(created.Value);

            var info = await Pg.SqlQueryAsync<DbInfoRow>(
                "SELECT pg_encoding_to_char(encoding) AS \"Encoding\", datcollate AS \"Collate\" FROM pg_database WHERE datname = @n",
                new Dictionary<string, object?> { ["@n"] = name });
            Assert.True(info.IsSuccess, info.Error?.ToString());
            var row = Assert.Single(info.Value!);
            Assert.Equal("UTF8", row.Encoding);
            Assert.Equal("C", row.Collate);
        }
        finally
        {
            await Pg.DropDatabaseAsync(name);
        }
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Drop_database_forces_out_open_sessions()
    {
        var name = NewDbName();
        Assert.True((await Pg.CreateDatabaseAsync(name)).Value);

        // A session parked on the database would block a plain DROP DATABASE.
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(Pg.ConnectionManager.GetConnectionString())
        {
            Database = name,
            Pooling = false
        };
        await using var squatter = new Npgsql.NpgsqlConnection(cs.ConnectionString);
        await squatter.OpenAsync();

        var dropped = await Pg.DropDatabaseAsync(name);
        Assert.True(dropped.IsSuccess, dropped.Error?.ToString());
        Assert.False((await Pg.DatabaseExistsAsync(name)).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public void Database_admin_rejects_unsafe_encoding_and_collation()
    {
        Assert.Throws<ArgumentException>(() =>
            Pg.CreateDatabaseAsync("cl_x", encoding: "UTF8' TEMPLATE template1 --").GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() =>
            Pg.CreateDatabaseAsync("cl_x", collation: "C'; DROP DATABASE cl_test; --").GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() =>
            Pg.CreateDatabaseAsync(new string('x', 64)).GetAwaiter().GetResult());
    }

    // ── Tables ────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Table_create_truncate_drop_round_trip()
    {
        await Pg.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_adm\" CASCADE");
        Assert.False((await Pg.TableExistsAsync<PgAdmRow>()).Value);

        Assert.True((await Pg.CreateTableAsync<PgAdmRow>()).Value);
        Assert.False((await Pg.CreateTableAsync<PgAdmRow>()).Value);
        Assert.True((await Pg.CreateTableAsync<PgAdmRow>(ifNotExists: false)).IsFailure);
        Assert.True((await Pg.TableExistsAsync("it_adm")).Value);
        Assert.True((await Pg.TableExistsAsync("public.it_adm")).Value);
        Assert.NotNull(await Pg.SchemaState.GetStateAsync("public.it_adm"));

        var repo = Pg.GetRepository<PgAdmRow>();
        await repo.InsertAsync(new PgAdmRow { Sku = "a", Tokens = 1 });
        await repo.InsertAsync(new PgAdmRow { Sku = "b", Tokens = 1 });
        Assert.True((await Pg.TruncateTableAsync<PgAdmRow>()).IsSuccess);
        Assert.Equal(0, (await Pg.Query<PgAdmRow>().CountAsync()).Value);

        // RESTART IDENTITY (the default) resets the identity sequence.
        var again = await repo.InsertAsync(new PgAdmRow { Sku = "c", Tokens = 1 });
        Assert.Equal(1, again.Value!.Id);

        Assert.True((await Pg.DropTableAsync<PgAdmRow>()).Value);
        Assert.False((await Pg.TableExistsAsync<PgAdmRow>()).Value);
        Assert.Null(await Pg.SchemaState.GetStateAsync("public.it_adm"));
        Assert.False((await Pg.DropTableAsync<PgAdmRow>()).Value);
        Assert.True((await Pg.DropTableAsync<PgAdmRow>(ifExists: false)).IsFailure);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Table_admin_honours_the_entity_schema()
    {
        await Pg.ExecuteSqlAsync("DROP SCHEMA IF EXISTS \"cl_adm_schema\" CASCADE");
        try
        {
            var created = await Pg.CreateTableAsync<PgAdmScopedRow>();
            Assert.True(created.IsSuccess, created.Error?.ToString());
            Assert.True(created.Value);
            Assert.True((await Pg.TableExistsAsync<PgAdmScopedRow>()).Value);
            Assert.True((await Pg.TableExistsAsync("cl_adm_schema.it_adm_scoped")).Value);
            // Not in the default schema.
            Assert.False((await Pg.TableExistsAsync("it_adm_scoped")).Value);
            Assert.NotNull(await Pg.SchemaState.GetStateAsync("cl_adm_schema.it_adm_scoped"));

            Assert.True((await Pg.DropTableAsync<PgAdmScopedRow>()).Value);
            Assert.False((await Pg.TableExistsAsync<PgAdmScopedRow>()).Value);
            Assert.Null(await Pg.SchemaState.GetStateAsync("cl_adm_schema.it_adm_scoped"));
        }
        finally
        {
            await Pg.ExecuteSqlAsync("DROP SCHEMA IF EXISTS \"cl_adm_schema\" CASCADE");
        }
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Drop_table_needs_cascade_when_a_view_depends_on_it()
    {
        await FreshAsync(new PgAdmRow { Sku = "a" });
        Assert.True((await Pg.ExecuteSqlAsync(
            "CREATE VIEW \"public\".\"it_adm_view\" AS SELECT \"sku\" FROM \"public\".\"it_adm\"")).IsSuccess);

        Assert.True((await Pg.DropTableAsync<PgAdmRow>()).IsFailure);
        Assert.True((await Pg.TableExistsAsync<PgAdmRow>()).Value);

        Assert.True((await Pg.DropTableAsync<PgAdmRow>(cascade: true)).Value);
        Assert.False((await Pg.TableExistsAsync("it_adm_view")).Value);
    }

    // ── Column expressions / UpdateAndGet / upsert / Any ──────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Captured_string_in_a_column_expression_is_stored_verbatim()
    {
        await FreshAsync(new PgAdmRow { Sku = "a", Tokens = 1 });
        var hostile = "x\\' , \"tokens\" = 999 -- \\";

        var r = await Pg.Query<PgAdmRow>().Where(x => x.Sku == "a")
            .UpdateAsync(x => new PgAdmRow { Note = x.Tokens > 0 ? hostile : x.Note });
        Assert.True(r.IsSuccess, r.Error?.ToString());

        var row = (await Pg.Query<PgAdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(hostile, row.Note);
        Assert.Equal(1, row.Tokens);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task UpdateAndGet_returns_the_clamped_row()
    {
        await FreshAsync(new PgAdmRow { Sku = "a", Tokens = 8 }, new PgAdmRow { Sku = "b", Tokens = 1 });
        var cap = 10;
        var refill = 5;

        var r = await Pg.Query<PgAdmRow>().Where(x => x.Sku == "a")
            .UpdateAndGetAsync(x => new PgAdmRow { Tokens = Math.Min(cap, x.Tokens + refill) });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        var row = Assert.Single(r.Value!);
        Assert.Equal(10, row.Tokens);
        Assert.Equal("a", row.Sku);
        Assert.Equal(1, (await Pg.Query<PgAdmRow>().Where(x => x.Sku == "b").FirstOrDefaultAsync()).Value!.Tokens);

        var none = await Pg.Query<PgAdmRow>().Where(x => x.Sku == "zzz")
            .UpdateAndGetAsync(x => new PgAdmRow { Tokens = 0 });
        Assert.True(none.IsSuccess, none.Error?.ToString());
        Assert.Empty(none.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task UpdateAndGet_with_order_and_take_updates_only_the_first_rows_in_order()
    {
        await FreshAsync(
            new PgAdmRow { Sku = "j1", Tokens = 3 },
            new PgAdmRow { Sku = "j2", Tokens = 1 },
            new PgAdmRow { Sku = "j3", Tokens = 2 });

        var r = await Pg.Query<PgAdmRow>().Where(x => x.Note == null)
            .OrderByDescending(x => x.Tokens).Take(2)
            .UpdateAndGetAsync(x => new PgAdmRow { Note = "claimed", Hits = x.Hits + 1 });

        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.Equal(["j1", "j3"], r.Value!.Select(x => x.Sku).ToArray());
        Assert.All(r.Value!, x => Assert.Equal(1, x.Hits));
        var untouched = (await Pg.Query<PgAdmRow>().Where(x => x.Sku == "j2").FirstOrDefaultAsync()).Value!;
        Assert.Null(untouched.Note);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task UpdateAndGet_skip_locked_claims_the_next_free_job()
    {
        await FreshAsync(new PgAdmRow { Sku = "j1" }, new PgAdmRow { Sku = "j2" });

        await using var tx1 = await Pg.BeginTransactionAsync();
        await using var tx2 = await Pg.BeginTransactionAsync();

        var first = await Pg.Query<PgAdmRow>(tx1).Where(x => x.Note == null).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked)
            .UpdateAndGetAsync(x => new PgAdmRow { Note = "worker1" });
        var second = await Pg.Query<PgAdmRow>(tx2).Where(x => x.Note == null).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked)
            .UpdateAndGetAsync(x => new PgAdmRow { Note = "worker2" });

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal("j1", Assert.Single(first.Value!).Sku);
        Assert.Equal("j2", Assert.Single(second.Value!).Sku);

        await tx1.CommitAsync();
        await tx2.CommitAsync();
        Assert.Equal(2, (await Pg.Query<PgAdmRow>().Where(x => x.Note != null).CountAsync()).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Expression_upsert_inserts_then_builds_on_the_current_row()
    {
        await FreshAsync();
        var repo = Pg.GetRepository<PgAdmRow>();

        for (var i = 1; i <= 2; i++)
        {
            var r = await repo.UpsertAsync(
                new PgAdmRow { Sku = "ip", Tokens = 1, Note = $"try{i}" },
                (cur, inc) => new PgAdmRow { Tokens = cur.Tokens + 1, Note = inc.Note });
            Assert.True(r.IsSuccess, r.Error?.ToString());
            Assert.Equal(1, r.Value);
        }

        var row = (await Pg.Query<PgAdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(2, row.Tokens);
        Assert.Equal("try2", row.Note);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Expression_upsert_reads_old_values_regardless_of_assignment_order()
    {
        await FreshAsync(new PgAdmRow { Sku = "k", Tokens = 5 });
        var bonus = 10;

        var r = await Pg.GetRepository<PgAdmRow>().UpsertAsync(
            new PgAdmRow { Sku = "k", Tokens = 1 },
            (cur, inc) => new PgAdmRow { Tokens = cur.Tokens + bonus, Hits = cur.Tokens },
            conflictTarget: [nameof(PgAdmRow.Sku)]);
        Assert.True(r.IsSuccess, r.Error?.ToString());

        var row = (await Pg.Query<PgAdmRow>().FirstOrDefaultAsync()).Value!;
        Assert.Equal(15, row.Tokens);
        Assert.Equal(5, row.Hits); // the pre-update value, not 15
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task AnyAsync_reports_matches()
    {
        await FreshAsync(new PgAdmRow { Sku = "a", Tokens = 3 });
        Assert.True((await Pg.Query<PgAdmRow>().AnyAsync(x => x.Tokens == 3)).Value);
        Assert.False((await Pg.Query<PgAdmRow>().AnyAsync(x => x.Tokens == 4)).Value);
    }

    // ── Locking ───────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task SkipLocked_lets_two_transactions_claim_different_rows()
    {
        await FreshAsync(new PgAdmRow { Sku = "j1" }, new PgAdmRow { Sku = "j2" });

        await using var tx1 = await Pg.BeginTransactionAsync();
        await using var tx2 = await Pg.BeginTransactionAsync();

        var first = await Pg.Query<PgAdmRow>(tx1).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();
        var second = await Pg.Query<PgAdmRow>(tx2).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked).ToListAsync();

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal("j1", Assert.Single(first.Value!).Sku);
        Assert.Equal("j2", Assert.Single(second.Value!).Sku);

        await tx1.RollbackAsync();
        await tx2.RollbackAsync();
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task NoWait_fails_fast_on_a_locked_row()
    {
        await FreshAsync(new PgAdmRow { Sku = "j1" });

        await using var tx1 = await Pg.BeginTransactionAsync();
        await using var tx2 = await Pg.BeginTransactionAsync();

        var held = await Pg.Query<PgAdmRow>(tx1).ForUpdate().FirstOrDefaultAsync();
        Assert.True(held.IsSuccess, held.Error?.ToString());

        var blocked = await Pg.Query<PgAdmRow>(tx2).ForShare(LockWait.NoWait).AnyAsync();
        Assert.True(blocked.IsFailure);

        await tx1.RollbackAsync();
        await tx2.RollbackAsync();
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task ForUpdate_outside_a_transaction_fails()
    {
        await FreshAsync(new PgAdmRow { Sku = "a" });
        var r = await Pg.Query<PgAdmRow>().ForUpdate().ToListAsync();
        Assert.True(r.IsFailure);
        Assert.Contains("transaction", r.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DbInfoRow
    {
        public string Encoding { get; set; } = "";
        public string Collate { get; set; } = "";
    }
}
