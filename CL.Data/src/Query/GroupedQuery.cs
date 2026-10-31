using System.Linq.Expressions;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// A grouped query (<c>GROUP BY</c>). Filter groups with <see cref="Having"/>, sort with <see cref="OrderBy{TOrder}"/>,
/// and finish with <see cref="Select{TResult}"/>; group lambdas receive an <see cref="IDataGroup{TKey, TElement}"/>.
/// </summary>
/// <example>
/// <code>
/// var big = await db.Query&lt;Order&gt;().Where(o =&gt; o.Paid)
///     .GroupBy(o =&gt; o.CustomerId)
///     .Having(g =&gt; g.Sum(o =&gt; o.Total) &gt; 1000)
///     .OrderByDescending(g =&gt; g.Sum(o =&gt; o.Total))
///     .Select(g =&gt; new { Customer = g.Key, Total = g.Sum(o =&gt; o.Total), Orders = g.Count() })
///     .ToListAsync();
/// </code>
/// </example>
public sealed class GroupedQuery<T, TKey>
{
    private readonly QueryBuilder<T> _source;
    private readonly Expression<Func<T, TKey>> _key;
    private readonly IReadOnlyList<LambdaExpression> _having;
    private readonly IReadOnlyList<(LambdaExpression Key, bool Descending)> _order;
    private readonly int? _take;
    private readonly int? _skip;

    internal GroupedQuery(QueryBuilder<T> source, Expression<Func<T, TKey>> key)
        : this(source, key, [], [], null, null) { }

    private GroupedQuery(QueryBuilder<T> source, Expression<Func<T, TKey>> key, IReadOnlyList<LambdaExpression> having,
        IReadOnlyList<(LambdaExpression, bool)> order, int? take, int? skip)
    {
        _source = source;
        _key = key;
        _having = having;
        _order = order;
        _take = take;
        _skip = skip;
    }

    /// <summary>Keeps groups matching <paramref name="predicate"/> (<c>HAVING</c>).</summary>
    public GroupedQuery<T, TKey> Having(Expression<Func<IDataGroup<TKey, T>, bool>> predicate) =>
        new(_source, _key, [.. _having, predicate], _order, _take, _skip);

    /// <summary>Sorts groups ascending (replacing earlier sorts).</summary>
    public GroupedQuery<T, TKey> OrderBy<TOrder>(Expression<Func<IDataGroup<TKey, T>, TOrder>> key) =>
        new(_source, _key, _having, [(key, false)], _take, _skip);

    /// <summary>Sorts groups descending (replacing earlier sorts).</summary>
    public GroupedQuery<T, TKey> OrderByDescending<TOrder>(Expression<Func<IDataGroup<TKey, T>, TOrder>> key) =>
        new(_source, _key, _having, [(key, true)], _take, _skip);

    /// <summary>Adds a secondary ascending sort.</summary>
    public GroupedQuery<T, TKey> ThenBy<TOrder>(Expression<Func<IDataGroup<TKey, T>, TOrder>> key) =>
        new(_source, _key, _having, [.. _order, (key, false)], _take, _skip);

    /// <summary>Adds a secondary descending sort.</summary>
    public GroupedQuery<T, TKey> ThenByDescending<TOrder>(Expression<Func<IDataGroup<TKey, T>, TOrder>> key) =>
        new(_source, _key, _having, [.. _order, (key, true)], _take, _skip);

    /// <summary>Returns at most <paramref name="count"/> groups.</summary>
    public GroupedQuery<T, TKey> Take(int count) => new(_source, _key, _having, _order, count, _skip);

    /// <summary>Skips <paramref name="count"/> groups.</summary>
    public GroupedQuery<T, TKey> Skip(int count) => new(_source, _key, _having, _order, _take, count);

    /// <summary>Projects each group (its key and aggregates).</summary>
    public GroupProjection<TResult> Select<TResult>(Expression<Func<IDataGroup<TKey, T>, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new GroupProjection<TResult>((take, ct) => RunAsync<TResult>(selector, take, ct));
    }

    /// <summary>Builds and runs the grouped SELECT with <paramref name="selector"/> as its projection.</summary>
    internal async ValueTask<Result<List<TResult>>> RunAsync<TResult>(LambdaExpression selector, int? take, CancellationToken ct)
    {
        var plan = _source.PlanSelect();
        if (plan.IsFailure) return plan.Error!;
        var (select, accessor, table) = plan.Value!;
        var map = accessor.Map;
        var alias = select.From.Alias;

        // The key: one expression, or one per member of an anonymous type.
        var keyScope = new TranslationScope(_source.Database.Provider.Dialect, _source.Database.NamingPolicy).Bind(_key.Parameters[0], map, alias);
        SqlExpr? single = null;
        Dictionary<string, SqlExpr> members = [];
        List<SqlExpr> groupBy = [];
        if (_key.Body is NewExpression { Members: { } names } composite)
        {
            for (var i = 0; i < composite.Arguments.Count; i++)
            {
                var part = SqlTranslator.TranslateValue(composite.Arguments[i], keyScope);
                if (part.IsFailure) return part.Error!;
                var value = GroupKeys.AsValue(part.Value!);
                members[names[i].Name] = value;
                groupBy.Add(value);
            }
        }
        else
        {
            var key = SqlTranslator.TranslateValue(_key, keyScope);
            if (key.IsFailure) return key.Error!;
            single = GroupKeys.AsValue(key.Value!);
            groupBy.Add(single);
        }

        TranslationScope GroupScope(LambdaExpression lambda) =>
            new(_source.Database.Provider.Dialect, _source.Database.NamingPolicy)
            {
                Group = new GroupScope(lambda.Parameters[0], single, members, new ScopeTable(map, alias)),
            };

        SqlExpr? having = null;
        foreach (var predicate in _having)
        {
            var translated = SqlTranslator.TranslatePredicate(predicate, GroupScope(predicate));
            if (translated.IsFailure) return translated.Error!;
            having = ReadPlanner.And(having, translated.Value);
        }

        List<OrderItem> order = [];
        foreach (var (key, descending) in _order)
        {
            var translated = SqlTranslator.TranslateValue(key, GroupScope(key));
            if (translated.IsFailure) return translated.Error!;
            order.Add(new OrderItem(translated.Value!, descending));
        }
        if (order.Count == 0 && _skip is not null) order.AddRange(groupBy.Select(g => new OrderItem(g)));

        var projection = ProjectionPlan<TResult>.Create(selector, GroupScope(selector));
        if (projection.IsFailure) return projection.Error!;

        var limit = take ?? _take;
        var statement = select with
        {
            Projection = projection.Value!.Columns,
            GroupBy = groupBy,
            Having = having,
            OrderBy = order,
            Limit = limit is { } l ? new ParameterExpr(SqlParameterValue.Of((long)l)) : null,
            Offset = _skip is > 0 ? new ParameterExpr(SqlParameterValue.Of((long)_skip.Value)) : null,
        };
        return await _source.Database.QueryProjectedAsync(statement, projection.Value.Materialize, table, [DataTags.Table(table)], ct)
            .ConfigureAwait(false);
    }
}

internal static class GroupKeys
{
    /// <summary>A condition used as a group key becomes a CASE value (SQL Server cannot group by a predicate).</summary>
    public static SqlExpr AsValue(SqlExpr key) => SqlPredicates.IsPredicate(key) && key is not LiteralExpr
        ? new CaseExpr([new CaseWhen(key, new LiteralExpr(true))], new LiteralExpr(false))
        : key;
}

/// <summary>The projected results of a grouped query.</summary>
/// <typeparam name="TResult">Projected type.</typeparam>
public sealed class GroupProjection<TResult>
{
    private readonly Func<int?, CancellationToken, ValueTask<Result<List<TResult>>>> _run;

    internal GroupProjection(Func<int?, CancellationToken, ValueTask<Result<List<TResult>>>> run) => _run = run;

    /// <summary>Runs the query and returns every group's projection.</summary>
    public ValueTask<Result<List<TResult>>> ToListAsync(CancellationToken ct = default) => _run(null, ct);

    /// <summary>The first group's projection, or default.</summary>
    public async ValueTask<Result<TResult?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var rows = await _run(1, ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<TResult?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }
}
