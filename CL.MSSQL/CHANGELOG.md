# Changelog

## Unreleased

### Security

- **Captured values in column expressions are now parameters.** A typed `UpdateAsync` setter
  that reads the row (`t => new T { A = t.A + x }`), grouped `Select` projections and `GroupBy`
  keys used to write captured values into the SQL text, doubling only `'`. Every captured value
  except `NULL` and primitive numbers is now bound as a parameter. This also keeps the fractional
  seconds of `DateTime` values (previously cut to whole seconds), binds enums by value (previously
  written as their member name) and keeps non-Latin characters in strings (the old literal had no
  `N` prefix, so it was a `varchar` in the database's code page).

### Added

- **`Math.Min` / `Math.Max`** in setter, projection and grouping expressions, written as `CASE`
  (`LEAST` / `GREATEST` need SQL Server 2022).
- **Row locking**: `QueryBuilder.ForUpdate(LockWait)` / `ForShare(LockWait)` with
  `LockWait.Wait | NoWait | SkipLocked`, for queries created from a `TransactionScope`. Rendered as
  table hints (`UPDLOCK` / `HOLDLOCK`, `ROWLOCK`, `NOWAIT`, `READPAST`).
- **`QueryBuilder.UpdateAndGetAsync(setter)`** updates matching rows and returns them as they are
  afterwards, in one `UPDATE … OUTPUT INSERTED.*` statement (honours `Where`, `OrderBy`, `Take` and
  the `ForUpdate` wait mode; no primary key needed).
- **`QueryBuilder.AnyAsync()` / `AnyAsync(predicate)`** — `SELECT TOP (1) 1 …`.
- **`Repository.UpsertAsync(entity, (current, incoming) => new T { … })`** — insert, or update the
  existing row from its current values, through the same serializable upsert batch as
  `UpsertAsync(entity)`.
- **Database and table administration** on `MSSQLLibrary.Admin` (`DatabaseAdmin`) and as library
  methods: `CreateDatabaseAsync`, `DropDatabaseAsync`, `DatabaseExistsAsync`, `CreateTableAsync<T>`,
  `DropTableAsync<T>`, `TruncateTableAsync<T>`, `TableExistsAsync<T>` / `TableExistsAsync(name)`.
- `SqlServerDatabaseConfig.CreateDatabaseIfMissing` (default `false`) creates the database during
  initialization.
- `ConnectionManager.ExecuteOnServerAsync` runs work on a server-level connection (`master`,
  unpooled).

## 2026-10-24

### Added

- **Live config changes per database** (parity with CL.MySQL2; requires CodeLogic 4.9+).
  `MSSQLLibrary` implements `IConfigReloadable`: a change to `config.mssql.json` made through
  `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync` or the config file watcher is applied
  without a restart and touches only the databases that changed. A changed connection string clears
  that database's idle pool, re-tests it and ensures its schema-state tables; other settings update
  in place; added/removed databases connect/disconnect; untouched databases keep their connections.
  `mssql.cache` changes apply immediately. An unreachable database leaves the change saved, still
  applies the rest, and is reported as not applied. A runtime `SetSyncMode` survives unrelated changes.
- `ConnectionManager.ReplaceConfigurationAsync` and `ConnectionManager.UnregisterConfigurationAsync`
  (same surface as CL.MySQL2).

- **Index drift warning** (parity with CL.MySQL2 and CL.PostgreSQL): when an index declared on
  the model (`[CompositeIndex]`, `[Index]` or `Column.Index`) exists under the same name but with a
  different definition (key columns or their order, `UNIQUE`, `INCLUDE`), schema sync logs a
  Warning naming the table and both definitions. An index on a column renamed with
  `PreviousName` in the same sync is not reported as drift or rebuilt.
- **Nested casts in aggregate selectors**: `SumAsync(r => (long?)(long)r.Amount)` and similar
  unwrap to the column, like CL.MySQL2 and CL.PostgreSQL.

### Changed — behaviour

- **`Production` (Safe) no longer rebuilds a drifted index.** It used to emit `DROP INDEX` +
  `CREATE INDEX` at every level, which contradicted "Production never drops". At
  `SchemaSyncLevel.Safe` the index is now left as it is and the table is flagged `DriftPending`;
  `Developer`, `Migration` (and explicit `Additive` / `Full` levels) still drop and recreate it.
  Indexes dropped so a column can be altered are unchanged.

## 2026-10-23

### Added

- **`Repository<T>.InsertIgnoreAsync` and `InsertManyIgnoreAsync`** (API parity with CL.MySQL2).
  A duplicate key (SQL Server error 2627 / 2601) is a normal outcome: `InsertIgnoreAsync` returns
  `InsertOutcome.Inserted` (auto PK populated from `OUTPUT INSERTED`) or `InsertOutcome.Duplicate`;
  `InsertManyIgnoreAsync` returns the number of rows actually inserted. No exception, no Error log.
  - SQL Server has no `INSERT IGNORE`: each insert runs under a savepoint and a duplicate rolls back
    to it. `InsertManyIgnoreAsync` sends each batch as one multi-row `INSERT` and only falls back to
    row-by-row inserts for a batch that hit a duplicate.
  - **Atomic on failure**: the call runs in its own transaction, or a savepoint inside an explicit
    `TransactionScope`; any non-duplicate error rolls back everything the call wrote, so a failure
    `Result` means nothing was written. (With `SET XACT_ABORT ON` on the session a duplicate dooms
    the whole transaction, so the call fails instead of skipping the row.)
- **Opt-in `DistinguishDuplicateKeyError`** (`SqlServerDatabaseConfig` or `Repository<T>` property):
  a duplicate key in `InsertAsync` / `InsertManyAsync` returns error code `mssql.duplicate_key` and
  logs at Warning. Default `false` keeps `mssql.insert_failed` / `mssql.bulk_insert_failed` at Error.
- **Nullable aggregate results**: `MaxAsync` / `MinAsync` / `SumAsync` accept nullable selectors
  such as `r => (DateTime?)r.CreatedUtc` and return `null` for SQL NULL (for example no matching
  rows). Non-nullable selectors still return `default(TResult)`.

### Known behaviour (unchanged)

- String parameters are sized from `[Column(Size)]`, so SqlClient truncates over-long values
  client-side and SQL Server never reports truncation. This affects every insert and update path.

## 2026-09-13

### Changed — behaviour

- **`Repository<T>.CountAsync` now excludes soft-deleted rows.** For an entity carrying
  `[SoftDelete]` it emitted a bare `SELECT COUNT(*)` while `GetAllAsync` and the count inside
  `GetPagedAsync` both filtered on the same soft-delete column, so the two contradicted each
  other. They now agree. **If you relied on `CountAsync` returning the physical row count of a
  soft-delete entity, switch to `Query<T>().IncludeDeleted().CountAsync()`.** No other read
  path changes; entities without `[SoftDelete]` are unaffected.
- **A configured `MaxBatchInsertSize` now actually applies.** `GetRepository<T>()` never
  passed it, so every repository chunked at the constructor default of 500. A database that
  configured a different value now gets it — in both the `connectionId` and the
  `TransactionScope` overload. Statements are still capped further so a batch stays under
  SQL Server's 2,100-parameter limit.
- **A configured `QueryTimeoutMs` now applies** as the command timeout on the query commands
  the library issues (repositories, query builder, projections, joins, grouped queries),
  overriding the connection string's `Command Timeout` for those commands. The default of
  30 000 ms matches the provider's own 30 s default, so a default configuration is unchanged;
  a database that set `CommandTimeout` above 30 s but left `QueryTimeoutMs` alone will see
  library queries bounded at 30 s. Set `QueryTimeoutMs` to `0` to leave the connection-string
  value in place.
- **A configured `DefaultStringSize` now applies** to schema generation for string properties
  with no explicit `[Column(Size = ...)]`. The default is still 255, so generated DDL and the
  schema CRC are unchanged unless you change the setting.
- **A configured `CacheEnabledOverride` now applies**, winning over the global
  `mssql.cache.Enabled` switch for that connection id. `null` (the default) keeps today's
  behaviour.

### Documentation

- Corrected the `SqlFn` XML documentation, which had been copied from the MySQL library and
  named functions T-SQL does not have (`HOUR`, `MINUTE`, `DATE`, `DAYOFWEEK`, `IFNULL`,
  `FROM_UNIXTIME`). It now describes the `DATEPART`/`CONVERT`/`COALESCE` SQL actually emitted.
- Documented `SqlFn` and the transaction-scoped `GetRepository<T>(tx)` / `Query<T>(tx)`
  accessors in the queries guide, and added the missing soft-delete and retention sections to
  the schema guide — the index page had linked to them all along.
- Corrected more comments carried over from the MySQL port: `[Column(PreviousName = ...)]`
  renames via `EXEC sys.sp_rename`, not `CHANGE COLUMN`; the LIKE escaper escapes `%`, `_`
  and `[`, not a backslash; column references are bracket-quoted, not backtick-quoted; and
  `UpsertAsync` does not use any `AS new` alias syntax — it stages the row in a table
  variable and matches it with `UPDLOCK`/`HOLDLOCK` under a `SERIALIZABLE` transaction.
- **Raw SQL cannot join a `TransactionScope`.** The queries guide showed `ExecuteSqlAsync`
  calls inside an open scope being committed by `tx.CommitAsync()`; they in fact open their
  own connection and run outside the transaction. The example is replaced with an explicit
  warning and the supported alternatives.
- The configuration tables in the library index and the performance guide now describe what
  each setting does, after the wiring below: `QueryTimeoutMs`, `MaxBatchInsertSize`,
  `MaxInClauseValues` (advisory — it warns, it does not chunk), `N1DetectorThreshold`,
  `DefaultStringSize`, `CacheEnabledOverride`, `BackupDirectory`, `DefaultTtlSeconds` and
  `PublishEvents`. `PreparedStatementCacheSize` and `MaxMemoryMb` are documented as obsolete
  and point at the real control.
- The N+1 detection section documents the implemented detector: the one-second rolling
  window, publish-once-per-window, the normalized template, and that `0` disables it.
- Documented slow-query plan capture as it actually behaves. Unlike the sibling libraries,
  `CaptureExplainOnSlowQuery` was already read and already gating the cached-ShowPlan-XML
  lookup in `QueryObservability`; every `RecordSlow` call site reaches it. The guide now
  states that the capture runs on its own connection (never inside the callers
  transaction) and is strictly best-effort — valid ShowPlan XML or `null`, never a throw —
  and that the default remains on.
- Fixed the `MinPoolSize` default in the configuration table: it is `0`, not `1`.
- Fixed the imperative-migration example, which would not compile — the `Migration` base
  supplies `Version` and `Description` from its `(appVersion, order, description)`
  constructor and neither is virtual.
- Corrected the retention description: each pass loops until a batch deletes fewer rows than
  `BatchSize`, not until it deletes zero. The worker's entity list is now live, so the
  "entities synced after start are never purged" caveat is gone with it.
- Clarified that table-version invalidation is skipped for tables with live `SmartCachePool`
  entries, that `QueryCache.Enabled` / `TimeQuantizeSeconds` are internal rather than part of
  the public facade, and that `MaxMemoryMb` is obsolete (eviction is by entry count).
- Noted that composition-time guard errors (unsupported expressions, `.Join` after
  `.OrderBy`, `WhereExists` on the outer table) throw rather than returning a `Result`.

### Added

- `GetRepository<T>(TransactionScope)` and `Query<T>(TransactionScope)`.
  `BeginTransactionAsync` returned a scope that neither accessor took, so callers had to
  construct `Repository<T>` by hand to do any work inside a transaction.
- `RetentionWorker.RunOnceAsync()` is now public; it already existed but was internal, so
  the three libraries now expose the same retention surface.
- `MSSQLLibrary.RunRetentionOnceAsync(ct)` — an operator-triggered purge over every registered
  `[RetainDays]` entity, without constructing a worker by hand.
- `RetentionWorker.TryRegister(Type)` and `RetentionWorker.Entities` — the worker's entry list
  is now live and can be added to while the loop runs.
- `QueryBuilder<T>.WithCache()` and `ProjectedQuery<,>.WithCache()` — parameterless overloads
  that use the configured `mssql.cache` → `DefaultTtlSeconds` (60 s by default).
- `QueryCache.SetConnectionOverride(connectionId, enabled)` — registers a per-database
  override of the global cache switch; the library calls it from `CacheEnabledOverride`.
- `QueryObservability.ConfigureN1Detection(connectionId, threshold)` — registers a
  connection's N+1 threshold; the library calls it from `N1DetectorThreshold`.
- `QueryCache.Configure` takes `defaultTtlSeconds` and `publishEvents` (both optional, both
  defaulting to today's values).
- Configuration validation for the newly wired fields: `MaxInClauseValues`, `QueryTimeoutMs`,
  `N1DetectorThreshold` and `DefaultStringSize`.

### Deprecated

- `SqlServerDatabaseConfig.PreparedStatementCacheSize` is `[Obsolete]` and not applied.
  `Microsoft.Data.SqlClient` has no client-side prepared-statement cache to size and SQL
  Server's plan cache is automatic; configure pooling on the connection string instead. The
  property is kept so existing configuration keeps compiling and deserializing.
- `CacheConfiguration.MaxMemoryMb` is `[Obsolete]` and not applied. The in-process store
  bounds the cache by entry count — use `MaxEntries`.

### Fixed

- **Retention purged every entity against the `Default` connection.** The registered-entity
  set recorded types with no connection association, so an entity synced against a named
  connection was purged from the wrong database — in practice the `DELETE` hit a database
  where the table did not exist, the failure was caught and logged, and the retention the
  `[RetainDays]` attribute described silently never happened. Registrations now carry the
  connection they were made against, and the same entity synced to two connections is two
  registrations. Only reachable since the worker began running at all in this same release.
  `RetentionWorker.TryRegister(Type, string)` and a `Registrations` view are added; the existing
  type-only overload keeps its meaning and registers against the worker's own connection.
- **`ids.Contains(x.Id)` on a `List<T>` or `HashSet<T>` threw instead of emitting `IN`.**
  The expression visitor's first `Contains` case matched any single-argument instance call,
  so a collection membership test took the string `LIKE` branch and tried to emit the
  collection itself as a column. Arrays were unaffected because they bind to the static
  two-argument `Enumerable.Contains`, which had its own case. The `LIKE` branch is now
  restricted to a string receiver, and both membership shapes share one emitter.
- **Retention never ran.** `OnStartAsync` built the `RetentionWorker` from the set of
  registered entities and the constructor snapshotted it, but every documented flow registers
  entities through `SyncTableAsync` / `SyncSchemaAsync` *after* `CodeLogic.StartAsync()`. So
  `HasWork` was false, the background loop never started, and `[RetainDays]` was dead in
  normal usage. The worker's entry list is now live, the library feeds it every registration,
  and it starts the loop the first time a `[RetainDays]` entity appears. The 5-minute initial
  delay, the 24-hour interval and `RunOnceAsync()` are unchanged, and disposal still cancels
  the loop cleanly.
- **The N+1 detector never fired.** `QueryObservability.RecordN1` had no call sites and
  `N1DetectorThreshold` was never read, so `N1QueryDetectedEvent` could not be published.
  Executions of the same normalized statement are now counted per connection in a one-second
  rolling window and the event is published once per window when the threshold is crossed.
  Bookkeeping is bounded by a fixed-capacity map pruned by age. A threshold of `0` — the
  default — costs nothing: no dictionary touch and no allocation on the query path.
- **`ProjectedQuery` and `JoinedQuery` dropped the caller's `CancellationToken`** when handing
  their work to the connection manager, so an already-cancelled call still opened a connection
  (and could still be retried by the transient-failure logic). Both now forward it, as
  `QueryBuilder` and `Repository` already did.
- **`.WhereExists(...).Select(...).WithCache(...)` cached a cross-table result under one
  table's version** and would serve it stale after the other table changed. `QueryBuilder`
  refuses to cache a subquery-filtered query, but `Select` and `GroupBy` forwarded the TTL and
  smart-cache pool into the projection regardless, and `ProjectedQuery.WithCache` could set
  one afterwards. The refusal now propagates to the projected and grouped queries, which log a
  warning and execute uncached.
- **`BackupDirectory` is honoured.** Schema backups were always written to
  `<DataDirectory>/backups`; a configured directory (absolute, or relative to the data
  directory) is now used for both writing and reading back the latest file.
- **`CacheConfiguration.PublishEvents` is honoured** — `CacheHitEvent` / `CacheMissEvent` are
  only published when it is true (it is by default).
- **`MaxInClauseValues` is reported.** A generated `IN (...)` list longer than the configured
  cap logs one warning per query build, naming the entity and the value count. Nothing is
  chunked, truncated or rejected: callers that exceed it today keep working.
- **`SqlFn.Like` in a projection, `GROUP BY` key or `UPDATE ... SET` was a syntax error.**
  T-SQL has no boolean expression type, so the emitted `a LIKE b` is a predicate and is
  rejected anywhere a value is expected (`Incorrect syntax near the keyword 'LIKE'`). It
  now materializes as `CAST(CASE WHEN a LIKE b THEN 1 ELSE 0 END AS bit)`. `Where(...)`
  was never affected — it takes a different translation path.
- **A migration registered twice ran twice.** `Register` and `RegisterFrom` both appended
  unconditionally, so pairing `RegisterMigrationsFrom(assembly)` with an explicit
  `RegisterMigration(...)` held two copies, and both passed the apply filter. Registration
  now deduplicates by migration id.
- **`HealthChangedEvent` was declared but never raised.** It is now published on a health
  state transition.
- **The schema-state sentinel was keyed on the bare table name**, so two entities with the
  same table name in different schemas shared one row and masked each other's CRC. The key
  is now `schema.table`; `SchemaStateStore` resolves an unqualified name against `dbo`, so
  the public diagnostic API still accepts a bare table name.
- Restoring a table from backup cleared the sentinel by the bare name, which no longer
  matched the qualified key and left a stale CRC behind.
- `Contains()` over an empty collection emitted `IN ()`, which is a syntax error. It now
  emits `1 = 0`.
- Cancellation tokens are forwarded to connection acquisition, so opening a connection can
  be cancelled.
- `ConnectionManager` held its configuration map in a non-concurrent `Dictionary` that could
  be written by `RegisterConfiguration` while another thread read it.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## Initial release

- Initial `CodeLogic.MSSQL` release for SQL Server 2019+, SQL Server 2022/2025, and Azure SQL Database.
- Added repository, fluent-query, projection, grouping, paging, transaction, raw SQL, cache, health, migration, retention, backup, and named-connection workflows matching `CL.MySQL2`.
- Added SQL Server-native mappings, schemas, identity output, parameter-capped batches, lock-based non-`MERGE` upserts, `sys.*` schema management, application locks, transient retry, and estimated-plan capture.
