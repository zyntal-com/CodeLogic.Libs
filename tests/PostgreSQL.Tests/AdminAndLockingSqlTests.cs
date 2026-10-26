using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

// The SQL shapes of row locking, AnyAsync, UpdateAndGetAsync and the expression upsert,
// verified without a database.

[Table(Name = "sql_job", Schema = "app")]
public sealed class SqlJobRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "queue", Size = 40, Unique = true, NotNull = true)] public string Queue { get; set; } = "";
    [Column(Name = "tokens", NotNull = true)] public int Tokens { get; set; }
    [Column(Name = "note", Size = 80)] public string? Note { get; set; }
}

[Table(Name = "sql_nokey", Schema = "app")]
public sealed class SqlNoKeyRow
{
    [Column(Name = "label", Size = 40)] public string? Label { get; set; }
    [Column(Name = "n")] public int N { get; set; }
}

// A column whose insert parameter (@ups_0) would collide with the setter's default prefix.
[Table(Name = "sql_clash", Schema = "app")]
public sealed class SqlClashRow
{
    [Column(Name = "k", Primary = true, Size = 10)] public string K { get; set; } = "";
    [Column(Name = "ups_0")] public int Ups0 { get; set; }
}

public sealed class AdminAndLockingSqlTests
{
    private static QueryBuilder<T> Q<T>() where T : class, new() => new(new ConnectionManager());

    [Theory]
    [InlineData(true, LockWait.Wait, " LIMIT 5 OFFSET 10 FOR UPDATE")]
    [InlineData(true, LockWait.NoWait, " LIMIT 5 OFFSET 10 FOR UPDATE NOWAIT")]
    [InlineData(true, LockWait.SkipLocked, " LIMIT 5 OFFSET 10 FOR UPDATE SKIP LOCKED")]
    [InlineData(false, LockWait.Wait, " LIMIT 5 OFFSET 10 FOR SHARE")]
    [InlineData(false, LockWait.NoWait, " LIMIT 5 OFFSET 10 FOR SHARE NOWAIT")]
    [InlineData(false, LockWait.SkipLocked, " LIMIT 5 OFFSET 10 FOR SHARE SKIP LOCKED")]
    public void Lock_clause_follows_limit_and_offset(bool exclusive, LockWait wait, string expectedTail)
    {
        var q = Q<SqlJobRow>().Take(5).Skip(10);
        q = exclusive ? q.ForUpdate(wait) : q.ForShare(wait);
        var (sql, _) = q.BuildSelectSql();
        Assert.EndsWith(expectedTail, sql);
    }

    [Fact]
    public void Any_selects_one_row_with_the_lock_suffix()
    {
        var (sql, parms) = Q<SqlJobRow>().Where(x => x.Tokens > 3).ForUpdate(LockWait.SkipLocked).BuildAnySql();
        Assert.StartsWith("SELECT 1 FROM \"app\".\"sql_job\" WHERE", sql);
        Assert.EndsWith(" LIMIT 1 FOR UPDATE SKIP LOCKED", sql);
        Assert.Single(parms);
    }

    [Fact]
    public async Task Locking_read_outside_a_transaction_is_rejected_before_any_connection()
    {
        var r = await Q<SqlJobRow>().ForUpdate().ToListAsync();
        Assert.True(r.IsFailure);
        Assert.Contains("transaction", r.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lock_is_refused_by_non_locking_terminals()
    {
        var r = await Q<SqlJobRow>().ForUpdate().CountAsync();
        Assert.True(r.IsFailure);
        Assert.Contains("not supported by CountAsync", r.Error!.Message);
    }

    [Fact]
    public void UpdateAndGet_without_order_or_take_is_a_plain_update_returning()
    {
        var cap = 10;
        var (sql, parms) = Q<SqlJobRow>().Where(x => x.Queue == "q")
            .BuildUpdateAndGetSql(x => new SqlJobRow { Tokens = Math.Min(cap, x.Tokens + 1) });

        Assert.StartsWith("UPDATE \"app\".\"sql_job\" SET \"tokens\" = LEAST(", sql);
        Assert.Contains(" WHERE ", sql);
        Assert.EndsWith(" RETURNING *", sql);
        Assert.DoesNotContain("FOR UPDATE", sql);
        Assert.Single(parms); // the WHERE value; the captured int stays inline
    }

    [Fact]
    public void UpdateAndGet_with_order_and_take_picks_keys_in_a_locking_subquery()
    {
        var (sql, _) = Q<SqlJobRow>().Where(x => x.Note == null).OrderBy(x => x.Id).Take(1)
            .ForUpdate(LockWait.SkipLocked)
            .BuildUpdateAndGetSql(x => new SqlJobRow { Note = "claimed" });

        Assert.StartsWith(
            "WITH updated AS (UPDATE \"app\".\"sql_job\" SET \"note\" = @upd_0 WHERE (\"id\") IN " +
            "(SELECT \"id\" FROM \"app\".\"sql_job\" WHERE ", sql);
        Assert.Contains(" ORDER BY \"id\" ASC LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING *)", sql);
        Assert.EndsWith("SELECT * FROM updated ORDER BY \"id\" ASC", sql);
    }

    [Fact]
    public void UpdateAndGet_share_lock_is_upgraded_to_for_update()
    {
        var (sql, _) = Q<SqlJobRow>().Take(3).ForShare(LockWait.NoWait)
            .BuildUpdateAndGetSql(x => new SqlJobRow { Tokens = 0 });
        Assert.Contains("LIMIT 3 FOR UPDATE NOWAIT)", sql);
        Assert.DoesNotContain("FOR SHARE", sql);
    }

    [Fact]
    public void UpdateAndGet_by_key_needs_a_primary_key()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Q<SqlNoKeyRow>().Take(1).BuildUpdateAndGetSql(x => new SqlNoKeyRow { N = 1 }));
        // The plain shape does not.
        var (sql, _) = Q<SqlNoKeyRow>().BuildUpdateAndGetSql(x => new SqlNoKeyRow { N = 1 });
        Assert.EndsWith("RETURNING *", sql);
    }

    [Fact]
    public void UpdateAndGet_rejects_offset_and_group_by()
    {
        Assert.Throws<NotSupportedException>(() =>
            Q<SqlJobRow>().Skip(1).BuildUpdateAndGetSql(x => new SqlJobRow { Tokens = 0 }));
    }

    [Fact]
    public void Expression_upsert_renders_current_as_the_table_column_and_incoming_as_excluded()
    {
        var bonus = "x' --";
        var (sql, insertCols, sink) = Repository<SqlJobRow>.BuildExpressionUpsertSql(
            (cur, inc) => new SqlJobRow { Tokens = cur.Tokens + inc.Tokens, Note = bonus },
            null, "Default");

        Assert.StartsWith("INSERT INTO \"app\".\"sql_job\" (\"queue\", \"tokens\", \"note\") VALUES (@queue, @tokens, @note)", sql);
        Assert.Contains("ON CONFLICT (\"queue\") DO UPDATE SET ", sql);
        Assert.Contains("\"tokens\" = (\"app\".\"sql_job\".\"tokens\" + EXCLUDED.\"tokens\")", sql);
        Assert.Contains("\"note\" = @ups_0", sql);
        Assert.DoesNotContain("x'", sql);
        Assert.Equal(3, insertCols.Length);
        Assert.Equal(bonus, Assert.Single(sink.Parameters).Value);
    }

    [Fact]
    public void Expression_upsert_parameters_never_collide_with_insert_parameters()
    {
        var v = 7;
        var (sql, insertCols, sink) = Repository<SqlClashRow>.BuildExpressionUpsertSql(
            (cur, inc) => new SqlClashRow { Ups0 = v }, null, "Default");

        var setterParam = Assert.Single(sink.Parameters).Key;
        Assert.DoesNotContain(insertCols, c => "@" + c.ColumnName == setterParam);
        Assert.Contains($"\"ups_0\" = {setterParam}", sql);
    }

    [Fact]
    public void Expression_upsert_requires_a_member_init_setter()
    {
        Assert.Throws<ArgumentException>(() =>
            Repository<SqlJobRow>.BuildExpressionUpsertSql((cur, inc) => inc, null, "Default"));
    }
}
