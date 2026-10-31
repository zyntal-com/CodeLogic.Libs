using System.Linq.Expressions;
using System.Numerics;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// A typed query over <typeparamref name="T"/>. Builder methods return a new query (the original is unchanged, so a
/// query can be reused); lambdas are translated when a terminal method runs, and an untranslatable lambda fails that
/// call with <c>data.translation_failed</c>. Soft-deleted rows and other tenants' rows are filtered out.
/// </summary>
/// <example>
/// <code>
/// var top = await db.Query&lt;Order&gt;()
///     .Where(o =&gt; o.Total &gt; 100 &amp;&amp; o.Status == Status.Paid)
///     .OrderByDescending(o =&gt; o.Total).ThenBy(o =&gt; o.Id)
///     .Take(20)
///     .ToListAsync(ct);
/// </code>
/// </example>
public sealed class QueryBuilder<T>
{
    private const string Alias = "t0";

    private readonly Database _db;
    private readonly QueryState _state;

    internal QueryBuilder(Database db) : this(db, QueryState.Empty) { }

    private QueryBuilder(Database db, QueryState state)
    {
        _db = db;
        _state = state;
    }

    private QueryBuilder<T> With(QueryState state) => new(_db, state);

    // ── Building ────────────────────────────────────────────────────────────

    /// <summary>Adds a condition (several are combined with AND).</summary>
    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return With(_state with { Filters = [.. _state.Filters, new Filter(predicate, null, false)] });
    }

    /// <summary>
    /// Keeps rows for which a <typeparamref name="TInner"/> row matching <paramref name="correlation"/> exists (correlated
    /// <c>EXISTS</c>); the inner entity's own filters (soft delete, tenant) apply.
    /// </summary>
    public QueryBuilder<T> WhereExists<TInner>(Expression<Func<T, TInner, bool>> correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        InnerMaps.Ensure<TInner>();
        return With(_state with { Filters = [.. _state.Filters, new Filter(correlation, typeof(TInner), false)], Correlated = true });
    }

    /// <summary>Keeps rows for which no matching <typeparamref name="TInner"/> row exists (<c>NOT EXISTS</c>).</summary>
    public QueryBuilder<T> WhereNotExists<TInner>(Expression<Func<T, TInner, bool>> correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        InnerMaps.Ensure<TInner>();
        return With(_state with { Filters = [.. _state.Filters, new Filter(correlation, typeof(TInner), true)], Correlated = true });
    }

    /// <summary>
    /// Keeps rows whose <paramref name="key"/> is among the <paramref name="innerKey"/> values of <typeparamref name="TInner"/>
    /// rows matching <paramref name="innerFilter"/> (<c>IN (SELECT ...)</c>).
    /// </summary>
    public QueryBuilder<T> WhereIn<TInner, TKey>(Expression<Func<T, TKey>> key, Expression<Func<TInner, TKey>> innerKey,
        Expression<Func<TInner, bool>>? innerFilter = null)
    {
        InnerMaps.Ensure<TInner>();
        return With(_state with { Subqueries = [.. _state.Subqueries, new SubqueryFilter(key, typeof(TInner), innerKey, innerFilter, false)], Correlated = true });
    }

    /// <summary>Keeps rows whose <paramref name="key"/> is not among the <paramref name="innerKey"/> values (<c>NOT IN (SELECT ...)</c>).</summary>
    public QueryBuilder<T> WhereNotIn<TInner, TKey>(Expression<Func<T, TKey>> key, Expression<Func<TInner, TKey>> innerKey,
        Expression<Func<TInner, bool>>? innerFilter = null)
    {
        InnerMaps.Ensure<TInner>();
        return With(_state with { Subqueries = [.. _state.Subqueries, new SubqueryFilter(key, typeof(TInner), innerKey, innerFilter, true)], Correlated = true });
    }

    /// <summary>Also returns soft-deleted rows.</summary>
    public QueryBuilder<T> IncludeDeleted() => With(_state with { IncludeDeleted = true });

    /// <summary>Sorts by <paramref name="key"/> ascending (replacing earlier sorts).</summary>
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> key) => With(_state with { Order = [new Sort(key, false)] });

    /// <summary>Sorts by <paramref name="key"/> descending (replacing earlier sorts).</summary>
    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key) => With(_state with { Order = [new Sort(key, true)] });

    /// <summary>Adds a secondary ascending sort.</summary>
    public QueryBuilder<T> ThenBy<TKey>(Expression<Func<T, TKey>> key) => With(_state with { Order = [.. _state.Order, new Sort(key, false)] });

    /// <summary>Adds a secondary descending sort.</summary>
    public QueryBuilder<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key) => With(_state with { Order = [.. _state.Order, new Sort(key, true)] });

    /// <summary>Returns at most <paramref name="count"/> rows.</summary>
    public QueryBuilder<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_state with { Take = count });
    }

    /// <summary>Skips <paramref name="count"/> rows (an order makes this stable; the key is used when none is given).</summary>
    public QueryBuilder<T> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_state with { Skip = count });
    }

    /// <summary>Leaves these properties out of the SELECT (they keep their default values). Key columns cannot be excluded.</summary>
    public QueryBuilder<T> Exclude(params ReadOnlySpan<Expression<Func<T, object?>>> properties)
    {
        List<LambdaExpression> excluded = [.. _state.Excluded];
        foreach (var property in properties) excluded.Add(property);
        return With(_state with { Excluded = excluded });
    }

    /// <summary>Projects each row with <paramref name="selector"/> (computed values allowed).</summary>
    public ProjectedQuery<T, TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new ProjectedQuery<T, TResult>(this, selector);
    }

    internal Database Database => _db;

    internal Result<(SelectStatement Select, IEntityAccessor<T> Accessor, string Table)> PlanSelect() => BuildSelect();

    internal TranslationScope ScopeFor(EntityMap map, ParameterExpression parameter) => Scope(map, parameter);

    // ── Joins ───────────────────────────────────────────────────────────────

    /// <summary>Inner-joins <typeparamref name="T2"/> on <paramref name="on"/>; this query's conditions apply to the first table.</summary>
    public JoinedQuery<T, T2> Join<T2>(Expression<Func<T, T2, bool>> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        InnerMaps.Ensure<T2>();
        return new JoinedQuery<T, T2>(JoinCore.Start(_db, JoinSource()).Join(typeof(T2), SqlJoinKind.Inner, on));
    }

    /// <summary>Left-joins <typeparamref name="T2"/> on <paramref name="on"/> (the joined entity is null when nothing matched).</summary>
    public JoinedQuery<T, T2> LeftJoin<T2>(Expression<Func<T, T2, bool>> on)
    {
        ArgumentNullException.ThrowIfNull(on);
        InnerMaps.Ensure<T2>();
        return new JoinedQuery<T, T2>(JoinCore.Start(_db, JoinSource()).Join(typeof(T2), SqlJoinKind.Left, on));
    }

    /// <summary>
    /// Inner-joins <typeparamref name="T2"/> where <paramref name="leftKey"/> equals <paramref name="rightKey"/>. Composite keys
    /// use anonymous types with matching members: <c>Join&lt;Line, object&gt;(o =&gt; new { o.Id, o.Shop }, l =&gt; new { Id = l.OrderId, l.Shop })</c>.
    /// </summary>
    public JoinedQuery<T, T2> Join<T2, TKey>(Expression<Func<T, TKey>> leftKey, Expression<Func<T2, TKey>> rightKey) =>
        Join(KeyJoin(leftKey, rightKey));

    private static Expression<Func<T, T2, bool>> KeyJoin<T2, TKey>(Expression<Func<T, TKey>> leftKey, Expression<Func<T2, TKey>> rightKey)
    {
        var left = leftKey.Body;
        var right = rightKey.Body;
        Expression body;
        if (left is NewExpression l && right is NewExpression r && l.Arguments.Count == r.Arguments.Count)
        {
            body = l.Arguments.Zip(r.Arguments, (a, b) => (Expression)Expression.Equal(a, b))
                .Aggregate(Expression.AndAlso);
        }
        else
        {
            body = Expression.Equal(left, right);
        }
        return Expression.Lambda<Func<T, T2, bool>>(body, leftKey.Parameters[0], rightKey.Parameters[0]);
    }

    private Func<Result<(SelectStatement, EntityMap)>> JoinSource()
    {
        var source = With(_state with { Correlated = true });
        return () =>
        {
            var plan = source.BuildSelect(ignorePaging: true);
            return plan.IsFailure
                ? Result<(SelectStatement, EntityMap)>.Failure(plan.Error!)
                : Result<(SelectStatement, EntityMap)>.Success((plan.Value!.Select, plan.Value.Accessor.Map));
        };
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>Runs the query and returns every row.</summary>
    public async ValueTask<Result<List<T>>> ToListAsync(CancellationToken ct = default)
    {
        var plan = BuildSelect();
        if (plan.IsFailure) return plan.Error!;
        var (select, accessor, table) = plan.Value!;
        return await _db.QueryAsync(select, accessor, table, [DataTags.Table(table)], ct).ConfigureAwait(false);
    }

    /// <summary>The first row, or null.</summary>
    public async ValueTask<Result<T?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var rows = await Take(1).ToListAsync(ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<T?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }

    /// <summary>The first row matching <paramref name="predicate"/>, or null.</summary>
    public ValueTask<Result<T?>> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
        Where(predicate).FirstOrDefaultAsync(ct);

    /// <summary>Number of matching rows (Take/Skip are ignored).</summary>
    public async ValueTask<Result<long>> CountAsync(CancellationToken ct = default)
    {
        var count = await AggregateAsync<long>(SqlAggregate.Count, null, ct).ConfigureAwait(false);
        return count.IsSuccess ? Result<long>.Success(count.Value ?? 0) : count.Error!;
    }

    /// <summary>True when any row matches.</summary>
    public async ValueTask<Result<bool>> AnyAsync(CancellationToken ct = default)
    {
        var plan = BuildSelect(projectKeyOnly: true, ignorePaging: true);
        if (plan.IsFailure) return plan.Error!;
        var (select, _, table) = plan.Value!;
        var rows = await _db.QueryAsync<long>(select with { Projection = [new ProjectionItem(new LiteralExpr(1))], Limit = LimitParam(1), OrderBy = [] },
            null, table, [DataTags.Table(table)], ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<bool>.Success(rows.Value!.Count > 0) : rows.Error!;
    }

    /// <summary>True when any row matches <paramref name="predicate"/>.</summary>
    public ValueTask<Result<bool>> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) => Where(predicate).AnyAsync(ct);

    /// <summary>Largest value of <paramref name="selector"/>, or null when no row matches.</summary>
    public ValueTask<Result<TValue?>> MaxAsync<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default) where TValue : struct =>
        AggregateAsync<TValue>(SqlAggregate.Max, selector, ct);

    /// <summary>Smallest value of <paramref name="selector"/>, or null when no row matches.</summary>
    public ValueTask<Result<TValue?>> MinAsync<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default) where TValue : struct =>
        AggregateAsync<TValue>(SqlAggregate.Min, selector, ct);

    /// <summary>Sum of <paramref name="selector"/> (zero when no row matches).</summary>
    public async ValueTask<Result<TValue>> SumAsync<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default)
        where TValue : struct, INumber<TValue>
    {
        var sum = await AggregateAsync<TValue>(SqlAggregate.Sum, selector, ct).ConfigureAwait(false);
        return sum.IsSuccess ? Result<TValue>.Success(sum.Value ?? TValue.Zero) : sum.Error!;
    }

    /// <summary>Average of <paramref name="selector"/>, or null when no row matches.</summary>
    public ValueTask<Result<double?>> AverageAsync<TValue>(Expression<Func<T, TValue>> selector, CancellationToken ct = default)
        where TValue : struct, INumber<TValue> =>
        AggregateAsync<double>(SqlAggregate.Average, selector, ct);

    /// <summary>Average of a decimal <paramref name="selector"/>, or null when no row matches.</summary>
    public ValueTask<Result<decimal?>> AverageAsync(Expression<Func<T, decimal>> selector, CancellationToken ct = default) =>
        AggregateAsync<decimal>(SqlAggregate.Average, selector, ct);

    private async ValueTask<Result<TValue?>> AggregateAsync<TValue>(SqlAggregate aggregate, LambdaExpression? selector, CancellationToken ct)
        where TValue : struct
    {
        var plan = BuildSelect(ignorePaging: true);
        if (plan.IsFailure) return plan.Error!;
        var (select, accessor, table) = plan.Value!;

        SqlExpr? operand = null;
        if (selector is not null)
        {
            var value = SqlTranslator.TranslateValue(selector, Scope(accessor.Map, selector.Parameters[0]));
            if (value.IsFailure) return value.Error!;
            operand = value.Value;
        }

        var statement = select with { Projection = [new ProjectionItem(new AggregateExpr(aggregate, operand))], OrderBy = [] };
        // Read as TValue? so an aggregate over no rows (SQL NULL) stays null instead of becoming 0.
        var result = await _db.ScalarAsync<TValue?>(statement, table, [DataTags.Table(table)], ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<TValue?>.Success(result.Value) : result.Error!;
    }

    // ── Paging and streaming ────────────────────────────────────────────────

    /// <summary>
    /// One page (offset paging). The total is counted only when <paramref name="withTotal"/> is true;
    /// <see cref="PagedResult{T}.HasMore"/> is always set. Prefer <see cref="ToCursorPageAsync"/> for deep paging.
    /// </summary>
    public async ValueTask<Result<PagedResult<T>>> ToPagedListAsync(int page, int pageSize, bool withTotal = false, CancellationToken ct = default)
    {
        if (page < 1 || pageSize < 1) return DataErrors.Validation("Page and page size must be at least 1.");
        var rows = await Skip((page - 1) * pageSize).Take(pageSize + 1).ToListAsync(ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;
        var items = rows.Value!;
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);

        long? total = null;
        if (withTotal)
        {
            var count = await CountAsync(ct).ConfigureAwait(false);
            if (count.IsFailure) return count.Error!;
            total = count.Value;
        }
        return new PagedResult<T>(items, page, pageSize, total, hasMore);
    }

    /// <summary>
    /// Keyset paging: the next <paramref name="pageSize"/> rows after <paramref name="after"/> (a cursor from the previous
    /// page). The order (property sorts only) is completed with the primary key, so pages never skip or repeat rows
    /// while data changes, and the cost does not grow with depth.
    /// </summary>
    public async ValueTask<Result<CursorPage<T>>> ToCursorPageAsync(int pageSize, string? after = null, CancellationToken ct = default)
    {
        if (pageSize < 1) return DataErrors.Validation("Page size must be at least 1.");
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        if (map.Key.IsEmpty) return DataErrors.NoPrimaryKey(map.ClrType.Name);

        List<(ColumnMap Column, bool Descending)> order = [];
        foreach (var sort in _state.Order)
        {
            var body = sort.Key.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : sort.Key.Body;
            if (body is not MemberExpression { Expression: ParameterExpression } member || map.FindProperty(member.Member.Name) is not { } column)
                return DataErrors.Validation("Cursor paging can only order by mapped properties.");
            if (order.All(o => o.Column != column)) order.Add((column, sort.Descending));
        }
        var lastDescending = order.Count > 0 && order[^1].Descending;
        foreach (var key in map.Key.Columns)
            if (order.All(o => o.Column != key)) order.Add((key, lastDescending));

        var fingerprint = typeof(T).Name + ":" + string.Join(",", order.Select(o => o.Column.PropertyName + (o.Descending ? "-" : "+")));
        var names = map.GetNames(_db.NamingPolicy);
        var alias = _state.Correlated ? Alias : null;

        var plan = With(_state with { Take = null, Skip = null }).BuildSelect();
        if (plan.IsFailure) return plan.Error!;
        var (select, _, table) = plan.Value!;
        select = select with
        {
            OrderBy = order.Select(o => new OrderItem(new ColumnExpr(names.Of(o.Column), alias), o.Descending)).ToArray(),
            Limit = LimitParam(pageSize + 1),
            Offset = null,
        };

        if (after is not null)
        {
            var values = CursorToken.Decode(after, fingerprint, order.Select(o => o.Column.ClrType).ToArray());
            if (values.IsFailure) return values.Error!;
            SqlExpr? seek = null;
            for (var i = 0; i < order.Count; i++)
            {
                if (values.Value![i] is null) return DataErrors.InvalidCursor("Cursor paging needs non-null sort values.");
                SqlExpr? branch = null;
                for (var j = 0; j < i; j++)
                    branch = ReadPlanner.And(branch, new BinaryExpr(SqlBinaryOperator.Equal, new ColumnExpr(names.Of(order[j].Column), alias),
                        new ParameterExpr(SqlParameterValue.ForColumn(values.Value[j], order[j].Column))));
                branch = ReadPlanner.And(branch, new BinaryExpr(order[i].Descending ? SqlBinaryOperator.LessThan : SqlBinaryOperator.GreaterThan,
                    new ColumnExpr(names.Of(order[i].Column), alias), new ParameterExpr(SqlParameterValue.ForColumn(values.Value[i], order[i].Column))));
                seek = seek is null ? branch : new BinaryExpr(SqlBinaryOperator.Or, seek, branch!);
            }
            select = select with { Where = ReadPlanner.And(select.Where, seek) };
        }

        var rows = await _db.QueryAsync(select, accessor.Value, table, [DataTags.Table(table)], ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;
        var items = rows.Value!;
        string? next = null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            next = CursorToken.Encode(fingerprint, order.Select(o => accessor.Value.GetValue(last, o.Column.Ordinal)).ToArray());
        }
        return new CursorPage<T>(items, next);
    }

    /// <summary>
    /// Streams the rows without buffering them; one connection stays open until enumeration ends. Failures are thrown
    /// as <see cref="DataQueryException"/>, because a stream cannot return a result. Streams bypass the cache.
    /// </summary>
    public async IAsyncEnumerable<T> AsAsyncEnumerable([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var plan = BuildSelect();
        if (plan.IsFailure) throw new DataQueryException(plan.Error!);
        var (select, accessor, _) = plan.Value!;
        await foreach (var row in _db.StreamAsync(select, accessor, ct).ConfigureAwait(false))
            yield return row;
    }

    // ── Set-based writes ────────────────────────────────────────────────────

    /// <summary>
    /// Deletes every matching row in one statement: a soft delete for <c>[SoftDelete]</c> entities unless
    /// <paramref name="hard"/>. Returns the number of rows affected.
    /// </summary>
    public async ValueTask<Result<int>> DeleteAsync(bool hard = false, CancellationToken ct = default)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var where = BuildWhere(map, alias: null);
        if (where.IsFailure) return where.Error!;

        var ctx = _db.WriteContext;
        var names = map.GetNames(ctx.Policy);
        var table = WritePlanner.TableOf(map, names, ctx);
        SqlStatement statement;
        if (!hard && map.SoftDelete is { } soft)
        {
            var now = ctx.Time.GetUtcNow();
            List<SetItem> set = [new SetItem(names.Of(soft), TimeParam(soft, now))];
            if (map.UpdatedAt is { } updatedAt) set.Add(new SetItem(names.Of(updatedAt), TimeParam(updatedAt, now)));
            statement = new UpdateStatement(table, set) { Where = where.Value };
        }
        else
        {
            statement = new DeleteStatement(table) { Where = where.Value };
        }

        var command = new WriteCommand(statement, WriteKind.Delete, names.Table, 0, [DataTags.Table(names.Table)]);
        return await _db.ExecuteWritesAsync([command], Array.Empty<T>(), accessor.Value!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates every matching row in one statement. <paramref name="setter"/> is a member initializer whose values may use
    /// the row's columns: <c>q.UpdateAsync(p =&gt; new Product { Stock = p.Stock - 1, Name = newName })</c>. Audit columns are
    /// written and integer concurrency tokens bumped. Returns the number of rows affected.
    /// </summary>
    public async ValueTask<Result<int>> UpdateAsync(Expression<Func<T, T>> setter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setter);
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        if (setter.Body is not MemberInitExpression init || init.Bindings.Count == 0)
            return DataErrors.TranslationFailed("UpdateAsync needs a member initializer: x => new T { Prop = value, ... }.", setter.ToString());

        var where = BuildWhere(map, alias: null);
        if (where.IsFailure) return where.Error!;

        var ctx = _db.WriteContext;
        var names = map.GetNames(ctx.Policy);
        var scope = Scope(map, setter.Parameters[0], alias: null);
        List<SetItem> set = [];
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment || map.FindProperty(assignment.Member.Name) is not { } column)
                return DataErrors.TranslationFailed($"'{binding.Member.Name}' is not a mapped property of {typeof(T).Name}.");
            if (column.Has(ColumnRoles.Key) || column.Has(ColumnRoles.TenantKey))
                return DataErrors.Validation($"'{column.PropertyName}' is a key or tenant column and cannot be updated in bulk.");
            var value = SqlTranslator.TranslateValue(assignment.Expression, scope);
            if (value.IsFailure) return value.Error!;
            set.Add(new SetItem(names.Of(column), value.Value!));
        }

        var now = ctx.Time.GetUtcNow();
        if (map.UpdatedAt is { } updatedAt && set.All(s => s.Column != names.Of(updatedAt)))
            set.Add(new SetItem(names.Of(updatedAt), TimeParam(updatedAt, now)));
        if (map.UpdatedBy is { } updatedBy && ctx.User is not null)
            set.Add(new SetItem(names.Of(updatedBy), new ParameterExpr(SqlParameterValue.ForColumn(ctx.User, updatedBy))));
        if (map.ConcurrencyToken is { } token && token.ValueType != typeof(byte[]) && set.All(s => s.Column != names.Of(token)))
            set.Add(new SetItem(names.Of(token), new BinaryExpr(SqlBinaryOperator.Add, new ColumnExpr(names.Of(token)), new LiteralExpr(1))));

        var statement = new UpdateStatement(WritePlanner.TableOf(map, names, ctx), set) { Where = where.Value };
        var command = new WriteCommand(statement, WriteKind.Update, names.Table, 0, [DataTags.Table(names.Table)]);
        return await _db.ExecuteWritesAsync([command], Array.Empty<T>(), accessor.Value!, ct).ConfigureAwait(false);
    }

    // ── Planning ────────────────────────────────────────────────────────────

    private Result<(SelectStatement Select, IEntityAccessor<T> Accessor, string Table)> BuildSelect(bool projectKeyOnly = false, bool ignorePaging = false)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var names = map.GetNames(_db.NamingPolicy);
        var alias = _state.Correlated ? Alias : null;

        var where = BuildWhere(map, alias);
        if (where.IsFailure) return where.Error!;
        var select = ReadPlanner.Select(_db, map, null, includeDeleted: true, alias);
        if (select.IsFailure) return select.Error!;
        var statement = select.Value! with { Where = where.Value };

        if (_state.Excluded.Count > 0 && !projectKeyOnly)
        {
            HashSet<string> excluded = [];
            foreach (var lambda in _state.Excluded)
            {
                var body = lambda.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : lambda.Body;
                if (body is not MemberExpression { Expression: ParameterExpression } member || map.FindProperty(member.Member.Name) is not { } column)
                    return DataErrors.Validation($"'{lambda}' is not a mapped property of {typeof(T).Name}.");
                if (column.Has(ColumnRoles.Key))
                    return DataErrors.Validation($"Key column '{column.PropertyName}' cannot be excluded.");
                excluded.Add(column.PropertyName);
            }
            statement = statement with
            {
                Projection = map.Columns.Where(c => !excluded.Contains(c.PropertyName))
                    .Select(c => new ProjectionItem(new ColumnExpr(names.Of(c), alias))).ToArray(),
            };
        }

        if (_state.Order.Count > 0)
        {
            List<OrderItem> order = [];
            foreach (var sort in _state.Order)
            {
                var key = SqlTranslator.TranslateValue(sort.Key, Scope(map, sort.Key.Parameters[0], alias));
                if (key.IsFailure) return key.Error!;
                order.Add(new OrderItem(key.Value!, sort.Descending));
            }
            statement = statement with { OrderBy = order };
        }

        if (!ignorePaging && (_state.Take is not null || _state.Skip is not null))
        {
            statement = statement with
            {
                OrderBy = statement.OrderBy.Count > 0 || _state.Skip is null ? statement.OrderBy : ReadPlanner.KeyOrder(map, names, alias),
                Limit = _state.Take is { } take ? LimitParam(take) : null,
                Offset = _state.Skip is > 0 ? LimitParam(_state.Skip.Value) : null,
            };
        }

        return (statement, accessor.Value!, names.Table);
    }

    /// <summary>The query's WHERE: its conditions, subqueries and the entity's standing filters.</summary>
    private Result<SqlExpr?> BuildWhere(EntityMap map, string? alias)
    {
        // Set-based writes do not alias their target; correlated subqueries then qualify outer columns by table name.
        var outer = alias ?? (_state.Correlated ? map.GetNames(_db.NamingPolicy).Table : null);
        SqlExpr? where = null;

        foreach (var filter in _state.Filters)
        {
            if (filter.Inner is null)
            {
                var translated = SqlTranslator.TranslatePredicate(filter.Lambda, Scope(map, filter.Lambda.Parameters[0], outer));
                if (translated.IsFailure) return translated.Error!;
                where = ReadPlanner.And(where, translated.Value);
                continue;
            }

            var exists = Correlated(map, outer, filter);
            if (exists.IsFailure) return exists.Error!;
            where = ReadPlanner.And(where, exists.Value);
        }

        foreach (var sub in _state.Subqueries)
        {
            var inner = MapOf(sub.Inner);
            if (inner.IsFailure) return inner.Error!;
            var outerKey = SqlTranslator.TranslateValue(sub.Key, Scope(map, sub.Key.Parameters[0], outer));
            if (outerKey.IsFailure) return outerKey.Error!;

            var innerScope = new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy).Bind(sub.InnerKey.Parameters[0], inner.Value!, "i0");
            var innerKey = SqlTranslator.TranslateValue(sub.InnerKey, innerScope);
            if (innerKey.IsFailure) return innerKey.Error!;
            SqlExpr? innerWhere = null;
            if (sub.InnerFilter is not null)
            {
                var filterScope = new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy).Bind(sub.InnerFilter.Parameters[0], inner.Value!, "i0");
                var translated = SqlTranslator.TranslatePredicate(sub.InnerFilter, filterScope);
                if (translated.IsFailure) return translated.Error!;
                innerWhere = translated.Value;
            }

            var innerSelect = ReadPlanner.Select(_db, inner.Value!, innerWhere, includeDeleted: false, "i0");
            if (innerSelect.IsFailure) return innerSelect.Error!;
            var subquery = innerSelect.Value! with { Projection = [new ProjectionItem(innerKey.Value!)] };
            where = ReadPlanner.And(where, new InSubqueryExpr(outerKey.Value!, subquery, sub.Negated));
        }

        var standing = ReadPlanner.Filters(_db, map, _state.IncludeDeleted, outer);
        if (standing.IsFailure) return standing.Error!;
        return Result<SqlExpr?>.Success(ReadPlanner.And(where, standing.Value));
    }

    private Result<SqlExpr> Correlated(EntityMap outerMap, string? outerAlias, Filter filter)
    {
        var inner = MapOf(filter.Inner!);
        if (inner.IsFailure) return inner.Error!;
        var scope = new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy)
            .Bind(filter.Lambda.Parameters[0], outerMap, outerAlias)
            .Bind(filter.Lambda.Parameters[1], inner.Value!, "i0");
        var correlation = SqlTranslator.TranslatePredicate(filter.Lambda, scope);
        if (correlation.IsFailure) return correlation.Error!;

        var select = ReadPlanner.Select(_db, inner.Value!, correlation.Value, includeDeleted: false, "i0");
        if (select.IsFailure) return select.Error!;
        return new ExistsExpr(select.Value! with { Projection = [new ProjectionItem(new LiteralExpr(1))] }, filter.Negated);
    }

    private static Result<EntityMap> MapOf(Type type) => InnerMaps.Get(type);

    private TranslationScope Scope(EntityMap map, ParameterExpression parameter, string? alias = null) =>
        new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy).Bind(parameter, map, alias ?? (_state.Correlated ? Alias : null));

    private static ParameterExpr LimitParam(long value) => new(SqlParameterValue.Of(value));

    private static ParameterExpr TimeParam(ColumnMap column, DateTimeOffset now) =>
        new(column.ValueType == typeof(DateTime) ? SqlParameterValue.For(now.UtcDateTime, column) : SqlParameterValue.For(now, column));

    private sealed record Filter(LambdaExpression Lambda, Type? Inner, bool Negated);

    private sealed record SubqueryFilter(LambdaExpression Key, Type Inner, LambdaExpression InnerKey, LambdaExpression? InnerFilter, bool Negated);

    private sealed record Sort(LambdaExpression Key, bool Descending);

    private sealed record QueryState
    {
        public static readonly QueryState Empty = new();
        public IReadOnlyList<Filter> Filters { get; init; } = [];
        public IReadOnlyList<SubqueryFilter> Subqueries { get; init; } = [];
        public IReadOnlyList<Sort> Order { get; init; } = [];
        public IReadOnlyList<LambdaExpression> Excluded { get; init; } = [];
        public int? Take { get; init; }
        public int? Skip { get; init; }
        public bool IncludeDeleted { get; init; }
        public bool Correlated { get; init; }
    }
}

/// <summary>Resolves entity maps for types known only at runtime (subquery targets).</summary>
internal static class InnerMaps
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<Result<EntityMap>>> Resolvers = new();

    public static Result<EntityMap> Get(Type type) =>
        Resolvers.TryGetValue(type, out var resolve) ? resolve() : DataErrors.EntityNotMapped(type.Name);

    /// <summary>Registers how to resolve <typeparamref name="TInner"/>'s map (called when a subquery mentions it).</summary>
    public static void Ensure<TInner>() =>
        Resolvers.TryAdd(typeof(TInner), static () =>
        {
            var accessor = EntityAccessorRegistry.Get<TInner>();
            return accessor.IsSuccess ? Result<EntityMap>.Success(accessor.Value!.Map) : accessor.Error!;
        });
}
