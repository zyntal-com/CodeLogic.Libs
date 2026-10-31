using CL.Common.Scheduling;
using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Pipeline;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Retention;

/// <summary>
/// Deletes rows older than their <c>[RetainDays]</c> limit, in batches so a large purge never holds long locks
/// (<c>db.Retention</c>). Schedule it with <see cref="Schedule"/> or call <see cref="PurgeAsync{T}"/> yourself.
/// </summary>
public sealed class DataRetention
{
    /// <summary>Upper bound on batches per table and run, so one run always ends.</summary>
    public const int MaxBatchesPerRun = 1000;

    private readonly Database _db;

    internal DataRetention(Database db) => _db = db;

    /// <summary>Purges the expired rows of <typeparamref name="T"/>; returns how many were deleted.</summary>
    public async ValueTask<Result<long>> PurgeAsync<T>(CancellationToken ct = default)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        return accessor.IsFailure ? accessor.Error! : await PurgeAsync(accessor.Value!.Map, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Purges the expired rows of <paramref name="map"/>'s table; returns how many were deleted. For a <c>[TenantKey]</c>
    /// entity, a tenant view purges only that tenant's rows; without a tenant, every tenant's expired rows are purged.
    /// </summary>
    public async ValueTask<Result<long>> PurgeAsync(EntityMap map, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Retention is not { } column) return DataErrors.Validation($"'{map.ClrType.Name}' has no [RetainDays] column.");
        if (map.Key.Columns.Count == 0) return DataErrors.NoPrimaryKey(map.ClrType.Name);

        var dialect = _db.Provider.Dialect;
        var names = map.GetNames(_db.Config.NamingPolicy);
        var schema = _db.Provider.Capabilities.Schemas ? map.Schema ?? _db.Provider.GetDefaultSchema(_db.Config) : null;
        var table = dialect.QuoteTable(names.Table, schema);
        var predicate = $"{dialect.QuoteIdentifier(names.Of(column))} < {dialect.ParameterName(0)}";
        // From a tenant view (db.ForTenant(id)) only that tenant's expired rows go. Without a tenant this is the system-wide
        // retention job: the retention policy applies to every tenant's rows.
        var tenant = map.TenantKey is { } tenantKey ? _db.CurrentTenant : null;
        if (tenant is not null) predicate += $" AND {dialect.QuoteIdentifier(names.Of(map.TenantKey!))} = {dialect.ParameterName(1)}";
        var batch = Math.Max(1, column.RetentionBatchSize);
        var sql = dialect.RenderBatchedDelete(table, predicate, batch, map.Key.Columns.Select(c => dialect.QuoteIdentifier(names.Of(c))).ToArray());

        var cutoff = Cutoff(column);
        if (cutoff.IsFailure) return cutoff.Error!;
        IReadOnlyList<SqlParameterValue> parameters = tenant is null
            ? [SqlParameterValue.ForColumn(cutoff.Value, column)]
            : [SqlParameterValue.ForColumn(cutoff.Value, column), SqlParameterValue.ForColumn(tenant, map.TenantKey!)];

        long total = 0;
        for (var i = 0; i < MaxBatchesPerRun; i++)
        {
            var deleted = await _db.ExecuteSpecAsync(new CommandSpec(sql, parameters), "delete", names.Table, ct).ConfigureAwait(false);
            if (deleted.IsFailure) return deleted.Error!;
            total += deleted.Value;
            if (deleted.Value < batch) break;
        }

        if (total > 0) _db.Publish(new RetentionPurgedEvent(_db.Name, _db.Provider.Name, _db.Options.Time.GetUtcNow(), names.Table, total));
        return total;
    }

    /// <summary>
    /// Purges every map in <paramref name="maps"/> on <paramref name="scheduler"/> every <paramref name="every"/>.
    /// A failing table does not stop the others; failures surface through the scheduler's job results.
    /// </summary>
    public IJobHandle Schedule(DataScheduler scheduler, IReadOnlyList<EntityMap> maps, TimeSpan every)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(maps);
        var targets = maps.Where(m => m.Retention is not null).ToArray();
        return scheduler.Every($"retention:{_db.Name}", every, async ct =>
        {
            List<string> failures = [];
            foreach (var map in targets)
            {
                var purged = await PurgeAsync(map, ct).ConfigureAwait(false);
                if (purged.IsFailure) failures.Add($"{map.ClrType.Name}: {purged.Error!.Message}");
            }
            if (failures.Count > 0) throw new InvalidOperationException("Retention failed for " + string.Join("; ", failures));
        });
    }

    private Result<object> Cutoff(ColumnMap column)
    {
        var limit = _db.Options.Time.GetUtcNow().AddDays(-column.RetainDays);
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        if (type == typeof(DateTimeOffset)) return limit;
        if (type == typeof(DateTime)) return DateTime.SpecifyKind(limit.UtcDateTime, DateTimeKind.Utc);
        if (type == typeof(DateOnly)) return DateOnly.FromDateTime(limit.UtcDateTime);
        return DataErrors.Validation($"[RetainDays] column '{column.PropertyName}' must be DateTimeOffset, DateTime or DateOnly.");
    }
}
