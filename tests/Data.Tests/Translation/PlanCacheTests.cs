using System.Linq.Expressions;
using CL.Data;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CodeLogic.Core.Results;
using Data.Tests.Providers;

namespace Data.Tests.Translation;

/// <summary>
/// The plan cache must be invisible: a cached translation renders exactly the SQL and parameters a fresh
/// translation renders, for every captured value. Each case is translated through the cache (which records the
/// shape on first use) and directly through the translator, with captured values that change between runs.
/// </summary>
public sealed class PlanCacheTests
{
    private static readonly SqlDialect Dialect = new FakeDialect();

    private static TranslationScope Scope(LambdaExpression predicate)
    {
        var map = EntityAccessorRegistry.Get<Account>().ValueOrThrow().Map;
        return new TranslationScope(Dialect, NamingPolicy.AsIs).Bind(predicate.Parameters[0], map);
    }

    private static (string Sql, object?[] Values) Render(SqlExpr expr)
    {
        var rendered = SqlWriter.RenderExpression(expr, Dialect);
        return (rendered.Text, rendered.Parameters.Select(p => p.BoxedValue).ToArray());
    }

    private static (string Sql, object?[] Values) Cached(Expression<Func<Account, bool>> predicate) =>
        Render(SqlTranslator.TranslatePredicate(predicate, Scope(predicate)).ValueOrThrow());

    private static (string Sql, object?[] Values) Fresh(Expression<Func<Account, bool>> predicate) =>
        Render(new ExpressionTranslator(Scope(predicate)).Predicate(predicate.Body));

    private static void Same(Expression<Func<Account, bool>> predicate)
    {
        var fresh = Fresh(predicate);
        var cached = Cached(predicate);
        Assert.Equal(fresh.Sql, cached.Sql);
        Assert.Equal(fresh.Values, cached.Values);
    }

    // One call site per shape: the lambda below is the same expression shape on every iteration.
    private static Expression<Func<Account, bool>> ByName(string? name) => a => a.Name == name;
    private static Expression<Func<Account, bool>> ByNick(string? nick) => a => a.Nick == nick;
    private static Expression<Func<Account, bool>> InIds(IEnumerable<long> ids) => a => ids.Contains(a.Id);
    private static Expression<Func<Account, bool>> InParents(int?[] parents) => a => parents.Contains(a.ParentId);
    private static Expression<Func<Account, bool>> NameStarts(string? prefix) => a => a.Name.StartsWith(prefix!);
    private static Expression<Func<Account, bool>> InTiers(Tier[] tiers) => a => tiers.Contains(a.Tier);
    private static Expression<Func<Account, bool>> Since(DateTimeOffset at, decimal min) => a => a.CreatedAt >= at && a.Score > min * 2;
    private static Expression<Func<Account, bool>> Combined(string name, long[] ids, bool flag) =>
        a => (a.Name == name || ids.Contains(a.Id)) && a.Active == flag;

    [Fact]
    public void Cached_translations_match_fresh_ones_for_changing_values()
    {
        var hitsBefore = QueryPlanCache.Hits;
        foreach (var name in new[] { "Ann", "Bob", null, "Cy", null, "O'Brien_%" })
        {
            Same(ByName(name));
            Same(ByNick(name));
            Same(NameStarts(name));
        }
        foreach (var ids in new long[][] { [1, 2], [3, 4], [], [5], [6, 7], Enumerable.Range(1, 2500).Select(i => (long)i).ToArray() })
            Same(InIds(ids));
        Same(InIds(new List<long> { 9, 10 }));
        Same(InIds(new HashSet<long> { 11, 12 }));
        foreach (var parents in new int?[][] { [1, null], [2, 3], [null], [4, null], [] })
            Same(InParents(parents));
        foreach (var tiers in new Tier[][] { [Tier.Pro], [Tier.Free], [Tier.Free, Tier.Pro] })
            Same(InTiers(tiers));
        for (var i = 0; i < 4; i++)
        {
            Same(Since(DateTimeOffset.UnixEpoch.AddDays(i), i * 1.5m));
            Same(Combined("n" + i, [i, i + 1], i % 2 == 0));
        }
        // Most of the 40 cached calls above must have been bound from a plan, not translated.
        Assert.True(QueryPlanCache.Hits - hitsBefore >= 25, $"only {QueryPlanCache.Hits - hitsBefore} hits");
    }

    [Fact]
    public void Same_shape_with_new_values_is_served_from_the_cache()
    {
        var first = Cached(ByName("warm-up " + Guid.NewGuid()));   // records the shape (if no other test did)
        var hitsBefore = QueryPlanCache.Hits;
        var (sql, values) = Cached(ByName("second"));
        Assert.True(QueryPlanCache.Hits > hitsBefore);
        Assert.Equal(first.Sql, sql);
        Assert.Equal(["second"], values);
    }

    [Fact]
    public void Values_computed_at_call_time_are_computed_again_on_a_hit()
    {
        // DateTimeOffset.UtcNow is read on every translation, cached or not.
        Expression<Func<Account, bool>> Recent() => a => a.CreatedAt > DateTimeOffset.UtcNow.AddDays(-1);
        var first = Cached(Recent());
        Thread.Sleep(5);
        var second = Cached(Recent());
        Assert.Equal(first.Sql, second.Sql);
        Assert.NotEqual(first.Values[0], second.Values[0]);
    }

    [Fact]
    public void Different_literal_constants_are_different_shapes()
    {
        var (adults, _) = Cached(a => a.Score > 18);
        var (seniors, _) = Cached(a => a.Score > 65);
        Assert.Contains("18", adults);
        Assert.Contains("65", seniors);
    }

    [Fact]
    public void Failures_are_reported_on_every_call()
    {
        string? name = null;
        Expression<Func<Account, bool>> Broken() => a => a.Name == name!.ToUpperInvariant();
        var scope = Scope(Broken());
        Assert.True(SqlTranslator.TranslatePredicate(Broken(), scope).IsFailure);
        Assert.True(SqlTranslator.TranslatePredicate(Broken(), Scope(Broken())).IsFailure);
    }
}
