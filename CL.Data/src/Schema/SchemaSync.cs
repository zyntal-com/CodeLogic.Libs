using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using SqlKit = CL.Data.Sql.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Schema;

/// <summary>What table sync did with one table.</summary>
public enum TableSyncOutcome
{
    /// <summary>The model's fingerprint matched the last sync; the database was not inspected.</summary>
    Unchanged,
    /// <summary>The table already matched the model.</summary>
    UpToDate,
    /// <summary>The table was created.</summary>
    Created,
    /// <summary>Changes were applied.</summary>
    Altered,
    /// <summary>Some changes are not allowed by the sync level and were left pending (drift).</summary>
    Drift,
}

/// <summary>The result for one table.</summary>
/// <param name="Table">Table name (schema-qualified when the provider has schemas).</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Applied">Changes applied.</param>
/// <param name="Pending">Changes left pending (drift).</param>
public sealed record TableSyncResult(string Table, TableSyncOutcome Outcome, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);

/// <summary>The result of a sync run.</summary>
/// <param name="Tables">Per-table results, in the order they were synced.</param>
public sealed record SchemaSyncReport(IReadOnlyList<TableSyncResult> Tables)
{
    /// <summary>True when any table has pending changes.</summary>
    public bool HasDrift => Tables.Any(t => t.Outcome == TableSyncOutcome.Drift);
}

/// <summary>Options for one sync run.</summary>
public sealed record SchemaSyncOptions
{
    /// <summary>Inspect every table even when its fingerprint matches the last sync.</summary>
    public bool Force { get; init; }

    /// <summary>Overrides the connection's sync level for this run.</summary>
    public SchemaSyncLevel? Level { get; init; }

    /// <summary>How long to wait for another process's sync to finish.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Brings tables in line with their entity models. Per table: build the desired schema, skip it when its
/// fingerprint matches the last sync, otherwise read the table from the catalog, plan the changes and apply the
/// ones the sync level allows. Changes outside the level are reported as drift
/// (<see cref="SchemaDriftDetectedEvent"/>). Runs under an advisory lock so only one process syncs at a time,
/// applies each table's changes in one transaction where the database supports transactional DDL, and records
/// fingerprints in <c>cl_data_schema</c>.
/// </summary>
public sealed class SchemaSync
{
    /// <summary>Name of the table that stores the fingerprint of each synced table.</summary>
    public const string StateTable = "cl_data_schema";

    private const string LockName = "cl_data_schema_sync";
    private readonly Database _db;

    internal SchemaSync(Database db) => _db = db;

    /// <summary>Syncs the table of <typeparamref name="T"/>.</summary>
    public ValueTask<Result<SchemaSyncReport>> SyncAsync<T>(SchemaSyncOptions? options = null, CancellationToken ct = default)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        return accessor.IsFailure
            ? ValueTask.FromResult<Result<SchemaSyncReport>>(accessor.Error!)
            : SyncAsync([accessor.Value!.Map], options, ct);
    }

    /// <summary>
    /// Syncs the tables of <paramref name="maps"/>. Tables referenced by foreign keys are synced before the tables
    /// that reference them.
    /// </summary>
    public async ValueTask<Result<SchemaSyncReport>> SyncAsync(IReadOnlyList<EntityMap> maps, SchemaSyncOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(maps);
        options ??= new SchemaSyncOptions();
        var level = options.Level ?? _db.Config.EffectiveSyncLevel;
        var provider = _db.Provider;
        if (provider.Introspector is not { } introspector) return DataErrors.Unsupported($"{provider.Name} has no schema introspector.");

        // One process at a time: the lock lives on its own connection for the whole run.
        DataLock? held = null;
        if (provider.Capabilities.AdvisoryLocks)
        {
            var acquired = await _db.AcquireLockAsync(LockName, options.LockTimeout, ct).ConfigureAwait(false);
            if (acquired.IsFailure) return acquired.Error!;
            held = acquired.Value!;
        }

        try
        {
            var state = await LoadStateAsync(introspector, ct).ConfigureAwait(false);
            if (state.IsFailure) return state.Error!;

            List<TableSyncResult> results = [];
            foreach (var map in Order(maps))
            {
                var result = await SyncTableAsync(map, level, options.Force, state.Value!, introspector, ct).ConfigureAwait(false);
                if (result.IsFailure) return result.Error!;
                results.Add(result.Value!);
            }
            return new SchemaSyncReport(results);
        }
        finally
        {
            if (held is not null) await held.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Plans the changes for <typeparamref name="T"/> at <paramref name="level"/> without applying anything (drift checks).</summary>
    public async ValueTask<Result<SchemaPlan>> PlanAsync<T>(SchemaSyncLevel level = SchemaSyncLevel.Full, CancellationToken ct = default)
    {
        var accessor = EntityAccessorRegistry.Get<T>();
        if (accessor.IsFailure) return accessor.Error!;
        if (_db.Provider.Introspector is not { } introspector) return DataErrors.Unsupported($"{_db.Provider.Name} has no schema introspector.");
        var desired = SchemaBuilder.FromMap(accessor.Value!.Map, _db.Provider, _db.Config);
        if (desired.IsFailure) return desired.Error!;
        var actual = await introspector.ReadTableAsync(_db, desired.Value!.Name, desired.Value.Schema, ct).ConfigureAwait(false);
        if (actual.IsFailure) return actual.Error!;
        return SchemaDiff.Compare(desired.Value, actual.Value, level);
    }

    private async ValueTask<Result<TableSyncResult>> SyncTableAsync(EntityMap map, SchemaSyncLevel level, bool force,
        Dictionary<string, string> state, SchemaIntrospector introspector, CancellationToken ct)
    {
        var desiredResult = SchemaBuilder.FromMap(map, _db.Provider, _db.Config);
        if (desiredResult.IsFailure) return desiredResult.Error!;
        var desired = desiredResult.Value!;
        var key = desired.Schema is null ? desired.Name : desired.Schema + "." + desired.Name;
        var fingerprint = SchemaBuilder.Fingerprint(desired);
        if (!force && state.TryGetValue(key, out var known) && known == fingerprint)
            return new TableSyncResult(key, TableSyncOutcome.Unchanged, [], []);

        var actual = await introspector.ReadTableAsync(_db, desired.Name, desired.Schema, ct).ConfigureAwait(false);
        if (actual.IsFailure) return actual.Error!;
        var plan = SchemaDiff.Compare(desired, actual.Value, level);

        if (plan.Apply.Count > 0)
        {
            var applied = await ApplyAsync(key, plan.Apply, ct).ConfigureAwait(false);
            if (applied.IsFailure) return applied.Error!;
        }

        var appliedText = plan.Apply.Select(c => c.Describe()).ToArray();
        var pendingText = plan.Pending.Select(c => c.Describe()).ToArray();
        var now = _db.Options.Time.GetUtcNow();
        if (plan.Apply.Count > 0)
            _db.Publish(new SchemaSyncedEvent(_db.Name, _db.Provider.Name, now, key, actual.Value is null, plan.Apply.Count));

        if (plan.Pending.Count > 0)
        {
            // Forget any earlier fingerprint: the table does not match its model, so the next sync must look again.
            if (state.Remove(key))
            {
                var forgotten = await _db.ExecuteSqlAsync($"DELETE FROM {Table()} WHERE {SqlKit.Identifier("table_name")} = {key}", ct).ConfigureAwait(false);
                if (forgotten.IsFailure) return forgotten.Error!;
            }
            _db.Publish(new SchemaDriftDetectedEvent(_db.Name, _db.Provider.Name, now, key, pendingText));
            return new TableSyncResult(key, TableSyncOutcome.Drift, appliedText, pendingText);
        }

        var saved = await SaveStateAsync(key, fingerprint, ct).ConfigureAwait(false);
        if (saved.IsFailure) return saved.Error!;
        state[key] = fingerprint;
        var outcome = actual.Value is null ? TableSyncOutcome.Created : plan.Apply.Count > 0 ? TableSyncOutcome.Altered : TableSyncOutcome.UpToDate;
        return new TableSyncResult(key, outcome, appliedText, []);
    }

    /// <summary>Applies one table's changes on a single session, in a transaction where DDL is transactional.</summary>
    private async ValueTask<Result> ApplyAsync(string table, IReadOnlyList<SchemaChange> changes, CancellationToken ct)
    {
        var ddl = _db.Provider.Ddl;
        var opened = await DataSession.OpenAsync(_db, ct).ConfigureAwait(false);
        if (opened.IsFailure) return opened.Error!;
        await using var session = opened.Value!;

        foreach (var statement in ddl.SessionSetup)
        {
            var setup = await session.ExecuteAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
            if (setup.IsFailure) return DataErrors.SchemaSyncFailed(table, statement, setup.Error);
        }

        try
        {
            if (ddl.TransactionalDdl)
            {
                var begun = await session.BeginAsync(ct).ConfigureAwait(false);
                if (begun.IsFailure) return begun.Error!;
            }

            foreach (var change in changes)
            foreach (var statement in ddl.Generate(change))
            {
                var executed = await session.ExecuteAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
                if (executed.IsFailure)
                {
                    await session.RollbackAsync().ConfigureAwait(false);
                    return DataErrors.SchemaSyncFailed(table, $"{change.Describe()}: {statement}", executed.Error);
                }
            }

            if (ddl.IntegrityCheckSql is { } check)
            {
                var violations = await session.ReadAsync(SqlText.Raw(check), static _ => 0, ct).ConfigureAwait(false);
                if (violations.IsFailure || violations.Value!.Count > 0)
                {
                    await session.RollbackAsync().ConfigureAwait(false);
                    return DataErrors.SchemaSyncFailed(table, "Rows violate a foreign key after the change; nothing was applied.", violations.IsFailure ? violations.Error : null);
                }
            }

            var committed = await session.CommitAsync(ct).ConfigureAwait(false);
            return committed.IsFailure ? DataErrors.SchemaSyncFailed(table, "commit", committed.Error) : Result.Success();
        }
        finally
        {
            foreach (var statement in ddl.SessionTeardown)
                await session.ExecuteAsync(SqlText.Raw(statement), CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ── State table ─────────────────────────────────────────────────────────

    private async ValueTask<Result<Dictionary<string, string>>> LoadStateAsync(SchemaIntrospector introspector, CancellationToken ct)
    {
        var definition = StateDefinition();
        var existing = await introspector.ReadTableAsync(_db, definition.Name, definition.Schema, ct).ConfigureAwait(false);
        if (existing.IsFailure) return existing.Error!;
        if (existing.Value is null)
        {
            foreach (var statement in _db.Provider.Ddl.Generate(new CreateTableChange(definition)))
            {
                var created = await _db.ExecuteSqlAsync(SqlText.Raw(statement), ct).ConfigureAwait(false);
                if (created.IsFailure) return DataErrors.SchemaSyncFailed(StateTable, statement, created.Error);
            }
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var rows = await _db.SqlReadAsync($"SELECT {SqlKit.Identifier("table_name")}, {SqlKit.Identifier("fingerprint")} FROM {Table()}",
            static r => (Table: r.GetString(0), Fingerprint: r.GetString(1)), ct).ConfigureAwait(false);
        if (rows.IsFailure) return rows.Error!;
        return rows.Value!.ToDictionary(r => r.Table, r => r.Fingerprint, StringComparer.Ordinal);
    }

    private async ValueTask<Result> SaveStateAsync(string table, string fingerprint, CancellationToken ct)
    {
        var deleted = await _db.ExecuteSqlAsync($"DELETE FROM {Table()} WHERE {SqlKit.Identifier("table_name")} = {table}", ct).ConfigureAwait(false);
        if (deleted.IsFailure) return deleted.Error!;
        var inserted = await _db.ExecuteSqlAsync(
            $"INSERT INTO {Table()} ({SqlKit.Identifier("table_name")}, {SqlKit.Identifier("fingerprint")}, {SqlKit.Identifier("synced_at")}) VALUES ({table}, {fingerprint}, {_db.Options.Time.GetUtcNow()})",
            ct).ConfigureAwait(false);
        return inserted.IsFailure ? inserted.Error! : Result.Success();
    }

    private SqlIdentifier Table() => SqlKit.Identifier(StateTable);

    private TableSchema StateDefinition()
    {
        var provider = _db.Provider;
        ColumnSchema Column(string name, Type type, DataType dataType, int size) => new(name,
            provider.NormalizeStoreType(provider.Types.GetStoreType(new ColumnMap { PropertyName = name, ClrType = type, DataType = dataType, Size = size }, _db.Config)),
            false);
        return new TableSchema(StateTable, provider.Capabilities.Schemas ? provider.GetDefaultSchema(_db.Config) : null,
        [
            Column("table_name", typeof(string), DataType.String, 255),
            Column("fingerprint", typeof(string), DataType.String, 64),
            Column("synced_at", typeof(DateTimeOffset), DataType.DateTimeOffset, 0),
        ], ["table_name"]);
    }

    /// <summary>Referenced tables first (foreign keys within the set); otherwise the given order.</summary>
    private static List<EntityMap> Order(IReadOnlyList<EntityMap> maps)
    {
        var byType = maps.ToDictionary(m => m.ClrType);
        List<EntityMap> ordered = [];
        HashSet<Type> visiting = [];
        HashSet<Type> done = [];
        void Visit(EntityMap map)
        {
            if (done.Contains(map.ClrType) || !visiting.Add(map.ClrType)) return;   // cycles: keep the given order
            foreach (var fk in map.ForeignKeys)
                if (fk.ReferencedEntity is { } target && target != map.ClrType && byType.TryGetValue(target, out var referenced))
                    Visit(referenced);
            visiting.Remove(map.ClrType);
            done.Add(map.ClrType);
            ordered.Add(map);
        }
        foreach (var map in maps) Visit(map);
        return ordered;
    }
}
