using System.Linq.Expressions;
using CL.SQLite;
using CL.SQLite.Models;
using CL.SQLite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SQLite.Tests;

public enum XStatus { Pending = 0, Active = 2, Closed = 7 }

[SQLiteTable("x_row")]
public sealed class XRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "tag", DataType = SQLiteDataType.TEXT, IsNotNull = true)]
    public string Tag { get; set; } = "";

    [SQLiteColumn(ColumnName = "counter_col", DataType = SQLiteDataType.INTEGER)]
    public int Counter { get; set; }

    [SQLiteColumn(ColumnName = "stamp", DataType = SQLiteDataType.DATETIME)]
    public DateTime Stamp { get; set; }

    [SQLiteColumn(ColumnName = "status", DataType = SQLiteDataType.INTEGER)]
    public XStatus Status { get; set; }

    [SQLiteColumn(ColumnName = "note", DataType = SQLiteDataType.TEXT)]
    public string? Note { get; set; }

    [SQLiteColumn(ColumnName = "score", DataType = SQLiteDataType.REAL)]
    public double Score { get; set; }

    public string Unmapped { get; set; } = "";
}

[SQLiteTable("x_upsert")]
public sealed class XUpsertRow
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "key_col", DataType = SQLiteDataType.TEXT)]
    public string Key { get; set; } = "";

    [SQLiteColumn(ColumnName = "hits", DataType = SQLiteDataType.INTEGER)]
    public int Hits { get; set; }

    [SQLiteColumn(ColumnName = "note", DataType = SQLiteDataType.TEXT)]
    public string? Note { get; set; }
}

/// <summary>Auto-increment key plus one unique column: the unique column is the inferred target.</summary>
[SQLiteTable("x_upsert_unique")]
public sealed class XUpsertUniqueRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "email", DataType = SQLiteDataType.TEXT, IsUnique = true)]
    public string Email { get; set; } = "";

    [SQLiteColumn(ColumnName = "logins", DataType = SQLiteDataType.INTEGER)]
    public int Logins { get; set; }
}

[SQLiteTable("x_admin")]
[SQLiteIndex("name", IsUnique = true)]
public sealed class XAdminRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "name", DataType = SQLiteDataType.TEXT)]
    public string Name { get; set; } = "";
}

/// <summary>
/// Offline tests of <see cref="SQLiteColumnExpressionTranslator"/>: the SQL text it emits and
/// the parameters it binds. No database.
/// </summary>
public sealed class ColumnExpressionTranslatorTests
{
    private static (string Sql, IReadOnlyDictionary<string, object?> Params) Translate<TResult>(
        Expression<Func<XRow, TResult>> expr)
    {
        var sink = new SQLiteParameterSink();
        var (sql, _) = SQLiteColumnExpressionTranslator.Translate(expr.Body, expr.Parameters[0], sink);
        return (sql, sink.Parameters);
    }

    [Fact]
    public void Hostile_string_is_a_parameter_never_sql_text()
    {
        var hostile = "x\\'); DROP TABLE x_row; --\"";
        var (sql, ps) = Translate(t => t.Counter > 0 ? hostile : t.Note);

        Assert.DoesNotContain("DROP", sql);
        Assert.DoesNotContain("'", sql);
        Assert.Equal("(CASE WHEN (\"counter_col\" > 0) THEN @ex_0 ELSE \"note\" END)", sql);
        Assert.Equal(hostile, ps["@ex_0"]);
    }

    [Fact]
    public void Numerics_are_inline_with_invariant_culture()
    {
        var factor = 1.5;
        var step = -3;
        var (sql, ps) = Translate(t => t.Score * factor + step);
        Assert.Equal("((\"score\" * 1.5) + (-3))", sql);
        Assert.Empty(ps);
    }

    [Fact]
    public void Enum_binds_its_underlying_number()
    {
        var status = XStatus.Active;
        var (sql, ps) = Translate(t => t.Status == status ? 1 : 0);
        var param = Assert.Single(ps);
        Assert.Contains(param.Key, sql);
        Assert.Equal(2L, param.Value);
    }

    [Fact]
    public void DateTime_binds_as_stored_text_with_milliseconds()
    {
        var when = new DateTime(2026, 10, 26, 13, 14, 15, 678);
        var (_, ps) = Translate(t => t.Counter > 0 ? when : t.Stamp);
        Assert.Equal("2026-10-26 13:14:15.678", Assert.Single(ps).Value);
    }

    [Fact]
    public void Guid_and_bool_bind_like_the_repository()
    {
        var id = Guid.NewGuid();
        var flag = true;
        var (_, ps) = Translate(t => t.Counter > 0 ? (object)id : flag);
        Assert.Equal(id.ToString(), ps["@ex_0"]);
        Assert.Equal(1, ps["@ex_1"]);
    }

    [Fact]
    public void Equal_values_share_one_parameter()
    {
        var s = "same";
        var (sql, ps) = Translate(t => t.Note == s ? s : t.Tag);
        Assert.Single(ps);
        Assert.Equal(2, sql.Split("@ex_0").Length - 1);
    }

    [Fact]
    public void Math_min_and_max_become_scalar_min_and_max()
    {
        var cap = 10;
        Assert.Equal("min(10, (\"counter_col\" + 1))", Translate(t => Math.Min(cap, t.Counter + 1)).Sql);
        Assert.Equal("max(\"counter_col\", 0)", Translate(t => Math.Max(t.Counter, 0)).Sql);
    }

    [Fact]
    public void Null_comparison_uses_is()
    {
        Assert.Equal("(CASE WHEN (\"note\" IS NULL) THEN 1 ELSE 0 END)", Translate(t => t.Note == null ? 1 : 0).Sql);
    }

    [Fact]
    public void Unmapped_member_is_rejected()
    {
        Assert.Throws<NotSupportedException>(() => Translate(t => t.Unmapped));
    }

    [Fact]
    public void Column_renderers_apply_per_lambda_parameter()
    {
        Expression<Func<XUpsertRow, XUpsertRow, int>> e = (current, incoming) => current.Hits + incoming.Hits;
        var renderers = new Dictionary<ParameterExpression, Func<string, string>>
        {
            [e.Parameters[0]] = SQLiteColumnExpressionTranslator.Quote,
            [e.Parameters[1]] = c => $"excluded.{SQLiteColumnExpressionTranslator.Quote(c)}",
        };
        var (sql, _) = SQLiteColumnExpressionTranslator.Translate(e.Body, renderers, new SQLiteParameterSink());
        Assert.Equal("(\"hits\" + excluded.\"hits\")", sql);
    }
}

/// <summary>
/// <see cref="DatabaseAdmin"/> database-file operations, against a standalone
/// <see cref="ConnectionManager"/> and a throwaway directory (no CodeLogic runtime).
/// </summary>
public sealed class DatabaseAdminFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_admin_" + Guid.NewGuid().ToString("N"));

    public DatabaseAdminFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* Windows may hold the file briefly */ }
    }

    [Fact]
    public async Task Create_exists_drop_round_trip_with_relative_path()
    {
        using var cm = new ConnectionManager(null, _dir);
        var admin = new DatabaseAdmin(cm);
        var full = Path.Combine(_dir, "nested", "made.db");

        Assert.False((await admin.DatabaseExistsAsync("nested/made.db")).Value);

        var created = await admin.CreateDatabaseAsync("nested/made.db");
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.True(created.Value);
        Assert.True(File.Exists(full));
        Assert.True((await admin.DatabaseExistsAsync(full)).Value);

        // Already there: success/false by default, failure when ifNotExists is off.
        Assert.False((await admin.CreateDatabaseAsync("nested/made.db")).Value);
        Assert.True((await admin.CreateDatabaseAsync("nested/made.db", ifNotExists: false)).IsFailure);

        var dropped = await admin.DropDatabaseAsync("nested/made.db");
        Assert.True(dropped.IsSuccess, dropped.Error?.Message);
        Assert.True(dropped.Value);
        Assert.False(File.Exists(full));

        Assert.False((await admin.DropDatabaseAsync("nested/made.db")).Value);
        Assert.True((await admin.DropDatabaseAsync("nested/made.db", ifExists: false)).IsFailure);
    }

    [Fact]
    public async Task Dropping_the_configured_database_drains_the_pool_and_removes_wal_files()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = "live.db", UseWAL = true });
        var full = Path.Combine(_dir, "live.db");

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal(1, cm.GetPooledConnectionCount());
        Assert.True(File.Exists(full + "-wal"));

        var admin = new DatabaseAdmin(cm);
        var dropped = await admin.DropDatabaseAsync("live.db");
        Assert.True(dropped.IsSuccess, dropped.Error?.Message);
        Assert.Equal(0, cm.GetPooledConnectionCount());
        Assert.False(File.Exists(full));
        Assert.False(File.Exists(full + "-wal"));
        Assert.False(File.Exists(full + "-shm"));

        // The connection ID still works: the next query recreates an empty database.
        var tables = await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 't'";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        });
        Assert.Equal(0, tables);
        Assert.True(File.Exists(full));
    }
}

/// <summary>
/// Typed updates, UpdateAndGetAsync, AnyAsync, the expression upsert, locking stubs and
/// table administration against the shared runtime database.
/// </summary>
[Collection("codelogic")]
public sealed class AdminAndExpressionLiveTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public AdminAndExpressionLiveTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private async Task<string> SeedAsync(int count = 3)
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<XRow>()).IsSuccess);
        var repo = Lib.GetRepository<XRow>();
        var tag = "x" + Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < count; i++)
            Assert.True((await repo.InsertAsync(new XRow
            {
                Tag = tag, Counter = i, Stamp = new DateTime(2020, 1, 1), Status = XStatus.Pending, Score = i
            })).IsSuccess);
        return tag;
    }

    [Fact]
    public async Task Bundled_sqlite_supports_returning()
    {
        var version = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT sqlite_version()";
            return (string)(await cmd.ExecuteScalarAsync())!;
        });
        Assert.True(Version.Parse(version) >= new Version(3, 35), $"SQLite {version} has no RETURNING");
    }

    [Fact]
    public async Task Typed_update_writes_constants_captured_values_and_row_expressions()
    {
        var tag = await SeedAsync();
        var when = new DateTime(2026, 10, 26, 13, 14, 15, 678);
        var hostile = "it's a \\ \"test\"";
        var status = XStatus.Closed;

        var r = await Lib.GetQueryBuilder<XRow>()
            .Where(x => x.Tag == tag)
            .UpdateAsync(x => new XRow { Counter = x.Counter + 10, Stamp = when, Note = hostile, Status = status });
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal(3, r.Value);

        var rows = (await Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == tag).OrderBy(x => x.Counter).ToListAsync()).Value!;
        Assert.Equal([10, 11, 12], rows.Select(x => x.Counter));
        Assert.All(rows, x =>
        {
            Assert.Equal(when, x.Stamp);
            Assert.Equal(hostile, x.Note);
            Assert.Equal(XStatus.Closed, x.Status);
        });
    }

    [Fact]
    public async Task Typed_update_round_trips_datetime_milliseconds_through_the_translator()
    {
        var tag = await SeedAsync(1);
        var when = new DateTime(2031, 2, 3, 4, 5, 6, 789);

        var r = await Lib.GetQueryBuilder<XRow>()
            .Where(x => x.Tag == tag)
            .UpdateAsync(x => new XRow { Stamp = x.Counter >= 0 ? when : x.Stamp });
        Assert.True(r.IsSuccess, r.Error?.Message);

        var row = (await Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == tag).FirstOrDefaultAsync()).Value!;
        Assert.Equal(when, row.Stamp);
    }

    [Fact]
    public async Task Typed_update_rejects_auto_increment_and_unmapped_properties()
    {
        var tag = await SeedAsync(1);
        var q = Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == tag);
        Assert.True((await q.UpdateAsync(x => new XRow { Id = 5 })).IsFailure);
        Assert.True((await q.UpdateAsync(x => new XRow { Unmapped = "x" })).IsFailure);
    }

    [Fact]
    public async Task UpdateAndGet_clamps_with_math_min_and_returns_the_new_rows()
    {
        var tag = await SeedAsync(3);
        var cap = 2;

        var r = await Lib.GetQueryBuilder<XRow>()
            .Where(x => x.Tag == tag)
            .UpdateAndGetAsync(x => new XRow { Counter = Math.Min(cap, x.Counter + 2) });
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.Equal([2, 2, 2], r.Value!.Select(x => x.Counter));
        Assert.All(r.Value!, x => Assert.Equal(tag, x.Tag));
    }

    [Fact]
    public async Task UpdateAndGet_with_order_and_take_updates_only_those_rows()
    {
        var tag = await SeedAsync(3);

        var r = await Lib.GetQueryBuilder<XRow>()
            .Where(x => x.Tag == tag)
            .OrderByDescending(x => x.Counter)
            .Take(1)
            .UpdateAndGetAsync(x => new XRow { Note = "claimed" });
        Assert.True(r.IsSuccess, r.Error?.Message);
        var claimed = Assert.Single(r.Value!);
        Assert.Equal(2, claimed.Counter);
        Assert.Equal("claimed", claimed.Note);

        var count = await Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == tag && x.Note == "claimed").CountAsync();
        Assert.Equal(1, count.Value);
    }

    [Fact]
    public async Task AnyAsync_reports_true_and_false()
    {
        var tag = await SeedAsync(2);
        Assert.True((await Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == tag).AnyAsync()).Value);
        Assert.True((await Lib.GetQueryBuilder<XRow>().AnyAsync(x => x.Tag == tag && x.Counter == 1)).Value);
        Assert.False((await Lib.GetQueryBuilder<XRow>().AnyAsync(x => x.Tag == tag && x.Counter == 99)).Value);
        Assert.False((await Lib.GetQueryBuilder<XRow>().Where(x => x.Tag == "nope-" + tag).AnyAsync()).Value);
    }

    [Fact]
    public void ForUpdate_and_ForShare_throw_not_supported()
    {
        var q = Lib.GetQueryBuilder<XRow>();
        var ex = Assert.Throws<NotSupportedException>(() => q.ForUpdate());
        Assert.Contains("UpdateAndGetAsync", ex.Message);
        Assert.Throws<NotSupportedException>(() => q.ForShare(LockWait.SkipLocked));
    }

    [Fact]
    public async Task Expression_upsert_inserts_then_updates_from_current_and_incoming()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<XUpsertRow>()).IsSuccess);
        var repo = Lib.GetRepository<XUpsertRow>();
        var key = "k" + Guid.NewGuid().ToString("N")[..10];

        var first = await repo.UpsertAsync(
            new XUpsertRow { Key = key, Hits = 1, Note = "first" },
            (current, incoming) => new XUpsertRow { Hits = current.Hits + 1, Note = incoming.Note });
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(1, first.Value);
        Assert.Equal(1, (await repo.GetByIdAsync(key)).Value!.Hits);

        var second = await repo.UpsertAsync(
            new XUpsertRow { Key = key, Hits = 1, Note = "second's \\ note" },
            (current, incoming) => new XUpsertRow { Hits = current.Hits + 1, Note = incoming.Note });
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(1, second.Value);

        var row = (await repo.GetByIdAsync(key)).Value!;
        Assert.Equal(2, row.Hits);
        Assert.Equal("second's \\ note", row.Note);
        Assert.Equal(1, (await Lib.GetQueryBuilder<XUpsertRow>().Where(x => x.Key == key).CountAsync()).Value);
    }

    [Fact]
    public async Task Expression_upsert_infers_a_unique_column_behind_an_auto_increment_key()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<XUpsertUniqueRow>()).IsSuccess);
        var repo = Lib.GetRepository<XUpsertUniqueRow>();
        var email = Guid.NewGuid().ToString("N")[..10] + "@example.test";
        var bump = 5;

        for (var i = 0; i < 2; i++)
        {
            var r = await repo.UpsertAsync(
                new XUpsertUniqueRow { Email = email, Logins = 1 },
                (current, _) => new XUpsertUniqueRow { Logins = current.Logins + bump });
            Assert.True(r.IsSuccess, r.Error?.Message);
        }

        var rows = (await Lib.GetQueryBuilder<XUpsertUniqueRow>().Where(x => x.Email == email).ToListAsync()).Value!;
        Assert.Equal(6, Assert.Single(rows).Logins);
    }

    [Fact]
    public async Task Table_admin_create_exists_truncate_drop_round_trip()
    {
        await Lib.DropTableAsync<XAdminRow>();
        Assert.False((await Lib.TableExistsAsync<XAdminRow>()).Value);

        var created = await Lib.CreateTableAsync<XAdminRow>();
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.True(created.Value);
        Assert.True((await Lib.TableExistsAsync<XAdminRow>()).Value);
        Assert.True((await Lib.TableExistsAsync("x_admin")).Value);
        Assert.False((await Lib.CreateTableAsync<XAdminRow>()).Value);
        Assert.True((await Lib.CreateTableAsync<XAdminRow>(ifNotExists: false)).IsFailure);

        // The declared unique index was created with the table.
        var repo = Lib.GetRepository<XAdminRow>();
        Assert.True((await repo.InsertAsync(new XAdminRow { Name = "a" })).IsSuccess);
        Assert.True((await repo.InsertAsync(new XAdminRow { Name = "b" })).IsSuccess);
        Assert.True((await repo.InsertAsync(new XAdminRow { Name = "a" })).IsFailure);

        var truncated = await Lib.TruncateTableAsync<XAdminRow>();
        Assert.True(truncated.IsSuccess, truncated.Error?.Message);
        Assert.Equal(0, (await repo.CountAsync()).Value);

        // AUTOINCREMENT restarts at 1.
        var fresh = new XAdminRow { Name = "c" };
        Assert.True((await repo.InsertAsync(fresh)).IsSuccess);
        Assert.Equal(1, fresh.Id);

        var dropped = await Lib.DropTableAsync<XAdminRow>();
        Assert.True(dropped.IsSuccess, dropped.Error?.Message);
        Assert.True(dropped.Value);
        Assert.False((await Lib.TableExistsAsync<XAdminRow>()).Value);
        Assert.False((await Lib.DropTableAsync<XAdminRow>()).Value);
        Assert.True((await Lib.DropTableAsync<XAdminRow>(ifExists: false)).IsFailure);
    }
}
