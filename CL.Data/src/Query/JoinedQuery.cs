// Typed join wrappers for 2 to 5 tables; all logic is in JoinCore.
using System.Linq.Expressions;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>The projected results of a joined query.</summary>
/// <typeparam name="TResult">Projected type.</typeparam>
public sealed class JoinProjection<TResult>
{
    private readonly JoinCore _core;
    private readonly LambdaExpression _selector;

    internal JoinProjection(JoinCore core, LambdaExpression selector)
    {
        _core = core;
        _selector = selector;
    }

    /// <summary>Runs the query and returns every result.</summary>
    public ValueTask<Result<List<TResult>>> ToListAsync(CancellationToken ct = default) => _core.SelectAsync<TResult>(_selector, ct);

    /// <summary>The first result, or default.</summary>
    public async ValueTask<Result<TResult?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var rows = await _core.Take(1).SelectAsync<TResult>(_selector, ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<TResult?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }
}

/// <summary>
/// A query over 2 joined tables. Lambdas take one parameter per table, in join order. <c>ToListAsync</c> returns a
/// tuple of entities (a LEFT JOINed entity is null when nothing matched); <c>Select</c> projects.
/// </summary>
public sealed class JoinedQuery<T1, T2>
{
    private readonly JoinCore _core;

    internal JoinedQuery(JoinCore core) => _core = core;

    /// <summary>Inner-joins <typeparamref name="T3"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3> Join<T3>(Expression<Func<T1, T2, T3, bool>> on)
    {
        InnerMaps.Ensure<T3>();
        return new(_core.Join(typeof(T3), SqlJoinKind.Inner, on));
    }

    /// <summary>Left-joins <typeparamref name="T3"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3> LeftJoin<T3>(Expression<Func<T1, T2, T3, bool>> on)
    {
        InnerMaps.Ensure<T3>();
        return new(_core.Join(typeof(T3), SqlJoinKind.Left, on));
    }

    /// <summary>Adds a condition over the joined tables.</summary>
    public JoinedQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate) => new(_core.Where(predicate));

    /// <summary>Sorts ascending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> key) => new(_core.Order(key, false, false));

    /// <summary>Sorts descending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T1, T2, TKey>> key) => new(_core.Order(key, true, false));

    /// <summary>Adds a secondary ascending sort.</summary>
    public JoinedQuery<T1, T2> ThenBy<TKey>(Expression<Func<T1, T2, TKey>> key) => new(_core.Order(key, false, true));

    /// <summary>Adds a secondary descending sort.</summary>
    public JoinedQuery<T1, T2> ThenByDescending<TKey>(Expression<Func<T1, T2, TKey>> key) => new(_core.Order(key, true, true));

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2> Take(int count) => new(_core.Take(count));

    /// <summary>Skips <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2> Skip(int count) => new(_core.Skip(count));

    /// <summary>Projects each joined row.</summary>
    public JoinProjection<TResult> Select<TResult>(Expression<Func<T1, T2, TResult>> selector) => new(_core, selector);

    /// <summary>Runs the query and returns the joined entities of each row.</summary>
    public ValueTask<Result<List<(T1, T2?)>>> ToListAsync(CancellationToken ct = default) =>
        _core.EntitiesAsync(static (r, o) => (JoinCore.Entity<T1>(r, o, 0)!, JoinCore.Entity<T2>(r, o, 1)), ct);

    /// <summary>Number of joined rows.</summary>
    public ValueTask<Result<long>> CountAsync(CancellationToken ct = default) => _core.CountAsync(ct);
}

/// <summary>
/// A query over 3 joined tables. Lambdas take one parameter per table, in join order. <c>ToListAsync</c> returns a
/// tuple of entities (a LEFT JOINed entity is null when nothing matched); <c>Select</c> projects.
/// </summary>
public sealed class JoinedQuery<T1, T2, T3>
{
    private readonly JoinCore _core;

    internal JoinedQuery(JoinCore core) => _core = core;

    /// <summary>Inner-joins <typeparamref name="T4"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3, T4> Join<T4>(Expression<Func<T1, T2, T3, T4, bool>> on)
    {
        InnerMaps.Ensure<T4>();
        return new(_core.Join(typeof(T4), SqlJoinKind.Inner, on));
    }

    /// <summary>Left-joins <typeparamref name="T4"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3, T4> LeftJoin<T4>(Expression<Func<T1, T2, T3, T4, bool>> on)
    {
        InnerMaps.Ensure<T4>();
        return new(_core.Join(typeof(T4), SqlJoinKind.Left, on));
    }

    /// <summary>Adds a condition over the joined tables.</summary>
    public JoinedQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate) => new(_core.Where(predicate));

    /// <summary>Sorts ascending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key) => new(_core.Order(key, false, false));

    /// <summary>Sorts descending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key) => new(_core.Order(key, true, false));

    /// <summary>Adds a secondary ascending sort.</summary>
    public JoinedQuery<T1, T2, T3> ThenBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key) => new(_core.Order(key, false, true));

    /// <summary>Adds a secondary descending sort.</summary>
    public JoinedQuery<T1, T2, T3> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key) => new(_core.Order(key, true, true));

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3> Take(int count) => new(_core.Take(count));

    /// <summary>Skips <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3> Skip(int count) => new(_core.Skip(count));

    /// <summary>Projects each joined row.</summary>
    public JoinProjection<TResult> Select<TResult>(Expression<Func<T1, T2, T3, TResult>> selector) => new(_core, selector);

    /// <summary>Runs the query and returns the joined entities of each row.</summary>
    public ValueTask<Result<List<(T1, T2?, T3?)>>> ToListAsync(CancellationToken ct = default) =>
        _core.EntitiesAsync(static (r, o) => (JoinCore.Entity<T1>(r, o, 0)!, JoinCore.Entity<T2>(r, o, 1), JoinCore.Entity<T3>(r, o, 2)), ct);

    /// <summary>Number of joined rows.</summary>
    public ValueTask<Result<long>> CountAsync(CancellationToken ct = default) => _core.CountAsync(ct);
}

/// <summary>
/// A query over 4 joined tables. Lambdas take one parameter per table, in join order. <c>ToListAsync</c> returns a
/// tuple of entities (a LEFT JOINed entity is null when nothing matched); <c>Select</c> projects.
/// </summary>
public sealed class JoinedQuery<T1, T2, T3, T4>
{
    private readonly JoinCore _core;

    internal JoinedQuery(JoinCore core) => _core = core;

    /// <summary>Inner-joins <typeparamref name="T5"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> Join<T5>(Expression<Func<T1, T2, T3, T4, T5, bool>> on)
    {
        InnerMaps.Ensure<T5>();
        return new(_core.Join(typeof(T5), SqlJoinKind.Inner, on));
    }

    /// <summary>Left-joins <typeparamref name="T5"/> on <paramref name="on"/>.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> LeftJoin<T5>(Expression<Func<T1, T2, T3, T4, T5, bool>> on)
    {
        InnerMaps.Ensure<T5>();
        return new(_core.Join(typeof(T5), SqlJoinKind.Left, on));
    }

    /// <summary>Adds a condition over the joined tables.</summary>
    public JoinedQuery<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> predicate) => new(_core.Where(predicate));

    /// <summary>Sorts ascending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> key) => new(_core.Order(key, false, false));

    /// <summary>Sorts descending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> key) => new(_core.Order(key, true, false));

    /// <summary>Adds a secondary ascending sort.</summary>
    public JoinedQuery<T1, T2, T3, T4> ThenBy<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> key) => new(_core.Order(key, false, true));

    /// <summary>Adds a secondary descending sort.</summary>
    public JoinedQuery<T1, T2, T3, T4> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> key) => new(_core.Order(key, true, true));

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3, T4> Take(int count) => new(_core.Take(count));

    /// <summary>Skips <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3, T4> Skip(int count) => new(_core.Skip(count));

    /// <summary>Projects each joined row.</summary>
    public JoinProjection<TResult> Select<TResult>(Expression<Func<T1, T2, T3, T4, TResult>> selector) => new(_core, selector);

    /// <summary>Runs the query and returns the joined entities of each row.</summary>
    public ValueTask<Result<List<(T1, T2?, T3?, T4?)>>> ToListAsync(CancellationToken ct = default) =>
        _core.EntitiesAsync(static (r, o) => (JoinCore.Entity<T1>(r, o, 0)!, JoinCore.Entity<T2>(r, o, 1), JoinCore.Entity<T3>(r, o, 2), JoinCore.Entity<T4>(r, o, 3)), ct);

    /// <summary>Number of joined rows.</summary>
    public ValueTask<Result<long>> CountAsync(CancellationToken ct = default) => _core.CountAsync(ct);
}

/// <summary>
/// A query over 5 joined tables. Lambdas take one parameter per table, in join order. <c>ToListAsync</c> returns a
/// tuple of entities (a LEFT JOINed entity is null when nothing matched); <c>Select</c> projects.
/// </summary>
public sealed class JoinedQuery<T1, T2, T3, T4, T5>
{
    private readonly JoinCore _core;

    internal JoinedQuery(JoinCore core) => _core = core;

    /// <summary>Adds a condition over the joined tables.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> Where(Expression<Func<T1, T2, T3, T4, T5, bool>> predicate) => new(_core.Where(predicate));

    /// <summary>Sorts ascending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> key) => new(_core.Order(key, false, false));

    /// <summary>Sorts descending (replacing earlier sorts).</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> key) => new(_core.Order(key, true, false));

    /// <summary>Adds a secondary ascending sort.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> ThenBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> key) => new(_core.Order(key, false, true));

    /// <summary>Adds a secondary descending sort.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> key) => new(_core.Order(key, true, true));

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> Take(int count) => new(_core.Take(count));

    /// <summary>Skips <paramref name="count"/> rows.</summary>
    public JoinedQuery<T1, T2, T3, T4, T5> Skip(int count) => new(_core.Skip(count));

    /// <summary>Projects each joined row.</summary>
    public JoinProjection<TResult> Select<TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> selector) => new(_core, selector);

    /// <summary>Runs the query and returns the joined entities of each row.</summary>
    public ValueTask<Result<List<(T1, T2?, T3?, T4?, T5?)>>> ToListAsync(CancellationToken ct = default) =>
        _core.EntitiesAsync(static (r, o) => (JoinCore.Entity<T1>(r, o, 0)!, JoinCore.Entity<T2>(r, o, 1), JoinCore.Entity<T3>(r, o, 2), JoinCore.Entity<T4>(r, o, 3), JoinCore.Entity<T5>(r, o, 4)), ct);

    /// <summary>Number of joined rows.</summary>
    public ValueTask<Result<long>> CountAsync(CancellationToken ct = default) => _core.CountAsync(ct);
}
