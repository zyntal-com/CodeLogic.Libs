using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Writes;

/// <summary>Cache tags used by writes (invalidation) and reads (dependencies).</summary>
public static class DataTags
{
    /// <summary>Tag of everything read from <paramref name="table"/>.</summary>
    public static string Table(string table) => "table:" + table;

    /// <summary>Tag of one row of <paramref name="table"/>.</summary>
    public static string Entity(string table, EntityKey key) => $"entity:{table}:{key}";
}

/// <summary>Options for upserts.</summary>
public sealed record UpsertOptions
{
    /// <summary>Properties whose values identify an existing row; null uses the primary key.</summary>
    public IReadOnlyList<string>? ConflictProperties { get; init; }

    /// <summary>Properties overwritten when the row exists; null overwrites every non-key property.</summary>
    public IReadOnlyList<string>? UpdateProperties { get; init; }

    /// <summary>Properties added to the existing value instead of overwriting it (counters).</summary>
    public IReadOnlyList<string> IncrementProperties { get; init; } = [];

    /// <summary>Insert only when no row with the key exists; leave existing rows unchanged.</summary>
    public bool IgnoreOnConflict { get; init; }
}

/// <summary>
/// Plans entity writes as SQL statements. Every write path goes through here, so auditing (<c>[CreatedAt]</c>,
/// <c>[UpdatedAt]</c>, <c>[CreatedBy]</c>, <c>[UpdatedBy]</c>), optimistic concurrency, tenant stamping and checks,
/// soft delete, identity handling, server-generated columns, batching and cache tags behave the same everywhere.
/// Planning stamps audit, tenant and token values onto the entities.
/// </summary>
public static class WritePlanner
{
    // ── Insert ──────────────────────────────────────────────────────────────

    /// <summary>Plans INSERTs for <paramref name="entities"/>, split into batches that fit the provider's limits.</summary>
    public static Result<IReadOnlyList<WriteCommand>> Insert<T>(IEntityAccessor<T> accessor, IReadOnlyList<T> entities, WriteContext ctx)
    {
        var map = accessor.Map;
        if (!map.IsTable) return DataErrors.Unsupported($"'{map.ClrType.Name}' is a [DataRow] type and cannot be written.");
        if (entities.Count == 0) return Result<IReadOnlyList<WriteCommand>>.Success([]);

        var now = ctx.Time.GetUtcNow();
        foreach (var entity in entities)
            if (Stamp(accessor, entity, ctx, now, insert: true) is { } error) return error;

        var names = map.GetNames(ctx.Policy);
        var table = TableOf(map, names, ctx);
        List<WriteCommand> commands = [];

        // Rows that bring their own identity value and rows that let the server generate it need different column lists.
        var identity = map.Identity;
        var groups = identity is null
            ? [(Explicit: true, Rows: Enumerable.Range(0, entities.Count).ToList())]
            : Enumerable.Range(0, entities.Count).GroupBy(i => !IsDefault(accessor.GetValue(entities[i], identity.Ordinal)))
                .Select(g => (Explicit: g.Key, Rows: g.ToList())).ToList();

        foreach (var (explicitIdentity, rows) in groups)
        {
            var columns = map.Columns.Where(c => IsWritable(c) && (explicitIdentity || !c.Has(ColumnRoles.Identity))).ToArray();
            var generated = map.Columns.Where(c => (!explicitIdentity && c.Has(ColumnRoles.Identity)) || c.DataType == DataType.RowVersion).ToArray();
            var useReturning = generated.Length > 0 && ctx.Capabilities.Returning;
            var perStatement = RowsPerStatement(columns.Length, ctx, 1);
            // Without RETURNING, generated identities come from the last-insert-id, which covers one statement at a time.

            foreach (var chunk in rows.Chunk(perStatement))
            {
                var values = chunk.Select(i => (IReadOnlyList<SqlExpr>)columns.Select(c => Value(accessor, entities[i], c)).ToArray()).ToArray();
                var statement = new InsertStatement(table, columns.Select(names.Of).ToArray(), values)
                {
                    Returning = useReturning ? generated.Select(names.Of).ToArray() : [],
                };
                commands.Add(new WriteCommand(statement, WriteKind.Insert, names.Table, chunk.Length, [DataTags.Table(names.Table)])
                {
                    FirstRow = chunk[0],
                    ReturnedColumns = useReturning ? generated : [],
                    IdentityFromLastInsertId = !useReturning && !explicitIdentity ? identity : null,
                });
            }
        }

        return commands;
    }

    // ── Update ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Plans an UPDATE of <paramref name="entity"/> by key. With <paramref name="onlyProperties"/>, only those properties
    /// (plus audit columns and the concurrency token) are written.
    /// </summary>
    public static Result<WriteCommand> Update<T>(IEntityAccessor<T> accessor, T entity, WriteContext ctx,
        IReadOnlyCollection<string>? onlyProperties = null)
    {
        var map = accessor.Map;
        if (map.Key.IsEmpty) return DataErrors.NoPrimaryKey(map.ClrType.Name);
        if (Stamp(accessor, entity, ctx, ctx.Time.GetUtcNow(), insert: false) is { } error) return error;

        var names = map.GetNames(ctx.Policy);
        List<SetItem> set = [];
        foreach (var column in map.Columns)
        {
            if (!IsWritable(column) || column.Has(ColumnRoles.Key) || column.Has(ColumnRoles.Identity) || column.Has(ColumnRoles.CreatedAt)
                || column.Has(ColumnRoles.CreatedBy) || column.Has(ColumnRoles.TenantKey) || column.Has(ColumnRoles.ConcurrencyToken))
                continue;
            var forced = column.Has(ColumnRoles.UpdatedAt) || column.Has(ColumnRoles.UpdatedBy);
            if (onlyProperties is not null && !forced && !onlyProperties.Contains(column.PropertyName)) continue;
            set.Add(new SetItem(names.Of(column), Value(accessor, entity, column)));
        }

        var key = accessor.GetKey(entity);
        var where = KeyPredicate(map, names, key);
        where = And(where, TenantPredicate(map, names, ctx));

        Action? onSuccess = null;
        List<ColumnMap> returned = [];
        if (map.ConcurrencyToken is { } token)
        {
            var current = accessor.GetValue(entity, token.Ordinal);
            if (token.DataType == DataType.RowVersion || token.ValueType == typeof(byte[]))
            {
                where = And(where, Equal(names.Of(token), current, token));
                if (map.Columns.Any(c => c.DataType == DataType.RowVersion) && ctx.Capabilities.Returning) returned.Add(token);
            }
            else
            {
                var next = token.ValueType == typeof(long) ? (object)(Convert.ToInt64(current) + 1) : Convert.ToInt32(current) + 1;
                set.Add(new SetItem(names.Of(token), new ParameterExpr(SqlParameterValue.ForColumn(next, token))));
                where = And(where, Equal(names.Of(token), current, token));
                onSuccess = () => accessor.SetValue(entity, token.Ordinal, next);
            }
        }

        if (set.Count == 0) return DataErrors.Validation($"Nothing to update on '{map.ClrType.Name}'.");

        var statement = new UpdateStatement(TableOf(map, names, ctx), set)
        {
            Where = where,
            Returning = returned.Select(names.Of).ToArray(),
        };
        return new WriteCommand(statement, WriteKind.Update, names.Table, 1, Tags(names.Table, key))
        {
            Check = RowCheck.ExactlyOne,
            ReturnedColumns = returned,
            OnSuccess = onSuccess,
        };
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Plans the delete of <paramref name="entity"/>: a soft delete (setting the <c>[SoftDelete]</c> timestamp) unless
    /// <paramref name="hard"/> or the entity has no soft-delete column. Checks the concurrency token when there is one.
    /// </summary>
    public static Result<WriteCommand> Delete<T>(IEntityAccessor<T> accessor, T entity, WriteContext ctx, bool hard = false)
    {
        var map = accessor.Map;
        if (map.Key.IsEmpty) return DataErrors.NoPrimaryKey(map.ClrType.Name);
        var names = map.GetNames(ctx.Policy);
        object? token = map.ConcurrencyToken is { } t ? accessor.GetValue(entity, t.Ordinal) : null;
        var planned = DeleteCore(map, names, accessor.GetKey(entity), ctx, hard, token);
        if (planned.IsFailure) return planned;

        var command = planned.Value!;
        if (command.Kind == WriteKind.Delete && command.Statement is UpdateStatement && map.SoftDelete is { } soft)
        {
            var at = command.SoftDeletedAt!.Value;
            command = command with
            {
                OnSuccess = () => accessor.SetValue(entity, soft.Ordinal, soft.ValueType == typeof(DateTime) ? (object)at.UtcDateTime : at),
            };
        }
        return map.ConcurrencyToken is not null ? command with { Check = RowCheck.ExactlyOne } : command;
    }

    /// <summary>Plans a delete by key (soft unless <paramref name="hard"/>). The affected-row count says whether the row existed.</summary>
    public static Result<WriteCommand> DeleteByKey(EntityMap map, EntityKey key, WriteContext ctx, bool hard = false)
    {
        if (map.Key.IsEmpty) return DataErrors.NoPrimaryKey(map.ClrType.Name);
        if (key.Count != map.Key.Columns.Count)
            return DataErrors.Validation($"'{map.ClrType.Name}' has a key of {map.Key.Columns.Count} column(s); {key.Count} value(s) were given.");
        return DeleteCore(map, map.GetNames(ctx.Policy), key, ctx, hard, token: null);
    }

    private static Result<WriteCommand> DeleteCore(EntityMap map, EntityNames names, EntityKey key, WriteContext ctx, bool hard, object? token)
    {
        var where = And(KeyPredicate(map, names, key), TenantPredicate(map, names, ctx));
        if (token is not null && map.ConcurrencyToken is { } t) where = And(where, Equal(names.Of(t), token, t));
        var table = TableOf(map, names, ctx);

        if (!hard && map.SoftDelete is { } soft)
        {
            var now = ctx.Time.GetUtcNow();
            List<SetItem> set = [new SetItem(names.Of(soft), TimeValue(soft, now))];
            if (map.UpdatedAt is { } updatedAt) set.Add(new SetItem(names.Of(updatedAt), TimeValue(updatedAt, now)));
            if (map.UpdatedBy is { } updatedBy && ctx.User is not null)
                set.Add(new SetItem(names.Of(updatedBy), new ParameterExpr(SqlParameterValue.ForColumn(ctx.User, updatedBy))));
            where = And(where, new IsNullExpr(new ColumnExpr(names.Of(soft))));
            return new WriteCommand(new UpdateStatement(table, set) { Where = where }, WriteKind.Delete, names.Table, 1, Tags(names.Table, key))
            {
                SoftDeletedAt = now,
            };
        }

        return new WriteCommand(new DeleteStatement(table) { Where = where }, WriteKind.Delete, names.Table, 1, Tags(names.Table, key));
    }

    // ── Upsert ──────────────────────────────────────────────────────────────

    /// <summary>Plans upserts (insert, or update/ignore when the conflict key exists), split into batches.</summary>
    public static Result<IReadOnlyList<WriteCommand>> Upsert<T>(IEntityAccessor<T> accessor, IReadOnlyList<T> entities, WriteContext ctx,
        UpsertOptions? options = null)
    {
        options ??= new UpsertOptions();
        var map = accessor.Map;
        if (!map.IsTable) return DataErrors.Unsupported($"'{map.ClrType.Name}' is a [DataRow] type and cannot be written.");
        if (entities.Count == 0) return Result<IReadOnlyList<WriteCommand>>.Success([]);

        var now = ctx.Time.GetUtcNow();
        foreach (var entity in entities)
            if (Stamp(accessor, entity, ctx, now, insert: true) is { } error) return error;

        var names = map.GetNames(ctx.Policy);
        var identityGenerated = map.Identity is { } id && entities.Any(e => IsDefault(accessor.GetValue(e, id.Ordinal)));
        var columns = map.Columns.Where(c => IsWritable(c) && !(identityGenerated && c.Has(ColumnRoles.Identity))).ToArray();

        ColumnMap[] conflict;
        if (options.ConflictProperties is { Count: > 0 } props)
        {
            var resolved = props.Select(map.FindProperty).ToArray();
            if (resolved.Any(c => c is null))
                return DataErrors.Validation($"Conflict properties of '{map.ClrType.Name}' must be mapped: {string.Join(", ", props)}.");
            conflict = resolved!;
        }
        else
        {
            conflict = map.Key.Columns.ToArray();
            if (conflict.Length == 0 || conflict.Any(c => !columns.Contains(c)))
            {
                var unique = map.Indexes.FirstOrDefault(i => i.Unique && i.Columns.All(c => columns.Contains(c.Column)));
                if (unique is null)
                    return DataErrors.Validation(
                        $"Upsert on '{map.ClrType.Name}' needs a conflict key: give the key values, declare a unique index, or set ConflictProperties.");
                conflict = unique.Columns.Select(c => c.Column).ToArray();
            }
        }

        foreach (var name in options.IncrementProperties.Concat(options.UpdateProperties ?? []))
            if (map.FindProperty(name) is null)
                return DataErrors.Validation($"'{name}' is not a mapped property of '{map.ClrType.Name}'.");

        List<SetItem> updateSet = [];
        if (!options.IgnoreOnConflict)
        {
            foreach (var column in columns)
            {
                if (conflict.Contains(column) || column.Has(ColumnRoles.Key) || column.Has(ColumnRoles.CreatedAt)
                    || column.Has(ColumnRoles.CreatedBy) || column.Has(ColumnRoles.TenantKey) || column.Has(ColumnRoles.Identity))
                    continue;
                var name = names.Of(column);
                if (column.Has(ColumnRoles.ConcurrencyToken) && column.ValueType != typeof(byte[]))
                    updateSet.Add(new SetItem(name, new BinaryExpr(SqlBinaryOperator.Add, new ColumnExpr(name), new LiteralExpr(1))));
                else if (options.IncrementProperties.Contains(column.PropertyName))
                    updateSet.Add(new SetItem(name, new BinaryExpr(SqlBinaryOperator.Add, new ColumnExpr(name), new ExcludedExpr(name))));
                else if (options.UpdateProperties is null || options.UpdateProperties.Contains(column.PropertyName)
                         || column.Has(ColumnRoles.UpdatedAt) || column.Has(ColumnRoles.UpdatedBy))
                    updateSet.Add(new SetItem(name, new ExcludedExpr(name)));
            }
        }

        var table = TableOf(map, names, ctx);
        var perStatement = RowsPerStatement(columns.Length, ctx, ctx.Dialect.UpsertParameterMultiplier);
        List<WriteCommand> commands = [];
        var first = 0;
        foreach (var chunk in entities.Chunk(perStatement))
        {
            var values = chunk.Select(e => (IReadOnlyList<SqlExpr>)columns.Select(c => Value(accessor, e, c)).ToArray()).ToArray();
            var insert = new InsertStatement(table, columns.Select(names.Of).ToArray(), values);
            var statement = new UpsertStatement(insert, conflict.Select(names.Of).ToArray())
            {
                UpdateSet = updateSet,
                IgnoreOnConflict = options.IgnoreOnConflict || updateSet.Count == 0,
            };
            commands.Add(new WriteCommand(statement, WriteKind.Upsert, names.Table, chunk.Length, [DataTags.Table(names.Table)])
            {
                FirstRow = first,
                RequiresTransaction = ctx.Dialect.UpsertRequiresTransaction,
            });
            first += chunk.Length;
        }
        return commands;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>The table reference of <paramref name="map"/>: declared schema, else the connection default when schemas exist.</summary>
    public static TableRef TableOf(EntityMap map, EntityNames names, WriteContext ctx) =>
        new(names.Table, ctx.Capabilities.Schemas ? map.Schema ?? ctx.DefaultSchema : null);

    /// <summary><c>key1 = @p AND key2 = @p</c> for <paramref name="key"/>.</summary>
    public static SqlExpr KeyPredicate(EntityMap map, EntityNames names, EntityKey key)
    {
        SqlExpr? where = null;
        for (var i = 0; i < map.Key.Columns.Count; i++)
        {
            var column = map.Key.Columns[i];
            where = And(where, Equal(names.Of(column), key[i], column));
        }
        return where!;
    }

    /// <summary><c>tenant = @tenant</c> when the entity is tenant-scoped and a tenant is set.</summary>
    public static SqlExpr? TenantPredicate(EntityMap map, EntityNames names, WriteContext ctx) =>
        map.TenantKey is { } tenant && ctx.Tenant is not null ? Equal(names.Of(tenant), ctx.Tenant, tenant) : null;

    private static Error? Stamp<T>(IEntityAccessor<T> accessor, T entity, WriteContext ctx, DateTimeOffset now, bool insert)
    {
        var map = accessor.Map;
        if (insert && map.CreatedAt is { } createdAt) SetTime(accessor, entity, createdAt, now);
        if (map.UpdatedAt is { } updatedAt) SetTime(accessor, entity, updatedAt, now);
        if (ctx.User is not null)
        {
            if (insert && map.CreatedBy is { } createdBy) accessor.SetValue(entity, createdBy.Ordinal, ctx.User);
            if (map.UpdatedBy is { } updatedBy) accessor.SetValue(entity, updatedBy.Ordinal, ctx.User);
        }

        if (map.TenantKey is { } tenant)
        {
            var current = accessor.GetValue(entity, tenant.Ordinal);
            if (ctx.Tenant is not null)
            {
                if (insert) accessor.SetValue(entity, tenant.Ordinal, ctx.Tenant);
                else if (!IsDefault(current) && !Equals(EntityKey.Of(current), EntityKey.Of(ctx.Tenant)))
                    return DataErrors.Validation($"'{map.ClrType.Name}' belongs to another tenant.");
            }
            else if (IsDefault(current))
            {
                return DataErrors.TenantMissing(map.ClrType.Name);
            }
        }

        if (insert && map.ConcurrencyToken is { } token && token.ValueType != typeof(byte[]) && IsDefault(accessor.GetValue(entity, token.Ordinal)))
            accessor.SetValue(entity, token.Ordinal, token.ValueType == typeof(long) ? 1L : 1);
        return null;
    }

    private static void SetTime<T>(IEntityAccessor<T> accessor, T entity, ColumnMap column, DateTimeOffset now) =>
        accessor.SetValue(entity, column.Ordinal, column.ValueType == typeof(DateTime) ? (object)now.UtcDateTime : now);

    private static SqlExpr TimeValue(ColumnMap column, DateTimeOffset now) =>
        new ParameterExpr(column.ValueType == typeof(DateTime) ? SqlParameterValue.For(now.UtcDateTime, column) : SqlParameterValue.For(now, column));

    private static bool IsWritable(ColumnMap column) => column.DataType != DataType.RowVersion;

    private static SqlExpr Value<T>(IEntityAccessor<T> accessor, T entity, ColumnMap column)
    {
        var collector = new ValueCollector();
        accessor.WriteColumn(entity, column.Ordinal, collector);
        return collector.Result is null ? NullExpr.Instance : new ParameterExpr(collector.Result);
    }

    private static SqlExpr Equal(string column, object? value, ColumnMap map) =>
        value is null
            ? new IsNullExpr(new ColumnExpr(column))
            : new BinaryExpr(SqlBinaryOperator.Equal, new ColumnExpr(column), new ParameterExpr(SqlParameterValue.ForColumn(value, map)));

    private static SqlExpr? And(SqlExpr? left, SqlExpr? right) =>
        left is null ? right : right is null ? left : new BinaryExpr(SqlBinaryOperator.And, left, right);

    private static IReadOnlyList<string> Tags(string table, EntityKey key) => [DataTags.Table(table), DataTags.Entity(table, key)];

    private static int RowsPerStatement(int columns, WriteContext ctx, int multiplier)
    {
        var byParameters = ctx.Capabilities.ParameterLimit / Math.Max(1, columns * Math.Max(1, multiplier));
        return Math.Max(1, Math.Min(ctx.MaxBatchSize, byParameters));
    }

    internal static bool IsDefault(object? value) => value switch
    {
        null => true,
        int i => i == 0,
        long l => l == 0,
        short s => s == 0,
        byte b => b == 0,
        uint ui => ui == 0,
        ulong ul => ul == 0,
        Guid g => g == Guid.Empty,
        string s => s.Length == 0,
        decimal m => m == 0,
        DateTime d => d == default,
        DateTimeOffset dto => dto == default,
        // Typed ids and other converted values: default when their stored form is.
        _ when CL.Data.Mapping.Conversion.ValueConverters.TryGet(value.GetType(), out var converter) => IsDefault(converter.ToStoreObject(value)),
        _ => false,
    };

    private sealed class ValueCollector : IColumnValueWriter
    {
        public SqlParameterValue? Result;

        public void Write<TValue>(ColumnMap column, TValue value) => Result = value is null ? null : SqlParameterValue.For(value, column);
    }
}
