using System.Linq.Expressions;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>
/// Key lookups and entity writes for <typeparamref name="T"/>. Reads hide soft-deleted rows and other tenants'
/// rows; writes go through the shared write planner (auditing, concurrency, tenants, identity write-back).
/// Obtain it with <c>db.Repository&lt;T&gt;()</c>.
/// </summary>
/// <typeparam name="T">A <c>[Table]</c> entity.</typeparam>
public sealed class Repository<T>
{
    private readonly Database _db;

    internal Repository(Database db) => _db = db;

    // ── Writes ──────────────────────────────────────────────────────────────

    /// <summary>Inserts <paramref name="entity"/> and writes generated values (identity, audit, token) back to it.</summary>
    public async ValueTask<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var result = await InsertManyAsync([entity], ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<T>.Success(entity) : result.Error!;
    }

    /// <summary>Inserts <paramref name="entities"/> in batches that fit the provider's limits; returns the rows inserted.</summary>
    public async ValueTask<Result<int>> InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var list = entities as IReadOnlyList<T> ?? entities.ToList();
        var plan = WritePlanner.Insert(accessor.Value!, list, _db.WriteContext);
        if (plan.IsFailure) return plan.Error!;
        return await _db.ExecuteWritesAsync(plan.Value!, list, accessor.Value!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates <paramref name="entity"/> by key. Fails with <c>data.concurrency</c> when its concurrency token changed,
    /// or <c>data.not_found</c> when the row is gone.
    /// </summary>
    public ValueTask<Result<T>> UpdateAsync(T entity, CancellationToken ct = default) => UpdateCoreAsync(entity, null, ct);

    /// <summary>Updates only <paramref name="properties"/> of <paramref name="entity"/> (plus audit columns and the concurrency token).</summary>
    public ValueTask<Result<T>> UpdateAsync(T entity, IReadOnlyCollection<Expression<Func<T, object?>>> properties, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        HashSet<string> names = [];
        foreach (var property in properties)
        {
            var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : property.Body;
            if (body is not MemberExpression { Expression: ParameterExpression } member)
                return ValueTask.FromResult<Result<T>>(DataErrors.Validation($"'{property}' is not a property of {typeof(T).Name}."));
            names.Add(member.Member.Name);
        }
        return UpdateCoreAsync(entity, names, ct);
    }

    private async ValueTask<Result<T>> UpdateCoreAsync(T entity, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var plan = WritePlanner.Update(accessor.Value!, entity, _db.WriteContext, only);
        if (plan.IsFailure) return plan.Error!;
        var result = await _db.ExecuteWritesAsync([plan.Value!], [entity], accessor.Value!, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<T>.Success(entity) : result.Error!;
    }

    /// <summary>
    /// Deletes <paramref name="entity"/>: sets its <c>[SoftDelete]</c> timestamp when it has one, otherwise removes the row.
    /// Returns false when the row was already gone.
    /// </summary>
    public ValueTask<Result<bool>> DeleteAsync(T entity, CancellationToken ct = default) => DeleteCoreAsync(entity, hard: false, ct);

    /// <summary>Removes the row of <paramref name="entity"/>, even when the entity uses soft delete.</summary>
    public ValueTask<Result<bool>> HardDeleteAsync(T entity, CancellationToken ct = default) => DeleteCoreAsync(entity, hard: true, ct);

    private async ValueTask<Result<bool>> DeleteCoreAsync(T entity, bool hard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var plan = WritePlanner.Delete(accessor.Value!, entity, _db.WriteContext, hard);
        if (plan.IsFailure) return plan.Error!;
        var result = await _db.ExecuteWritesAsync([plan.Value!], [entity], accessor.Value!, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<bool>.Success(result.Value > 0) : result.Error!;
    }

    /// <summary>Deletes by a single-column key (soft delete when the entity has one). Returns false when no row matched.</summary>
    public ValueTask<Result<bool>> DeleteByIdAsync(object id, CancellationToken ct = default) => DeleteByKeyAsync(EntityKey.Of(id), hard: false, ct);

    /// <summary>Deletes by key; <paramref name="hard"/> removes the row even for soft-delete entities.</summary>
    public async ValueTask<Result<bool>> DeleteByKeyAsync(EntityKey key, bool hard = false, CancellationToken ct = default)
    {
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var plan = WritePlanner.DeleteByKey(accessor.Value!.Map, key, _db.WriteContext, hard);
        if (plan.IsFailure) return plan.Error!;
        var result = await _db.ExecuteWritesAsync([plan.Value!], Array.Empty<T>(), accessor.Value!, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<bool>.Success(result.Value > 0) : result.Error!;
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    /// <summary>The row with single-column key <paramref name="id"/>, or null.</summary>
    public ValueTask<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default) => GetByKeyAsync(EntityKey.Of(id), ct);

    /// <summary>The row with <paramref name="key"/> (composite keys in key order), or null.</summary>
    public async ValueTask<Result<T?>> GetByKeyAsync(EntityKey key, CancellationToken ct = default)
    {
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var names = map.GetNames(_db.NamingPolicy);
        var keyWhere = ReadPlanner.KeyPredicate(map, names, key);
        if (keyWhere.IsFailure) return keyWhere.Error!;

        var select = ReadPlanner.Select(_db, map, keyWhere.Value, includeDeleted: false);
        if (select.IsFailure) return select.Error!;
        var rows = await _db.QueryAsync(select.Value! with { Limit = Limit(1) }, accessor.Value, names.Table,
            [DataTags.Entity(names.Table, key)], ct).ConfigureAwait(false);
        return rows.IsSuccess ? Result<T?>.Success(rows.Value!.Count > 0 ? rows.Value[0] : default) : rows.Error!;
    }

    /// <summary>Every row (soft-deleted rows excluded).</summary>
    public ValueTask<Result<List<T>>> GetAllAsync(CancellationToken ct = default) => QueryCoreAsync(null, null, null, ct);

    /// <summary>Rows matching <paramref name="predicate"/>.</summary>
    public ValueTask<Result<List<T>>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return QueryCoreAsync(predicate, null, null, ct);
    }

    /// <summary>
    /// One page ordered by key. The total is only counted when <paramref name="withTotal"/> is true (a COUNT can be
    /// expensive); <see cref="PagedResult{T}.HasMore"/> is always set.
    /// </summary>
    public async ValueTask<Result<PagedResult<T>>> GetPagedAsync(int page, int pageSize, bool withTotal = false, CancellationToken ct = default)
    {
        if (page < 1 || pageSize < 1) return DataErrors.Validation("Page and page size must be at least 1.");
        var rows = await QueryCoreAsync(null, pageSize + 1, (long)(page - 1) * pageSize, ct).ConfigureAwait(false);
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

    /// <summary>Number of rows (soft-deleted rows excluded).</summary>
    public ValueTask<Result<long>> CountAsync(CancellationToken ct = default) => CountCoreAsync(null, ct);

    /// <summary>Number of rows matching <paramref name="predicate"/>.</summary>
    public ValueTask<Result<long>> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return CountCoreAsync(predicate, ct);
    }

    private async ValueTask<Result<List<T>>> QueryCoreAsync(Expression<Func<T, bool>>? predicate, int? limit, long? offset, CancellationToken ct)
    {
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var names = map.GetNames(_db.NamingPolicy);
        var where = Translate(predicate, map);
        if (where.IsFailure) return where.Error!;

        var select = ReadPlanner.Select(_db, map, where.Value, includeDeleted: false);
        if (select.IsFailure) return select.Error!;
        var statement = select.Value!;
        if (limit is not null || offset is not null)
            statement = statement with
            {
                OrderBy = ReadPlanner.KeyOrder(map, names),
                Limit = limit is { } l ? Limit(l) : null,
                Offset = offset is > 0 ? new ParameterExpr(SqlParameterValue.Of(offset.Value)) : null,
            };
        return await _db.QueryAsync(statement, accessor.Value, names.Table, [DataTags.Table(names.Table)], ct).ConfigureAwait(false);
    }

    private async ValueTask<Result<long>> CountCoreAsync(Expression<Func<T, bool>>? predicate, CancellationToken ct)
    {
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var names = map.GetNames(_db.NamingPolicy);
        var where = Translate(predicate, map);
        if (where.IsFailure) return where.Error!;

        var select = ReadPlanner.Select(_db, map, where.Value, includeDeleted: false);
        if (select.IsFailure) return select.Error!;
        var count = select.Value! with { Projection = [new ProjectionItem(new AggregateExpr(SqlAggregate.Count, null))] };
        var result = await _db.ScalarAsync<long>(count, names.Table, [DataTags.Table(names.Table)], ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<long>.Success(result.Value) : result.Error!;
    }

    private Result<SqlExpr?> Translate(Expression<Func<T, bool>>? predicate, EntityMap map)
    {
        if (predicate is null) return Result<SqlExpr?>.Success(null);
        var scope = new TranslationScope(_db.Provider.Dialect, _db.NamingPolicy).Bind(predicate.Parameters[0], map);
        var translated = SqlTranslator.TranslatePredicate(predicate, scope);
        return translated.IsSuccess ? Result<SqlExpr?>.Success(translated.Value) : translated.Error!;
    }

    private static ParameterExpr Limit(int value) => new(SqlParameterValue.Of(value));

    private static Result<IEntityAccessor<T>> Accessor() => EntityAccessorRegistry.Get<T>();
}
