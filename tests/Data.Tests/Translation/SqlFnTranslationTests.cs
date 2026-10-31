using System.Linq.Expressions;
using CL.Data;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using Data.Tests.Providers;

namespace Data.Tests.Translation;

public sealed class SqlFnTranslationTests
{
    private static readonly SqlDialect Dialect = new FakeDialect();

    private static SqlExpr Tree(Expression<Func<Account, bool>> predicate)
    {
        var map = EntityAccessorRegistry.Get<Account>().ValueOrThrow().Map;
        var scope = new TranslationScope(Dialect, NamingPolicy.AsIs).Bind(predicate.Parameters[0], map);
        return SqlTranslator.TranslatePredicate(predicate, scope).ValueOrThrow();
    }

    private static string Sql(Expression<Func<Account, bool>> predicate) => SqlWriter.RenderExpression(Tree(predicate), Dialect).Text;

    [Fact]
    public void Functions_translate_in_where()
    {
        Assert.Equal("EXTRACT(MONTH FROM \"CreatedAt\") = 3", Sql(a => SqlFn.Month(a.CreatedAt) == 3));
        Assert.Equal("COALESCE(\"Nick\", \"Name\", @p0) = @p1", Sql(a => SqlFn.Coalesce(a.Nick, a.Name, "?") == "x"));
        Assert.Equal("GREATEST(\"Score\", 0) > 5", Sql(a => SqlFn.Greatest(a.Score, 0m) > 5));
        Assert.Equal("ROUND(\"Score\", 2) = 1.5", Sql(a => SqlFn.Round(a.Score, 2) == 1.5m));
        Assert.Equal("(UPPER(\"Name\")) || (@p0) = @p1", Sql(a => SqlFn.Concat(SqlFn.Upper(a.Name), "!") == "ANN!"));
        Assert.Equal("\"Name\" LIKE @p0 ESCAPE '\\'", Sql(a => SqlFn.Like(a.Name, "a_%")));
    }

    [Fact]
    public void Json_and_full_text_become_neutral_function_nodes()
    {
        var value = Assert.IsType<BinaryExpr>(Tree(a => SqlFn.Json.Value<string>(a.Nick, "$.city") == "Oslo"));
        Assert.Equal(SqlFunction.JsonValue, Assert.IsType<FunctionExpr>(value.Left).Function);

        var exists = Assert.IsType<FunctionExpr>(Tree(a => SqlFn.Json.Exists(a.Nick, "$.tags")));
        Assert.Equal(SqlFunction.JsonExists, exists.Function);               // a predicate node: no "= TRUE"

        var match = Assert.IsType<FunctionExpr>(Tree(a => SqlFn.FullText.Match(a.Name, "cat dog")));
        Assert.Equal(SqlFunction.FullTextMatch, match.Function);
        Assert.Throws<SqlRenderException>(() => SqlWriter.RenderExpression(match, Dialect));   // ANSI has no full-text; providers render it
    }

    [Fact]
    public void Calling_outside_a_query_throws() =>
        Assert.Throws<InvalidOperationException>(() => SqlFn.Lower("x"));
}
