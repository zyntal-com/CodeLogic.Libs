using System.Linq.Expressions;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// Verifies the SQL the expression translator emits, without a database. This is where
// MySQL constructs are most likely to have survived the port, because the translator is
// the one place that writes operators and function calls rather than identifiers.

public sealed class ExpressionTranslationTests
{
    private static (string Sql, Dictionary<string, object?> Parms) Translate(
        Expression<Func<PlainRow, bool>> predicate) =>
        PostgreSqlExpressionVisitor.Translate(predicate);

    [Fact]
    public void Identifiers_are_double_quoted()
    {
        var (sql, _) = Translate(x => x.Label == "a");
        Assert.Contains("\"label\"", sql);
        Assert.DoesNotContain("`", sql);
    }

    [Fact]
    public void Null_comparison_becomes_is_null()
    {
        var (sql, _) = Translate(x => x.Label == null);
        Assert.Contains("IS NULL", sql);
        Assert.DoesNotContain("= @", sql);
    }

    [Fact]
    public void Not_null_comparison_becomes_is_not_null()
    {
        var (sql, _) = Translate(x => x.Label != null);
        Assert.Contains("IS NOT NULL", sql);
    }

    [Fact]
    public void Contains_becomes_parameterised_like()
    {
        var (sql, parms) = Translate(x => x.Label.Contains("abc"));
        Assert.Contains("LIKE", sql);
        Assert.Contains("%abc%", parms.Values.Cast<object?>().Select(v => v?.ToString()));
    }

    [Fact]
    public void Like_wildcards_in_user_input_are_escaped()
    {
        // Without escaping, a '%' in the search term silently turns an anchored search
        // into a full scan (and changes the result set).
        var term = "100%";
        var (_, parms) = Translate(x => x.Label.StartsWith(term));
        // The user's '%' is escaped to a literal; the trailing '%' is the pattern's own
        // wildcard and must stay unescaped.
        Assert.Contains(@"100\%%", parms.Values.Cast<object?>().Select(v => v?.ToString()));
    }

    [Fact]
    public void Empty_in_list_emits_a_false_literal_not_invalid_sql()
    {
        // "IN ()" is a syntax error. An empty set matches nothing.
        var ids = Array.Empty<long>();
        var (sql, _) = Translate(x => ids.Contains(x.Id));
        Assert.Contains("FALSE", sql);
        Assert.DoesNotContain("IN ()", sql);
    }

    [Fact]
    public void Non_empty_in_list_parameterises_every_element()
    {
        var ids = new long[] { 1, 2, 3 };
        var (sql, parms) = Translate(x => ids.Contains(x.Id));
        Assert.Contains("IN (", sql);
        Assert.Equal(3, parms.Count);
    }

    [Fact]
    public void Values_are_parameterised_never_inlined()
    {
        var evil = "'; DROP TABLE plain; --";
        var (sql, parms) = Translate(x => x.Label == evil);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.Contains(evil, parms.Values.Cast<object?>().Select(v => v?.ToString()));
    }

    [Fact]
    public void And_or_compose_with_parentheses()
    {
        var (sql, _) = Translate(x => x.Id > 1 && (x.Label == "a" || x.Label == "b"));
        Assert.Contains(" AND ", sql);
        Assert.Contains(" OR ", sql);
    }

    [Fact]
    public void No_mysql_operators_survive()
    {
        var (sql, _) = Translate(x => x.Id > 1 && x.Label != null);
        foreach (var bad in new[] { "<=>", "IFNULL", "`" })
            Assert.DoesNotContain(bad, sql, StringComparison.OrdinalIgnoreCase);
    }
}

// ── Scalar function translation ──────────────────────────────────────────────────

public sealed class SqlFunctionTranslationTests
{
    private static string Sql(Expression<Func<PlainRow, object>> selector)
    {
        var body = selector.Body is UnaryExpression u ? u.Operand : selector.Body;
        return SqlExpressionTranslator.Translate(body, selector.Parameters[0], null, null, new SqlParameterSink()).Sql;
    }

    [Fact]
    public void Date_parts_use_extract_not_mysql_functions()
    {
        // MySQL: YEAR(d) / MONTH(d). PostgreSQL has no such functions.
        Assert.Contains("EXTRACT(YEAR FROM", Sql(x => SqlFn.Year(DateTime.UtcNow)));
        Assert.Contains("EXTRACT(MONTH FROM", Sql(x => SqlFn.Month(DateTime.UtcNow)));
        Assert.Contains("EXTRACT(DOW FROM", Sql(x => SqlFn.DayOfWeek(DateTime.UtcNow)));
    }

    [Fact]
    public void Day_of_week_is_not_offset()
    {
        // PostgreSQL's DOW is already 0..6 from Sunday, matching .NET. MySQL's DAYOFWEEK
        // is 1..7 and the MySQL translator subtracts one; doing that here would be wrong.
        var sql = Sql(x => SqlFn.DayOfWeek(DateTime.UtcNow));
        Assert.DoesNotContain("- 1", sql);
    }

    [Fact]
    public void IfNull_becomes_coalesce()
    {
        // PostgreSQL has no IFNULL.
        var sql = Sql(x => SqlFn.IfNull(x.Label, "fallback"));
        Assert.Contains("COALESCE(", sql);
        Assert.DoesNotContain("IFNULL", sql);
    }

    [Fact]
    public void Date_cast_replaces_the_date_function()
    {
        Assert.Contains("::date", Sql(x => SqlFn.Date(DateTime.UtcNow)));
    }

    [Fact]
    public void Bucket_uses_epoch_arithmetic()
    {
        // MySQL spelled this FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP(d)/n)*n).
        var sql = Sql(x => SqlFn.BucketUtc(DateTime.UtcNow, 300));
        Assert.Contains("to_timestamp(", sql);
        Assert.Contains("EXTRACT(EPOCH FROM", sql);
        Assert.DoesNotContain("UNIX_TIMESTAMP", sql);
        Assert.DoesNotContain("FROM_UNIXTIME", sql);
    }
}

// ── Cursor pagination ────────────────────────────────────────────────────────────

public sealed class CursorSqlTests
{
    [Fact]
    public void Seek_predicate_uses_the_standard_null_safe_operator()
    {
        var orders = new[]
        {
            new CursorOrder(EntityMetadata<PlainRow>.RequireColumn("label"), Descending: false),
            new CursorOrder(EntityMetadata<PlainRow>.RequireColumn("id"), Descending: false),
        };
        var (sql, parms) = CursorPagination.BuildSeekPredicate(orders, new object?[] { "a", 1L });

        // MySQL's <=> does not exist in PostgreSQL.
        Assert.Contains("IS NOT DISTINCT FROM", sql);
        Assert.DoesNotContain("<=>", sql);
        Assert.Equal(2, parms.Count);
    }

    [Fact]
    public void Token_round_trips()
    {
        var orders = new[]
        {
            new CursorOrder(EntityMetadata<PlainRow>.RequireColumn("id"), Descending: false),
        };
        var token = CursorTokenCodec.Encode(new PlainRow { Id = 42, Label = "x" }, orders);
        var values = CursorTokenCodec.Decode<PlainRow>(token, orders);

        Assert.Equal(42L, Assert.Single(values));
        // base64url: no characters that would need escaping in a URL.
        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
    }

    [Fact]
    public void Token_from_another_entity_is_rejected()
    {
        var plainOrders = new[]
        {
            new CursorOrder(EntityMetadata<PlainRow>.RequireColumn("id"), Descending: false),
        };
        var widgetOrders = new[]
        {
            new CursorOrder(EntityMetadata<DialectWidget>.RequireColumn("id"), Descending: false),
        };
        var token = CursorTokenCodec.Encode(new PlainRow { Id = 1 }, plainOrders);

        Assert.Throws<CursorPagingException>(() => CursorTokenCodec.Decode<DialectWidget>(token, widgetOrders));
    }
}
