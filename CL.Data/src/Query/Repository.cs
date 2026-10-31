using System.Linq.Expressions;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Pipeline;
using CL.Data.Sql;
using CL.Data.Translation;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>What an insert-ignore did.</summary>
public enum InsertOutcome
{
    /// <summary>The row was inserted.</summary>
    Inserted,
    /// <summary>A row with the same key already existed; nothing changed.</summary>
    Duplicate,
}

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
        return await SaveAsync(SaveKind.Insert, list, null, async () =>
        {
            var plan = WritePlanner.Insert(accessor.Value!, list, _db.WriteContext);
            if (plan.IsFailure) return plan.Error!;
            return await _db.ExecuteWritesAsync(plan.Value!, list, accessor.Value!, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="save"/> between the interceptors' save hooks (nothing extra when there are none).</summary>
    private async ValueTask<Result<TResult>> SaveAsync<TResult>(SaveKind kind, IReadOnlyList<T> entities, IReadOnlyList<string>? changed,
        Func<ValueTask<Result<TResult>>> save)
    {
        var interceptors = _db.Options.Interceptors;
        if (interceptors.Count == 0) return await save().ConfigureAwait(false);

        var context = new EntitySaveContext(_db, kind, typeof(T), entities.Cast<object>().ToArray(), changed);
        foreach (var interceptor in interceptors)
            if (await interceptor.OnSavingAsync(context).ConfigureAwait(false) is { } stop)
                return stop;
        var result = await save().ConfigureAwait(false);
        foreach (var interceptor in interceptors)
            await interceptor.OnSavedAsync(context, result.IsFailure ? result.Error : null).ConfigureAwait(false);
        return result;
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

    /// <summary>The current column values of <paramref name="entity"/>; pass it to <see cref="UpdateAsync(T, EntitySnapshot{T}, CancellationToken)"/> later.</summary>
    public Result<EntitySnapshot<T>> Snapshot(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var accessor = Accessor();
        return accessor.IsFailure ? accessor.Error! : Result<EntitySnapshot<T>>.Success(accessor.Value!.Snapshot(entity));
    }

    /// <summary>
    /// Writes only the columns of <paramref name="entity"/> that changed since <paramref name="since"/> (plus audit columns and
    /// the concurrency token). When nothing changed, nothing is sent to the database.
    /// </summary>
    public async ValueTask<Result<T>> UpdateAsync(T entity, EntitySnapshot<T> since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(since);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var changed = since.ChangedProperties(entity, accessor.Value!.Map);
        return changed.Count == 0 ? entity : await UpdateCoreAsync(entity, changed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to <paramref name="entity"/> and writes only the columns it changed. When it changed
    /// nothing, nothing is sent to the database.
    /// </summary>
    public ValueTask<Result<T>> UpdateAsync(T entity, Action<T> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var snapshot = Snapshot(entity);
        if (snapshot.IsFailure) return ValueTask.FromResult<Result<T>>(snapshot.Error!);
        change(entity);
        return UpdateAsync(entity, snapshot.Value!, ct);
    }

    /// <summary>
    /// Writes the columns where <paramref name="modified"/> differs from <paramref name="original"/> (detached objects, for
    /// example a stored copy and an edited DTO-mapped instance). When they are equal, nothing is sent to the database.
    /// </summary>
    public ValueTask<Result<T>> UpdateChangedAsync(T original, T modified, CancellationToken ct = default)
    {
        var snapshot = Snapshot(original);
        return snapshot.IsFailure ? ValueTask.FromResult<Result<T>>(snapshot.Error!) : UpdateAsync(modified, snapshot.Value!, ct);
    }

    private async ValueTask<Result<T>> UpdateCoreAsync(T entity, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        return await SaveAsync(SaveKind.Update, [entity], only as IReadOnlyList<string> ?? only?.ToArray(), async () =>
        {
            var plan = WritePlanner.Update(accessor.Value!, entity, _db.WriteContext, only);
            if (plan.IsFailure) return plan.Error!;
            var result = await _db.ExecuteWritesAsync([plan.Value!], [entity], accessor.Value!, ct).ConfigureAwait(false);
            return result.IsSuccess ? Result<T>.Success(entity) : result.Error!;
        }).ConfigureAwait(false);
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
        return await SaveAsync(SaveKind.Delete, [entity], null, async () =>
        {
            var plan = WritePlanner.Delete(accessor.Value!, entity, _db.WriteContext, hard);
            if (plan.IsFailure) return plan.Error!;
            var result = await _db.ExecuteWritesAsync([plan.Value!], [entity], accessor.Value!, ct).ConfigureAwait(false);
            return result.IsSuccess ? Result<bool>.Success(result.Value > 0) : result.Error!;
        }).ConfigureAwait(false);
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

    // ── Upserts and counters ────────────────────────────────────────────────

    /// <summary>
    /// Inserts <paramref name="entity"/>, or updates the existing row with the same conflict key (the primary key by
    /// default; see <see cref="UpsertOptions"/>). Generated identities are not written back by an upsert.
    /// </summary>
    public async ValueTask<Result<T>> UpsertAsync(T entity, UpsertOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var result = await UpsertManyAsync([entity], options, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<T>.Success(entity) : result.Error!;
    }

    /// <summary>Upserts <paramref name="entities"/> in batches; returns the provider's affected-row count.</summary>
    public async ValueTask<Result<int>> UpsertManyAsync(IEnumerable<T> entities, UpsertOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var list = entities as IReadOnlyList<T> ?? entities.ToList();
        return await SaveAsync(options?.IgnoreOnConflict == true ? SaveKind.Insert : SaveKind.Upsert, list, null, async () =>
        {
            var plan = WritePlanner.Upsert(accessor.Value!, list, _db.WriteContext, options);
            if (plan.IsFailure) return plan.Error!;
            return await _db.ExecuteWritesAsync(plan.Value!, list, accessor.Value!, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts <paramref name="seed"/>; on conflict the <paramref name="increments"/> are added to the stored values and
    /// every other property is overwritten. Typical for counters (<c>Hits</c>, <c>Total</c>).
    /// </summary>
    public ValueTask<Result<T>> UpsertWithIncrementsAsync(T seed, IReadOnlyCollection<Expression<Func<T, object?>>> increments,
        CancellationToken ct = default)
    {
        var names = PropertyNames(increments);
        return names.IsFailure
            ? ValueTask.FromResult<Result<T>>(names.Error!)
            : UpsertAsync(seed, new UpsertOptions { IncrementProperties = names.Value! }, ct);
    }

    /// <summary>Inserts <paramref name="entity"/> unless a row with the same key exists; says which happened.</summary>
    public async ValueTask<Result<InsertOutcome>> InsertIgnoreAsync(T entity, CancellationToken ct = default)
    {
        var inserted = await InsertManyIgnoreAsync([entity], ct).ConfigureAwait(false);
        return inserted.IsSuccess ? (inserted.Value > 0 ? InsertOutcome.Inserted : InsertOutcome.Duplicate) : inserted.Error!;
    }

    /// <summary>Inserts the entities whose key does not exist yet; returns how many were inserted.</summary>
    public ValueTask<Result<int>> InsertManyIgnoreAsync(IEnumerable<T> entities, CancellationToken ct = default) =>
        UpsertManyAsync(entities, new UpsertOptions { IgnoreOnConflict = true }, ct);

    /// <summary>Atomically adds <paramref name="delta"/> to <paramref name="property"/> of the row with single-column key <paramref name="id"/>. False when no row matched.</summary>
    public ValueTask<Result<bool>> AdjustAsync<TValue>(object id, Expression<Func<T, TValue>> property, TValue delta, CancellationToken ct = default)
        where TValue : struct, System.Numerics.INumber<TValue> =>
        AdjustCoreAsync(EntityKey.Of(id), property, delta, ct);

    /// <summary>Atomically adds <paramref name="delta"/> to <paramref name="property"/> of the row with <paramref name="key"/>.</summary>
    public ValueTask<Result<bool>> AdjustAsync<TValue>(EntityKey key, Expression<Func<T, TValue>> property, TValue delta, CancellationToken ct = default)
        where TValue : struct, System.Numerics.INumber<TValue> =>
        AdjustCoreAsync(key, property, delta, ct);

    /// <summary>Atomically adds <paramref name="by"/> (default 1) to <paramref name="property"/>.</summary>
    public ValueTask<Result<bool>> IncrementAsync<TValue>(object id, Expression<Func<T, TValue>> property, TValue? by = null, CancellationToken ct = default)
        where TValue : struct, System.Numerics.INumber<TValue> =>
        AdjustCoreAsync(EntityKey.Of(id), property, by ?? TValue.One, ct);

    /// <summary>Atomically subtracts <paramref name="by"/> (default 1) from <paramref name="property"/>.</summary>
    public ValueTask<Result<bool>> DecrementAsync<TValue>(object id, Expression<Func<T, TValue>> property, TValue? by = null, CancellationToken ct = default)
        where TValue : struct, System.Numerics.INumber<TValue> =>
        AdjustCoreAsync(EntityKey.Of(id), property, -(by ?? TValue.One), ct);

    private async ValueTask<Result<bool>> AdjustCoreAsync<TValue>(EntityKey key, Expression<Func<T, TValue>> property, TValue delta, CancellationToken ct)
        where TValue : struct
    {
        var accessor = Accessor();
        if (accessor.IsFailure) return accessor.Error!;
        var map = accessor.Value!.Map;
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : property.Body;
        if (body is not MemberExpression { Expression: ParameterExpression } member || map.FindProperty(member.Member.Name) is not { } column)
            return DataErrors.Validation($"'{property}' is not a mapped property of {typeof(T).Name}.");

        var plan = WritePlanner.Adjust(map, key, column, delta, _db.WriteContext);
        if (plan.IsFailure) return plan.Error!;
        var result = await _db.ExecuteWritesAsync([plan.Value!], Array.Empty<T>(), accessor.Value!, ct).ConfigureAwait(false);
        return result.IsSuccess ? Result<bool>.Success(result.Value > 0) : result.Error!;
    }

    private static Result<IReadOnlyList<string>> PropertyNames(IReadOnlyCollection<Expression<Func<T, object?>>> properties)
    {
        List<string> names = [];
        foreach (var property in properties)
        {
            var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : property.Body;
            if (body is not MemberExpression { Expression: ParameterExpression } member)
                return DataErrors.Validation($"'{property}' is not a property of {typeof(T).Name}.");
            names.Add(member.Member.Name);
        }
        return names;
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
        // The entity cache (DataCacheConfig.EntityCache) serves repeated key lookups; any write to the table drops them.
        var entityCache = _db.Options.Cache is { Config.EntityCache: true } cache
            ? CL.Data.Caching.CachePolicy.For(TimeSpan.FromSeconds(cache.Config.EntityCacheTtlSeconds))
            : null;
        var rows = await _db.QueryAsync(select.Value! with { Limit = Limit(1) }, accessor.Value, names.Table,
            [DataTags.Entity(names.Table, key)], ct, entityCache).ConfigureAwait(false);
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
