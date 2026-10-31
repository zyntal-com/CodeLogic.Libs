using System.Data.Common;
using System.Linq.Expressions;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// The shared engine behind <c>JoinedQuery&lt;...&gt;</c> of every arity. Tables are aliased <c>t0</c> (the source query)
/// to <c>t4</c>; each lambda's parameters bind to those tables in order. Joined entities' standing filters (soft
/// delete, tenant) go into their ON clause, so LEFT JOINs keep the left row when the right one is filtered out.
/// </summary>
internal sealed class JoinCore
{
    private readonly Database _db;
    private readonly Func<Result<(SelectStatement Select, EntityMap Map)>> _source;

    private JoinCore(Database db, Func<Result<(SelectStatement, EntityMap)>> source, JoinState state)
    {
        _db = db;
        _source = source;
        State = state;
    }

    internal JoinState State { get; }

    public static JoinCore Start(Database db, Func<Result<(SelectStatement, EntityMap)>> source) => new(db, source, JoinState.Empty);

    public JoinCore With(JoinState state) => new(_db, _source, state);

    public JoinCore Join(Type entity, SqlJoinKind kind, LambdaExpression on) =>
        With(State with { Joins = [.. State.Joins, new JoinSpec(entity, kind, on)] });

    public JoinCore Where(LambdaExpression predicate) => With(State with { Filters = [.. State.Filters, predicate] });

    public JoinCore Order(LambdaExpression key, bool descending, bool then) =>
        With(State with { Order = then ? [.. State.Order, (key, descending)] : [(key, descending)] });

    public JoinCore Take(int count) => With(State with { Take = count });

    public JoinCore Skip(int count) => With(State with { Skip = count });

    /// <summary>The joined SELECT (without a projection), the maps of all tables and a scope factory.</summary>
    public Result<JoinPlan> Plan(bool ignorePaging = false)
    {
        var source = _source();
        if (source.IsFailure) return source.Error!;
        var (baseSelect, baseMap) = source.Value!;
        List<EntityMap> maps = [baseMap];
        List<SqlJoin> joins = [];

        for (var i = 0; i < State.Joins.Count; i++)
        {
            var spec = State.Joins[i];
            var map = InnerMaps.Get(spec.Entity);
            if (map.IsFailure) return map.Error!;
            maps.Add(map.Value!);
            var alias = "t" + (i + 1);

            var on = SqlTranslator.TranslatePredicate(spec.On, Scope(spec.On, maps));
            if (on.IsFailure) return on.Error!;
            var standing = ReadPlanner.Filters(_db, map.Value!, includeDeleted: false, alias);
            if (standing.IsFailure) return standing.Error!;

            var names = map.Value!.GetNames(_db.NamingPolicy);
            var table = WritePlanner.TableOf(map.Value!, names, _db.WriteContext) with { Alias = alias };
            joins.Add(new SqlJoin(spec.Kind, table, ReadPlanner.And(on.Value, standing.Value)));
        }

        var where = baseSelect.Where;
        foreach (var filter in State.Filters)
        {
            var translated = SqlTranslator.TranslatePredicate(filter, Scope(filter, maps));
            if (translated.IsFailure) return translated.Error!;
            where = ReadPlanner.And(where, translated.Value);
        }

        var order = baseSelect.OrderBy;
        if (State.Order.Count > 0)
        {
            List<OrderItem> items = [];
            foreach (var (key, descending) in State.Order)
            {
                var translated = SqlTranslator.TranslateValue(key, Scope(key, maps));
                if (translated.IsFailure) return translated.Error!;
                items.Add(new OrderItem(translated.Value!, descending));
            }
            order = items;
        }

        var select = baseSelect with { Joins = joins, Where = where, OrderBy = order, Projection = [] };
        if (!ignorePaging && (State.Take is not null || State.Skip is not null))
        {
            if (order.Count == 0 && State.Skip is not null)
                order = ReadPlanner.KeyOrder(baseMap, baseMap.GetNames(_db.NamingPolicy), "t0");
            select = select with
            {
                OrderBy = order,
                Limit = State.Take is { } take ? new ParameterExpr(SqlParameterValue.Of((long)take)) : null,
                Offset = State.Skip is > 0 ? new ParameterExpr(SqlParameterValue.Of((long)State.Skip.Value)) : null,
            };
        }
        return new JoinPlan(select, maps, (lambda) => Scope(lambda, maps));
    }

    /// <summary>Projects with <paramref name="selector"/> and runs the query.</summary>
    public async ValueTask<Result<List<TResult>>> SelectAsync<TResult>(LambdaExpression selector, CancellationToken ct)
    {
        var plan = Plan();
        if (plan.IsFailure) return plan.Error!;
        var projection = ProjectionPlan<TResult>.Create(selector, plan.Value!.Scope(selector));
        if (projection.IsFailure) return projection.Error!;
        var table = plan.Value.Maps[0].GetNames(_db.NamingPolicy).Table;
        return await _db.QueryProjectedAsync(plan.Value.Select with { Projection = projection.Value!.Columns }, projection.Value.Materialize,
            table, Tags(plan.Value.Maps), ct).ConfigureAwait(false);
    }

    /// <summary>Runs the query and builds each row from the entities of every table (null for an unmatched LEFT JOIN).</summary>
    public async ValueTask<Result<List<TRow>>> EntitiesAsync<TRow>(Func<DbDataReader, int[][], TRow> build, CancellationToken ct)
    {
        var plan = Plan();
        if (plan.IsFailure) return plan.Error!;
        List<ProjectionItem> projection = [];
        var ordinals = new int[plan.Value!.Maps.Count][];
        for (var t = 0; t < plan.Value.Maps.Count; t++)
        {
            var map = plan.Value.Maps[t];
            var names = map.GetNames(_db.NamingPolicy);
            ordinals[t] = new int[map.Columns.Count];
            for (var c = 0; c < map.Columns.Count; c++)
            {
                ordinals[t][c] = projection.Count;
                projection.Add(new ProjectionItem(new ColumnExpr(names.Of(map.Columns[c]), "t" + t), $"j{t}_{c}"));
            }
        }

        var table = plan.Value.Maps[0].GetNames(_db.NamingPolicy).Table;
        return await _db.QueryProjectedAsync(plan.Value.Select with { Projection = projection }, reader => build(reader, ordinals),
            table, Tags(plan.Value.Maps), ct).ConfigureAwait(false);
    }

    /// <summary>Counts the joined rows (paging ignored).</summary>
    public async ValueTask<Result<long>> CountAsync(CancellationToken ct)
    {
        var plan = Plan(ignorePaging: true);
        if (plan.IsFailure) return plan.Error!;
        var table = plan.Value!.Maps[0].GetNames(_db.NamingPolicy).Table;
        var statement = plan.Value.Select with { Projection = [new ProjectionItem(new AggregateExpr(SqlAggregate.Count, null))], OrderBy = [] };
        var count = await _db.ScalarAsync<long>(statement, table, Tags(plan.Value.Maps), ct).ConfigureAwait(false);
        return count.IsSuccess ? Result<long>.Success(count.Value) : count.Error!;
    }

    /// <summary>Materializes table <paramref name="index"/>'s entity, or default when its key is NULL (unmatched LEFT JOIN).</summary>
    public static TEntity? Entity<TEntity>(DbDataReader reader, int[][] ordinals, int index)
    {
        var accessor = EntityAccessorRegistry.Get<TEntity>().ValueOrThrow();
        var columns = ordinals[index];
        var key = accessor.Map.Key.Columns.Count > 0 ? accessor.Map.Key.Columns[0].Ordinal : 0;
        return reader.IsDBNull(columns[key]) ? default : accessor.Materialize(reader, columns);
    }

    private TranslationScope Scope(LambdaExpression lambda, IReadOnlyList<EntityMap> maps)
    {
        if (lambda.Parameters.Count > maps.Count)
            throw new InvalidOperationException("A join lambda has more parameters than joined tables.");
        var scope = new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy);
        for (var i = 0; i < lambda.Parameters.Count; i++) scope.Bind(lambda.Parameters[i], maps[i], "t" + i);
        return scope;
    }

    private IReadOnlyList<string> Tags(IReadOnlyList<EntityMap> maps) =>
        maps.Select(m => DataTags.Table(m.GetNames(_db.NamingPolicy).Table)).Distinct().ToArray();

    internal sealed record JoinSpec(Type Entity, SqlJoinKind Kind, LambdaExpression On);

    internal sealed record JoinState
    {
        public static readonly JoinState Empty = new();
        public IReadOnlyList<JoinSpec> Joins { get; init; } = [];
        public IReadOnlyList<LambdaExpression> Filters { get; init; } = [];
        public IReadOnlyList<(LambdaExpression Key, bool Descending)> Order { get; init; } = [];
        public int? Take { get; init; }
        public int? Skip { get; init; }
    }

    internal sealed record JoinPlan(SelectStatement Select, IReadOnlyList<EntityMap> Maps, Func<LambdaExpression, TranslationScope> Scope);
}
