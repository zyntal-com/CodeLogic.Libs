using System.Linq.Expressions;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// A query that returns projected values instead of entities. Created by <c>QueryBuilder.Select</c>; the source query's
/// conditions, order and paging apply. Projections may compute values (<c>x.Price * x.Stock</c>, SqlFn functions, CASE).
/// </summary>
/// <typeparam name="T">Source entity.</typeparam>
/// <typeparam name="TResult">Projected type: a scalar, an anonymous type, a record or a DTO.</typeparam>
public sealed class ProjectedQuery<T, TResult>
{
    private readonly QueryBuilder<T> _source;
    private readonly Expression<Func<T, TResult>> _selector;
    private readonly bool _distinct;

    internal ProjectedQuery(QueryBuilder<T> source, Expression<Func<T, TResult>> selector, bool distinct = false)
    {
        _source = source;
        _selector = selector;
        _distinct = distinct;
    }

    /// <summary>Removes duplicate results (<c>SELECT DISTINCT</c>).</summary>
    public ProjectedQuery<T, TResult> Distinct() => new(_source, _selector, distinct: true);

    /// <summary>Runs the query and returns every result.</summary>
    public async ValueTask<Result<List<TResult>>> ToListAsync(CancellationToken ct = default)
    {
        var plan = _source.PlanSelect();
        if (plan.IsFailure) return plan.Error!;
        var (select, accessor, table) = plan.Value!;

        var projection = ProjectionPlan<TResult>.Create(_selector, _source.ScopeFor(accessor.Map, _selector.Parameters[0]));
        if (projection.IsFailure) return projection.Error!;

        var statement = select with { Projection = projection.Value!.Columns, Distinct = _distinct };
        return await _source.Database.QueryProjectedAsync(statement, projection.Value.Materialize, table, [DataTags.Table(table)], ct, _source.CachePolicy)
            .ConfigureAwait(false);
    }

    /// <summary>The first result, or default.</summary>
    public async ValueTask<Result<TResult?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var rows = await new ProjectedQuery<T, TResult>(_source.Take(1), _selector, _distinct).ToListAsync(ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<TResult?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }
}
