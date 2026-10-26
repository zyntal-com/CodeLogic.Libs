using System.Linq.Expressions;
using CL.PostgreSQL.Core;
using Xunit;

namespace PostgreSQL.Tests;

// Column expressions (UPDATE … SET with row references, grouped projections and keys)
// used to splice captured values into the SQL text, escaping only single quotes. These
// pin the replacement: every non-numeric captured value is a parameter.

public sealed class ExpressionParameterTests
{
    public enum Tier { Free = 0, Pro = 7 }

    public sealed class Row
    {
        public long Id { get; set; }
        public int Tokens { get; set; }
        public string? Name { get; set; }
        public DateTime SeenUtc { get; set; }
        public Tier Tier { get; set; }
    }

    private static (string Sql, SqlParameterSink Sink) Translate(Expression<Func<Row, object?>> selector)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : selector.Body;
        var sink = new SqlParameterSink();
        var (sql, _) = SqlExpressionTranslator.Translate(body, selector.Parameters[0], null, null, sink);
        return (sql, sink);
    }

    [Fact]
    public void Captured_string_is_a_parameter_not_a_literal()
    {
        var hostile = "x\\' OR 1=1 -- ";
        var (sql, sink) = Translate(r => SqlFn.Concat(r.Name!, hostile));

        Assert.DoesNotContain("OR 1=1", sql);
        Assert.DoesNotContain("'", sql);
        var p = Assert.Single(sink.Parameters);
        Assert.Contains(p.Key, sql);
        Assert.Equal(hostile, p.Value);
    }

    [Fact]
    public void Captured_datetime_keeps_fractional_seconds()
    {
        var at = new DateTime(2026, 10, 26, 12, 34, 56, 789, DateTimeKind.Utc);
        var (sql, sink) = Translate(r => r.SeenUtc > at ? r.SeenUtc : at);

        Assert.DoesNotContain("2026", sql);
        var value = Assert.IsType<DateTime>(Assert.Single(sink.Parameters).Value);
        Assert.Equal(at.Ticks, value.Ticks);
    }

    [Fact]
    public void Captured_enum_binds_its_underlying_value()
    {
        var tier = Tier.Pro;
        var (sql, sink) = Translate(r => r.Tier == tier ? 1 : 0);

        Assert.DoesNotContain("Pro", sql);
        Assert.Equal(7, Convert.ToInt32(Assert.Single(sink.Parameters).Value));
    }

    [Fact]
    public void Primitive_numbers_stay_inline()
    {
        var step = 5;
        var rate = 1.5;
        var (sql, sink) = Translate(r => r.Tokens * rate + step);

        Assert.Empty(sink.Parameters);
        Assert.Contains("1.5", sql);
        Assert.Contains("5", sql);
    }

    [Fact]
    public void Equal_values_share_one_parameter()
    {
        var name = "same";
        var (sql, sink) = Translate(r => r.Name == name ? name : r.Name);

        var p = Assert.Single(sink.Parameters);
        Assert.Equal(2, sql.Split(p.Key).Length - 1);
    }

    [Fact]
    public void Math_min_and_max_translate()
    {
        var cap = 100;
        Assert.Equal("LEAST(100, (\"Tokens\" + 3))", Translate(r => Math.Min(cap, r.Tokens + 3)).Sql);
        Assert.Equal("GREATEST(\"Tokens\", 0)", Translate(r => Math.Max(r.Tokens, 0)).Sql);
    }

    [Fact]
    public void Per_parameter_column_renderers_apply()
    {
        Expression<Func<Row, Row, int>> e = (cur, inc) => cur.Tokens + inc.Tokens;
        var renderers = new Dictionary<ParameterExpression, Func<string, string>>
        {
            [e.Parameters[0]] = PostgreSqlDialect.Quote,
            [e.Parameters[1]] = c => $"VALUES({PostgreSqlDialect.Quote(c)})",
        };
        var (sql, _) = SqlExpressionTranslator.Translate(e.Body, renderers, new SqlParameterSink());
        Assert.Equal("(\"Tokens\" + VALUES(\"Tokens\"))", sql);
    }
}
