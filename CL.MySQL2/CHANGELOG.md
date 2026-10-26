# CL.MySQL2 — Changelog

All notable changes to **CodeLogic.MySQL2** are documented here. Versions follow
[Semantic Versioning](https://semver.org/). The version listed here matches the
NuGet package version of `CodeLogic.MySQL2`.

## 2026-10-27

### Fixed

- **Empty-string column defaults.** `[Column(DefaultValue = "")]` was silently dropped, so the
  column got no default; on CHAR/VARCHAR columns it now creates `DEFAULT ''` (other types, where
  MySQL allows no literal empty default, still ignore it). `DefaultValue = "''"` keeps working.
- **Schema sync tells "no default" from `''`.** On a NOT NULL column both compared equal, so a
  column created without a default was never given the model's `''` (and a stray `''` was never
  removed). Inserts that leave such a column out then failed, for example from an older app
  version during a rollback. Sync now adds or drops the default. Tables whose models declare an
  empty default get a new schema CRC, so they are compared once on the next sync.

## 2026-10-26

### Security

- **Captured values in column expressions are now parameters.** A typed `UpdateAsync` setter
  that reads the row (`t => new T { A = t.A + x }`), grouped `Select` projections and `GroupBy`
  keys used to write captured values into the SQL text, escaping only `'`. A string ending in a
  backslash could break out of the literal. Every captured value except `NULL` and primitive
  numbers is now bound as a parameter. This also keeps the fractional seconds of `DateTime`
  values (previously cut to whole seconds) and binds enums by value (previously written as their
  member name).

### Added

- **`Math.Min` / `Math.Max`** in setter, projection and grouping expressions (`LEAST` / `GREATEST`).
- **Row locking**: `QueryBuilder.ForUpdate(LockWait)` / `ForShare(LockWait)` with
  `LockWait.Wait | NoWait | SkipLocked`, for queries created from a `TransactionScope`.
- **`QueryBuilder.UpdateAndGetAsync(setter)`** updates matching rows and returns them as they are
  afterwards, atomically (honours `Where`, `OrderBy`, `Take` and the `ForUpdate` wait mode).
- **`QueryBuilder.AnyAsync()` / `AnyAsync(predicate)`** — `SELECT 1 … LIMIT 1`.
- **`Repository.UpsertAsync(entity, (current, incoming) => new T { … })`** — insert, or update the
  existing row from its current values, in one `INSERT … ON DUPLICATE KEY UPDATE`.
- **Database and table administration** on `MySQL2Library.Admin` (`DatabaseAdmin`) and as library
  methods: `CreateDatabaseAsync`, `DropDatabaseAsync`, `DatabaseExistsAsync`, `CreateTableAsync<T>`,
  `DropTableAsync<T>`, `TruncateTableAsync<T>`, `TableExistsAsync<T>` / `TableExistsAsync(name)`.
- `MySqlDatabaseConfig.CreateDatabaseIfMissing` (default `false`) creates the database during
  initialization.
- `ConnectionManager.ExecuteOnServerAsync` runs work on a server-level connection (no default
  database).

## 2026-10-24

### Added

- **Live config changes per database** (requires CodeLogic 4.9+). `MySQL2Library` implements
  `IConfigReloadable`: a change to `config.mysql.json` made through `Libraries.UpdateConfigAsync`,
  `Libraries.ReloadConfigAsync` or the config file watcher is applied without a restart and touches
  only the databases that changed. Changed connection settings clear that database's idle pool,
  re-test it and ensure its schema-state tables; other settings update in place; added/removed
  databases connect/disconnect; untouched databases keep their connections. `mysql.cache` changes
  apply immediately. An unreachable database leaves the change saved, still applies the rest, and
  is reported as not applied. A runtime `SetSyncMode` survives unrelated changes.
- `ConnectionManager.ReplaceConfigurationAsync` and `ConnectionManager.UnregisterConfigurationAsync`
  swap or remove one connection's configuration, clearing its idle pool when the connection string
  changes.

### Changed — behaviour

- **Index drift is checked for every model index and rebuilt where drops are allowed** (aligned
  with CL.MSSQL and CL.PostgreSQL). The drift check now covers `[Index]`, `Column.Index` and
  `Column.Unique` as well as `[CompositeIndex]`, and compares uniqueness as well as the columns
  and their order. The Warning is logged at every level. `Production` (Safe) still never touches
  the index, but the table is now flagged `DriftPending`; `Developer` and `Migration` rebuild it
  with one atomic `ALTER TABLE … DROP INDEX x, ADD … x (…)`. Previously a drifted index was never
  rebuilt at any level.
- An index on a column renamed with `PreviousName` in the same sync is no longer reported as drift.

## 2026-10-23

### Added

- **`Repository<T>.InsertIgnoreAsync` and `InsertManyIgnoreAsync`**:
  - `Task<Result<InsertOutcome>> InsertIgnoreAsync(T entity, CancellationToken ct = default)` inserts a single entity while treating duplicate-key conflicts (UNIQUE index or PRIMARY KEY) as a normal outcome. Returns `InsertOutcome.Inserted` (with auto-generated PK populated on the entity) or `InsertOutcome.Duplicate`, without raising an exception and without logging at Error level (Debug at most).
  - `Task<Result<int>> InsertManyIgnoreAsync(IEnumerable<T> entities, CancellationToken ct = default)` bulk-inserts entities in batches, returning the count of rows actually inserted (duplicates contribute 0).
  - Built with `INSERT IGNORE` and connection-level warning validation: under MySqlConnector's default found-rows semantics (`UseAffectedRows=false`), `INSERT IGNORE` accurately distinguishes inserts (1) from duplicates (0), while connection warnings are intercepted so that non-duplicate errors (such as data truncation or NOT NULL constraint violations) are not silently swallowed.
  - **Atomic on failure.** `INSERT IGNORE` still writes a row when it downgrades such an error to a warning, so both methods run in a transaction and roll back everything the call wrote when any warning other than 1062 appears or anything throws: a failure `Result` always means nothing was written. Inside an explicit `TransactionScope` the call rolls back to a savepoint instead, leaving the caller's other work in that transaction intact.
- **Opt-in `mysql.duplicate_key` error code for `InsertAsync` and `InsertManyAsync`**:
  - Configurable via `MySqlDatabaseConfig.DistinguishDuplicateKeyError` in connection configuration, `Repository<T>.DistinguishDuplicateKeyError` property, or constructor overload.
  - When enabled, MySQL error 1062 (`ER_DUP_ENTRY`) returns the distinct error code `mysql.duplicate_key` and logs at Warning instead of Error.
  - Defaults to `false`: keeping unchanged existing behavior (`mysql.insert_failed` / `mysql.bulk_insert_failed` with Error logging).
- **CompositeIndex column drift detection**:
  - `SchemaAnalyzer` loads ordered column lists (`SEQ_IN_INDEX`) for existing indexes from `information_schema.STATISTICS`.
  - When an index declared via `[CompositeIndex]` exists in the database but its columns or column ordering differ from the model, a Warning is logged naming the table, index name, existing columns, and model columns.
  - Safe level (`SchemaSyncLevel.Safe`) never drops or rebuilds drifted indexes, maintaining backwards compatibility.
- **Nullable result support for QueryBuilder aggregates (`MaxAsync`, `MinAsync`, `SumAsync`)**:
  - Aggregates now support nullable types (e.g. `MaxAsync(p => (DateTime?)p.CreatedAt)` or `SumAsync(p => (int?)p.Amount)`).
  - Unwraps `Nullable.GetUnderlyingType(typeof(TResult))` prior to type conversion so that SQL NULL on an empty set or null column returns `null` instead of throwing `InvalidCastException`.
  - `MySqlExpressionVisitor.TranslateSelector` unwraps unary `Convert` expressions to support cast-to-nullable lambda selectors.
  - Non-nullable aggregate behavior remains completely unchanged (empty set returns `default(TResult)` such as `DateTime.MinValue` or `0`).

## 2026-09-13

### Behaviour changes (read before upgrading)

- **`Repository<T>.CountAsync()` now applies the `[SoftDelete]` filter.** It used to emit a
  bare `SELECT COUNT(*)`, contradicting `GetAllAsync` / `GetPagedAsync` on the same entity --
  the paged read already filtered its own count. For a soft-delete entity the returned count
  will now be *lower* than before by the number of deleted rows. If you relied on the old
  total, use `mysql.Query<T>().IncludeDeleted().CountAsync()`.
- **`CaptureExplainOnSlowQuery` now defaults to `false`.** The flag was declared `true` but
  never read, so nothing ran. Now that it is wired, keeping the old default would have turned
  `EXPLAIN` capture on for everyone on upgrade. A `config.mysql.json` written by an earlier
  version still carries `true` and will therefore capture plans -- set it to `false` if you
  do not want that.
- **Configuration fields that were declared and ignored are now read.** `QueryTimeoutMs`,
  `MaxBatchInsertSize`, `BackupDirectory`, `CacheEnabledOverride`, `SslCertificatePath`,
  `DefaultStringSize`, `N1DetectorThreshold`, `DefaultTtlSeconds` and `PublishEvents` all take
  effect now. Every one of them keeps today's behaviour at its default value, but a
  non-default value you set previously (and which did nothing) will now change behaviour.

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
- **`GetRepository<T>` ignored `MaxBatchInsertSize`.** Both overloads (connection id and
  `TransactionScope`) passed the slow-query threshold but not the batch size, so
  `InsertManyAsync` / `UpsertManyAsync` always chunked at the constructor default of 500.
- **`ProjectedQuery` and `JoinedQuery` dropped the terminal's `CancellationToken`** when
  opening the connection -- a cancelled token could still run the query to completion, and
  any transient-failure retry around the open ignored cancellation entirely. Both now
  forward it.
- **A subquery-filtered query could be cached through `.Select(...)`.** `QueryBuilder.Select`
  copied the cache TTL / smart-cache pool into the projection without consulting the
  subquery-filter guard that `ShouldCache` applies, so
  `WhereExists(...).Select(...).WithCache(...)` cached a cross-table result stamped with only
  one table's version and served it stale after the other table changed. The guard now
  travels with the projection, so a `.WithCache` applied after `.Select` is refused too.
- **`TypeConverter.ResolveColumn` ignored the configured default string size**, calling
  `InferColumn(clrType)` without threading it through. Same root cause as `DefaultStringSize`
  below; both are fixed together.

### Added

- **Retention actually runs.** `RetentionWorker` snapshotted its entry list at construction,
  and the worker was constructed during library start -- before any documented flow calls
  `SyncTableAsync` / `SyncSchemaAsync`. `HasWork` was therefore false and `[RetainDays]` was
  dead in normal use. The entry list is now live: an entity registered at any time is picked
  up (`RetentionWorker.TryRegister`), and the library starts the loop the first time a
  `[RetainDays]` entity is registered. `Start()` remains idempotent, the 5-minute initial
  delay and 24-hour interval are unchanged, disposal still cancels cleanly, and the list is
  safe to mutate while the loop reads it.
- `MySQL2Library.RunRetentionOnceAsync()` -- an on-demand purge pass over every registered
  `[RetainDays]` entity, without reaching for the worker directly.
- **N+1 detection.** `N1DetectorThreshold` is read and `QueryObservability.RecordN1` finally
  has a caller: executions of the same normalized SQL template on one connection are counted
  in a one-second rolling window and publish `N1QueryDetectedEvent` once per window when the
  count crosses the threshold. Bookkeeping is bounded (a fixed number of templates, pruned by
  age). `0` -- the default -- disables it with no allocation and no dictionary touch.
  `QueryObservability.ConfigureN1Detection(connectionId, threshold)` sets it at runtime.
- **Slow-query `EXPLAIN` capture.** With `CaptureExplainOnSlowQuery` on, a slow query also
  runs `EXPLAIN FORMAT=JSON` with the same parameters and attaches the plan to
  `SlowQueryEvent.ExplainJson`. It runs on a separate pooled connection (never the caller's
  transaction, never the caller's thread), skips statements MySQL cannot explain (DDL, and
  multi-statement batches such as `INSERT ...; SELECT LAST_INSERT_ID();`), and swallows every
  failure -- the event always publishes, with a null payload when no plan was obtained.
- `QueryBuilder<T>.WithCache()` and `ProjectedQuery<,>.WithCache()` -- parameterless overloads
  using the cache configuration's `DefaultTtlSeconds` (60s default).
- `QueryCache.SetConnectionOverride(connectionId, enabled)` backing the per-database
  `CacheEnabledOverride`, and an optional config lookup on `BackupManager` backing
  `BackupDirectory`. `BackupManager.GetLatestBackupFile` and `CleanupOldBackupsAsync` take an
  optional `connectionId` so they read the same directory the backup was written to.

### Changed

- `QueryTimeoutMs` is applied as `CommandTimeout` (rounded up to whole seconds) on the
  commands the repository, query builder, projections, joins, retention and the raw-SQL
  helpers create. `0` inherits the connection string's `CommandTimeout`; the 30000ms default
  equals that 30-second default, so nothing changes until you change it.
- `SslCertificatePath` is written to the connection string as MySqlConnector's `SslCa` when
  `EnableSsl` is true, raising the SSL mode to `VerifyCA`. With SSL off it is ignored. The
  label and description now say "SSL CA Certificate Path": a single path field can only work
  as a CA, since MySqlConnector's client-certificate option `SslCert` additionally requires
  `SslKey`, for which this configuration has no field.
- `MaxInClauseValues` is advisory: a generated `IN (...)` list larger than it logs one warning
  per query build naming the entity and the count. Nothing is chunked, thrown or rejected, so
  no query that works today changes its result.
- `DefaultStringSize` is applied process-wide at initialization from the `Default` database
  (or the first enabled one) -- DDL generation is static and not connection-scoped.
- `CacheConfiguration.PublishEvents` gates `CacheHitEvent` / `CacheMissEvent`; the default
  `true` is today's behaviour.

### Deprecated

- `MySqlDatabaseConfig.PreparedStatementCacheSize` is `[Obsolete]` and ignored -- statement
  caching is the provider's concern, configured on the MySqlConnector connection string
  (`IgnorePrepare=false`).
- `CacheConfiguration.MaxMemoryMb` is `[Obsolete]` and ignored -- the in-process store bounds
  the cache by entry count, not bytes. Use `MaxEntries`.

### Documentation

- The "not implemented" / "reserved" notes the previous audit pass added for exactly these
  items are gone from the README, the overview, the performance page and the schema guide,
  replaced by what the code now does.

## 2026-09-13 (documentation audit)

### Documentation

- The README's transaction example still built a `Repository<T>` by hand; it now uses the
  `GetRepository<T>(tx)` / `Query<T>(tx)` accessors added in this release.
- Documented `SqlFn` and transaction-scoped queries in the queries guide.
- **Corrected: the raw SQL helpers do not join a transaction.** The queries guide showed
  `ExecuteSqlAsync` calls inside an `await using TransactionScope` block as if they were part
  of the transaction. They are not — `SqlQueryAsync` / `ExecuteSqlAsync` / `SqlScalarAsync`
  take a `connectionId` and open their own pooled connection, so a rollback does not undo
  them. The guide now says so and points at `GetRepository<T>(tx)` / `Query<T>(tx)` and
  `IMigrationContext` instead.
- **Corrected: the attribute namespace.** The overview and schema guide told you to
  `using CL.MySQL2.Attributes;`. No such namespace exists — `[Table]`, `[Column]`,
  `[SoftDelete]` and friends live in `CL.MySQL2.Models`.
- **Corrected: configuration fields that do nothing.** `QueryTimeoutMs`,
  `MaxBatchInsertSize`, `MaxInClauseValues`, `PreparedStatementCacheSize`,
  `N1DetectorThreshold`, `CaptureExplainOnSlowQuery`, `BackupDirectory`,
  `CacheEnabledOverride`, `DefaultStringSize`, `Collation`, `SslCertificatePath`,
  `MaxMemoryMb`, `DefaultTtlSeconds` and `PublishEvents` were all documented as live knobs.
  None of them is read by any code path today. The config tables and the XML comments now
  mark each one, and state what actually governs the behaviour (for example insert chunking
  is fixed at 500 rows, and generated `IN (...)` lists are uncapped).
- **Corrected: N+1 detection and `EXPLAIN` capture are not implemented.** `N1QueryDetectedEvent`
  is never published and `SlowQueryEvent.ExplainJson` is always null; the performance page,
  the events table and the README no longer promise either.
- **Corrected: `QueryCache.Enabled` and `QueryCache.TimeQuantizeSeconds` are internal.** The
  performance page presented them as part of the public facade.
- **Corrected: soft delete and `CountAsync`.** `Repository.CountAsync()` issues a bare
  `SELECT COUNT(*)` and therefore counts soft-deleted rows, unlike every other repository
  read. The soft-delete documentation used to imply otherwise.
- **Corrected: the retention SQL and its registration window.** The `RetentionWorker` summary
  claimed a server-side `NOW() - INTERVAL N DAY` cutoff; it actually binds a client-side
  `DateTime.UtcNow.AddDays(-days)` as a parameter. The schema guide also now explains that the
  worker is built at library start from the entities already passed to `SyncTableAsync` /
  `SyncSchemaAsync`, and that the first pass runs 5 minutes after start, then daily.
- **Corrected: the upsert SQL.** `UpsertAsync` was documented as emitting
  `INSERT ... AS new ON DUPLICATE KEY UPDATE` (MySQL 8.0.20+). It deliberately emits the
  `VALUES(col)` form instead, so that it also works on MariaDB.
- **Corrected: `RegisterCachePool(maxIdleFires:)` defaults to 10, not 3** — an unread entry is
  dropped after roughly five minutes at a 30-second refresh interval, not ninety seconds.
- **Corrected: the raw-string `.Join` example.** It qualified the left side as `t0`, but the
  base table is not aliased on a raw join; `t0` / `t1` exist only inside a typed `Join<,,>`.
- **Corrected: `SqlScalarAsync<long>` returns `Result<long>`, not `Result<long?>`.** The
  README and queries examples as written did not compile.
- **Corrected: schema backups.** They are always written to `DataDirectory/backups` as
  `{table}_{yyyyMMdd_HHmmss}.sql`, and `RestoreSchemaAsync`'s `backupFile` is a file path, not
  a bare name. The restore example used a filename in a format the library never produces.
- Documented the empty-collection `Contains` translation (`1 = 0`), the grouped
  `g.Count(predicate)` / `g.Any(predicate)` overloads, and the 365-day window for `DateTime`
  cache-key quantization.

### Fixed (configuration validation)

- `MySqlDatabaseConfig.Validate` checked only host, port, database and username. An
  inverted pool range, a negative timeout or a zero batch size passed validation and then
  failed later as a driver error at connection time rather than a configuration error at
  startup. It now applies the same bounds `CL.MSSQL` and `CL.PostgreSQL` already did.

### Fixed

- **A migration registered twice ran twice.** `Register` and `RegisterFrom` both appended
  unconditionally, so pairing `RegisterMigrationsFrom(assembly)` with an explicit
  `RegisterMigration(...)` held two copies, and both passed the apply filter. Registration
  now deduplicates by migration id.
- **`HealthChangedEvent` was declared but never raised.** It is now published on a health
  state transition.

### Added

- `RetentionWorker.RunOnceAsync()` — the retention pass was only reachable from a
  background loop that wakes once a day, so there was no way to trigger a purge on demand.

- `GetRepository<T>(TransactionScope)` and `Query<T>(TransactionScope)`.
  `BeginTransactionAsync` returned a scope that neither accessor took, so callers had to
  construct `Repository<T>` by hand to do any work inside a transaction.

### Security

- Added `MySqlDialect` with `Quote`, `QuoteMultipart` and `EscapeLike`, and routed all 113
  identifier render sites through it, so a delimiter inside an identifier is escaped rather
  than closing it. Output is byte-identical for safe identifiers, so generated DDL and the
  schema CRC are unchanged.
- `EntityMetadata<T>` now rejects any mapped table or column name containing a backtick, NUL
  or newline. Combined with the render-site quoting this makes identifier injection
  structurally impossible rather than merely unlikely.
- Parameters for the dictionary overload of `QueryBuilder.UpdateAsync` are named by ordinal
  instead of by the caller's key, so a key that is a valid column name but not a valid
  parameter name can no longer corrupt the statement.

### Fixed

- **A `[Column]` attribute without an explicit `DataType` generated `TINYINT`.** Because
  `DataType` is a non-nullable enum whose default was `TinyInt`, the
  `colAttr?.DataType ?? Infer(...)` fallback could never fire when the attribute was
  present. `DataType.Unspecified` is now the enum's default and such columns infer from the
  CLR property type. An unattributed `Guid` likewise generated `CHAR(1)` instead of
  `CHAR(36)`, because the inferred size was dropped along with the inferred type.
- `Contains()` over an empty collection emitted `IN ()`, which is a syntax error. It now
  emits `1 = 0`.
- Both `UpdateAsync` overloads now reject database-generated (auto-increment) columns rather
  than producing SQL the server refuses.
- Cancellation tokens are forwarded to connection acquisition, so opening a connection can
  be cancelled.
- `ConnectionManager` held its configuration map in a non-concurrent `Dictionary` that could
  be written by `RegisterConfiguration` while another thread read it.

### Changed

- Entity values are bound with an explicit `MySqlDbType` derived from the column's declared
  or inferred type, via the new `TypeConverter.CreateParameter`, instead of `AddWithValue`.
  An inferred type that differs from the column's own forces a server-side conversion and
  can prevent the column's index from being used.

### Migration notes

- **Breaking:** `DataType` enum values shift by one to make room for `Unspecified = 0`. This
  matters only if the numeric value was persisted somewhere; serialising by name is
  unaffected.
- Entities with a `[Column]` attribute that omitted `DataType` will generate corrected DDL
  and see one `ALTER` on the next schema sync.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-08-07

### Added

- Forward-only keyset pagination on entity queries through `.After(cursor)` and
  `ToCursorPagedListAsync(pageSize)`, returning `CursorPagedResult<T>`.
- Versioned Base64URL continuation tokens, stable primary-key tie-breaking,
  compound ASC/DESC ordering, and MySQL-compatible nullable ordering.

### Fixed

- Reject cursor tokens longer than 4,096 encoded characters before Base64 decoding
  or JSON deserialization, bounding work performed on untrusted paging input.

### Documentation

- Expanded the package README into a complete capability overview covering entity
  mapping, repositories, querying and paging, schema sync, migrations, lifecycle,
  transactions, caching, resilience, observability, configuration, and API boundaries.

## 2026-06-20

### Fixed

- Query-builder parameter re-keying could corrupt SQL when a single predicate
  emitted 11 or more parameters: the rename used a substring replace, so `@p1`
  also rewrote `@p10`/`@p11`, leaving placeholders with no bound value.
  Parameters are now renamed longest-name-first in `QueryBuilder.Where` and
  `JoinedQuery`, matching the existing subquery path. Covered by a new
  integration test (12-parameter predicate).

### Documentation

- **Full README + multi-page docs rewrite to the unified house style.** The
  README is now concise — title, NuGet + license badges, one-line tagline, a
  short intro, `Install`, `Quick start`, `Features`, `Configuration` (table +
  JSON), `Documentation`, `Requirements`, and `License` — and renders correctly
  on both GitHub and NuGet (Markdown only, absolute `https://` links, no raw
  HTML or relative paths). The full API now lives in the docs site rather than
  the README.
- **Docs site pages rewritten** to match the house style across the four-page
  structure: [`index`](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/index.html)
  (overview, load, repository basics, entry points, config, health, events),
  [`queries`](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/queries.html),
  [`schema-migrations`](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/schema-migrations.html),
  and [`performance`](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/performance.html).
  Each sub-page now opens with a tagline and an overview breadcrumb and closes
  with a consistent "See also" footer.
- **No API changes.** Documentation only — no behaviour, signatures, config
  keys, or version numbers were altered.

## [4.5.3] — 2026-06-20

### Added

- **Three schema sync modes — `SyncMode`.** A new operator-facing knob on each
  database (`config.mysql.json`) replaces the lower-level `SchemaSyncLevel` /
  `AllowDestructiveSync` flags (which still work for back-compat — `SyncMode` takes
  precedence and maps onto them via `EffectiveSyncLevel`).

  | Mode | Behaviour |
  |---|---|
  | `Developer` | Aggressive rolling updates — drops removed columns/indexes/FKs on every boot (maps to `Full`). |
  | `Production` *(default)* | Additive only — adds/modifies, **never drops**. A change that needs a drop is deferred and the table is flagged `DriftPending`. |
  | `Migration` | Deliberate one-shot destructive reconcile (takes a schema backup first). Idempotent — once every model matches and no drift is pending it does nothing and logs a warning to switch back to `Production`. |

  ```json
  { "Databases": { "Default": { "SyncMode": "Production" } } }
  ```

- **CRC sentinel — `__schema_state`.** Each model's desired schema is hashed
  (CRC) into a per-table row. Sync skips a table **entirely** — no
  `information_schema` diffing, no DDL — when the stored CRC matches the model
  *and* the table still exists. New `SyncResult` fields: `Skipped`, `SchemaCrc`,
  `DriftPending`; new `SchemaSyncStatus` enum (`Synced` / `DriftPending`).
  Exposed via `mysql.SchemaState` (a `SchemaStateStore`).

- **Cross-node schema-sync lock — `SchemaSyncLock`.** A schema/migration pass
  serializes across application nodes with MySQL `GET_LOCK`. The winner runs the
  DDL; peers wait, then find the schema already reconciled (matching CRCs) and do
  nothing.

- **Batch schema sync + runtime mode override.** `mysql.SyncSchemaAsync(params
  Type[])` reconciles a whole set of entities as one pass under a single lock,
  honouring the configured `SyncMode` and the CRC fast-path — the recommended
  startup entry point. `mysql.SetSyncMode(mode, connectionId)` overrides the mode
  at runtime (e.g. to flip `Migration` back to `Production` once a pass completes).

- **Imperative migrations.** `IMigration` / `MigrationVersion` / the abstract
  `Migration` base for data transforms, seeds, and semantic changes the
  declarative sync can't express. `IMigrationContext` provides `ExecuteAsync`,
  `QueryAsync<T>`, `ScalarAsync<T>`, and a `SyncTableAsync<T>()` bridge into
  declarative sync. The `MigrationRunner` applies pending migrations in
  `MigrationVersion` order over the `__migrations` table, each in its own
  transaction, under the shared lock, gated by the app version
  (`CodeLogicEnvironment.AppVersion`), and warns when an applied migration's
  checksum has drifted. Library surface: `RegisterMigration`,
  `RegisterMigrationsFrom(assembly)`, `MigrateAsync`, `GetPendingMigrationsAsync`.

  ```csharp
  public sealed class SeedRoles() : Migration("1.4.0", 1, "Seed default roles")
  {
      public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
          await ctx.ExecuteAsync("INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);
  }
  ```

  > MySQL implicitly commits on DDL, so a migration that mixes `ALTER` with data
  > changes is not atomic — keep `UpAsync` steps idempotent.

- **Rollback.** `mysql.RollbackAsync(MigrationVersion target)` runs `DownAsync`
  newest-first for every applied migration above `target`, each in its own
  transaction. It pre-flights the range and aborts cleanly **before any change**
  if a migration in range has no `DownAsync` override. Declaratively,
  `mysql.RestoreSchemaAsync(tableName)` replays a `BackupManager` schema snapshot
  (DDL only — rows are lost) and clears the table's `__schema_state` row so the
  next sync reconciles from scratch.

### Fixed

- **Upsert now portable to MariaDB.** `UpsertAsync`, `UpsertManyAsync`, and
  `UpsertWithIncrementsAsync` emit the portable `... ON DUPLICATE KEY UPDATE col =
  VALUES(col)` form, which works on **both** MySQL and MariaDB, instead of the
  MySQL-8.0.19+-only `INSERT ... AS new ... ON DUPLICATE KEY UPDATE` row-alias
  syntax that MariaDB rejected.

## [4.5.2] — 2026-06-13

### Added

- **Typed JOINs.** `Query<TLeft>().Join<TRight, TKey, TResult>(leftKey, rightKey,
  resultSelector, type)` translates a strongly-typed equi-join to real SQL with
  table aliases (left `t0`, right `t1`) and a compiled, reflection-free projection
  into `TResult` — only the columns the selector references are transferred.

  ```csharp
  var views = await mysql.Query<Order>()
      .Where(o => o.Total > 100)
      .Join<Customer, long, OrderView>(
          o => o.CustomerId,                 // left key
          c => c.Id,                         // right key
          (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name })
      .OrderByDescending((o, c) => o.Total)
      .Take(20)
      .ToListAsync();
  ```

  - **Join types:** `Inner` (default), `Left`, `Right`. `Cross` is rejected for a
    keyed join (keys imply an equi-join).
  - **Composite keys:** `o => new { o.A, o.B }` matched positionally with
    `c => new { c.X, c.Y }`.
  - **Carried filters:** `.Where(...)` calls made on the left builder *before*
    `.Join` are re-qualified to the left table and preserved.
  - **Fluent surface on the join:** `.Where((l, r) => …)`, `.OrderBy` /
    `.OrderByDescending((l, r) => …)`, `.Take` / `.Skip`, and the
    `ToListAsync` / `FirstOrDefaultAsync` / `CountAsync` terminals.
  - The single-table query path and the existing raw-string
    `Join(table, condition, type)` overload are unchanged.

- **Subquery filters — `EXISTS` / `IN`.** Four new WHERE-family methods on the
  query builder translate to real SQL subqueries:

  ```csharp
  // Correlated EXISTS — correlated + non-correlated conditions in one predicate
  mysql.Query<Order>()
      .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent");

  // IN (subquery) with an optional uncorrelated inner filter
  mysql.Query<Order>()
      .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip);
  ```

  - `WhereExists<TInner>` / `WhereNotExists<TInner>` →
    `[NOT] EXISTS (SELECT 1 FROM inner WHERE …)`, correlated via the predicate.
  - `WhereIn<TInner, TKey>` / `WhereNotIn<TInner, TKey>` →
    `col [NOT] IN (SELECT innerCol FROM inner [WHERE innerFilter])`.
  - Composes with ordinary `.Where(...)` and reuses the same multi-source
    translator as joins (each source qualified by its table name).

- **Column rename — `[Column(PreviousName = "old_col")]`.** Schema sync now emits
  `CHANGE COLUMN old_col new_col …` to rename in place and **preserve the data**,
  instead of the drop-old + add-new that silently lost it (orphan column at Safe;
  data loss at Full). Works at `Safe` and above; remove `PreviousName` once every
  environment has synced.

  ```csharp
  [Column(Name = "email_address", PreviousName = "email")]
  public string EmailAddress { get; set; } = "";
  ```

- **Multi-node cache coordination — `ICacheCoordinator`.** A pluggable coordination
  seam (same model as `ICacheStore`: interface + in-process default, distributed
  adapter supplied by the consumer) that closes the single-node limitation called
  out in 4.1.2's notes. Install with `QueryCache.UseCoordinator(...)`.

  - **Cross-node invalidation** — a local mutation now fans out via
    `PublishInvalidationAsync`; a peer's broadcast bumps this node's table-version
    counter and evicts matching entries (without re-broadcasting). Previously the
    version counter was per-process, so a mutation on one node never invalidated
    the others.
  - **Single-flight pool refresh** — `SmartCachePool` ticks now acquire a refresh
    lease via `TryAcquireRefreshLeaseAsync`; only the lease holder hits the DB, so
    N nodes don't all refresh the same pool. Idle-entry retirement still runs on
    every node. Pair with a shared `ICacheStore` (e.g. Redis) so non-leaders read
    the entry the leader writes.
  - The default `NullCacheCoordinator` is single-node: no fan-out, always grants
    the lease — behaviour is identical to before off-cluster.

- **Raw SQL escape hatch.** `mysql.SqlQueryAsync<T>(sql, parameters)` materializes
  rows into `T` with the same compiled materializer as the query builder;
  `ExecuteSqlAsync(sql, parameters)` runs a non-query and returns the affected count;
  `SqlScalarAsync<T>(sql, parameters)` returns a single value. All use named
  parameters, flow through observability, and inherit the transient-retry policy.

  ```csharp
  var rows = await mysql.SqlQueryAsync<UserRecord>(
      "SELECT * FROM users WHERE country = @c", new() { ["@c"] = "DK" });
  ```

- **Transient-error auto-retry.** Single non-transactional statements that fail with
  a deadlock (1213) or lock-wait timeout (1205) are retried with exponential backoff
  + jitter. Configurable per database via `TransientRetryCount` (default 3) and
  `TransientRetryBaseDelayMs` (default 50); 0 disables. Statements inside an explicit
  transaction scope are never auto-retried — the whole transaction is the caller's
  to retry.

- **Cache stampede protection.** Concurrent cache misses on the same cold key now
  collapse to a single factory execution (single-flight) instead of a thundering
  herd of identical DB queries. Transparent — no API change.

- **Soft deletes — `[SoftDelete(nameof(DeletedUtc))]`.** Marks a nullable-`DateTime`
  column as the delete marker. `Repository.DeleteAsync` then sets it to UtcNow
  instead of issuing a physical `DELETE`, and reads via `mysql.Query<T>()` and the
  repository getters automatically exclude rows where it is set. Opt back in with
  `.IncludeDeleted()` on a query, or purge for real with `Repository.HardDeleteAsync`.

  ```csharp
  [SoftDelete(nameof(DeletedUtc))]
  public class Account { /* … */ public DateTime? DeletedUtc { get; set; } }
  ```

### Notes

- **No breaking changes.** Joins and subquery filters are new methods; the
  multi-source WHERE translator is byte-identical to the single-table translator
  when no alias map is supplied.
- **Subquery-filtered queries are not cacheable** and cannot be turned into a
  typed `.Join` — same single-table-version-stamping limitation as joins. Both
  are gated explicitly (cache silently bypassed; `.Join` throws).
- **`WhereExists` against the outer query's own table is rejected** — unqualified
  inner columns would be ambiguous.
- **Soft-delete auto-filtering applies to single-table reads only** —
  `mysql.Query<T>()` terminals and the repository getters. It does NOT apply to
  joins, subqueries, or the query builder's bulk `UpdateAsync`/`DeleteAsync` (those
  stay raw so you can target or restore deleted rows). `QueryBuilder.DeleteAsync`
  is a hard delete regardless of `[SoftDelete]`.
- **Joins are not cacheable in this version.** The result cache stamps each entry
  with a single table's version counter, so a join entry could not be invalidated
  when the *other* joined table mutates. `.WithCache` / `.SmartCache` are
  intentionally absent on `JoinedQuery` rather than risk serving stale joins;
  multi-table invalidation is on the roadmap.
- **`TRight` must be specified explicitly** (e.g. `Join<Customer, long, OrderView>`)
  — it cannot be inferred from a lambda parameter type.

## [4.5.0] — 2026-05-24

### Added

- **`StorageType` enum on `ColumnAttribute`.** Per-column physical storage
  override that takes precedence over `DataType` for DDL generation.
  Available values: `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`,
  `LongBlob`. When set, the column is stored as the chosen binary type and
  values are automatically converted to/from binary on read and write.

- **Guid-as-BINARY(16) support.** Set `StorageType = StorageType.Binary` on a
  `Guid` property and CL.MySQL2 stores it as `BINARY(16)` using RFC 4122
  big-endian byte layout for correct lexicographic sort order. Conversion is
  automatic in all paths: insert, update, read, WHERE clauses, and IN queries.

  ```csharp
  [Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
  public Guid Id { get; set; }
  ```

- **Automatic binary conversion for all CLR types.** Any property can be
  stored as binary by setting `StorageType`. Supported types and their binary
  sizes (auto-detected when `Size` is not explicit):

  | CLR type | Binary size | Byte order |
  |---|---|---|
  | `Guid` | 16 | RFC 4122 big-endian |
  | `long` / `ulong` | 8 | big-endian |
  | `int` / `uint` | 4 | big-endian |
  | `short` / `ushort` | 2 | big-endian |
  | `double` | 8 | big-endian |
  | `float` | 4 | big-endian |
  | `decimal` | 16 | big-endian |
  | `DateTime` / `DateTimeOffset` | 8 | ticks, big-endian |
  | `bool` / `byte` / `sbyte` | 1 | — |
  | `string` | explicit | UTF-8 |
  | `byte[]` | passthrough | as-is |

- **LINQ WHERE support for binary-stored columns.** Expressions like
  `repo.Where(x => x.Id == someGuid)` and `list.Contains(x.Id)` correctly
  convert parameter values to binary when the column uses `StorageType`.

- **`SequentialGuid.NewId()` helper.** Generates time-ordered UUIDv7 values
  optimized for `BINARY(16)` primary keys. Sequential inserts append to the
  B-tree instead of causing random page splits — dramatically reducing index
  fragmentation compared to random UUIDv4.

  ```csharp
  [Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
  public Guid Id { get; set; } = SequentialGuid.NewId();
  ```

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt`. AssemblyVersion is derived automatically.

### Notes

- **No breaking changes.** `StorageType` defaults to `StorageType.Default`
  (the zero-value), so all existing entities and schemas are unaffected.
  Guid inference still returns `Char(36)` unless you explicitly opt in.

## [4.2.3] — 2026-05-15

### Fixed

- **Cache orphan accumulation on mutations.** `QueryCache.Invalidate(tableName)`
  previously only bumped the per-table version counter — old cache entries
  (now unreachable via the read path because the cache key changed) lingered
  in the underlying store until TTL or LRU swept them. On a busy app this
  produced unbounded memory growth. Invalidate now also calls
  `ICacheStore.EvictByTableAsync` to sweep matching entries in the same step.
- **SmartCachePool orphan tracking.** Each pool entry now remembers the
  cache key it last wrote. If the next tick computes a different key
  (because a mutation bumped the table version between ticks), the
  previous key is evicted explicitly. Works on any `ICacheStore`
  implementation including ones that can't enumerate (Redis without
  SCAN, memcached).

### Added

- `ICacheStore.EvictByTableAsync(tableName)` — bulk eviction by table.
  Default in-process implementation is an O(n) scan over current entries.
  Distributed adapters can override (e.g. Redis tag-set or key prefix).
- `ICacheStore.CountByTable()` — entries grouped by tableName for diagnostics.
- `QueryCache.GetStats()` → `QueryCacheStats(TotalEntries, EntriesByTable,
  TableVersions)`. Surfaced on the library API via `MySQL2Library.GetCacheStats()`
  so admin tools can render "what's in the cache right now" without
  dumping values.

## [4.2.2] — 2026-05-15

### Fixed

- **SmartCache pool no longer corrupts `ToListAsync` results.** The pool's
  refresh factory stored the unwrapped `List<T>` instead of the
  `Result<List<T>>` that the cache-aside read path expects. After the first
  background refresh tick, every subsequent read failed the `(Result<List<T>>)`
  cast inside `GetOrSetAsync`, the outer try/catch turned it into a Failure
  Result, and callers saw an empty list (manifested as "No servers configured"
  / empty leaderboards roughly one refresh interval after warm-up). `FirstOrDefaultAsync`
  and `CountAsync` already cached the full Result and were unaffected; only
  `ToListAsync` was wrong.

## [4.2.1] — 2026-05-15

### Fixed

- **Failure Results no longer poison the cache.** Previously, a query that
  failed (e.g. transient connection error during a cold warm-up) had its
  `Result<T>.Failure` value cached just like a successful one — subsequent
  reads served the failure until the entry's TTL expired or a pool refresh
  overwrote it. Now `QueryCache.GetOrSetAsync` skips writing failure Results,
  evicts any pre-existing failure entry on read, and `SetDirectAsync` (the
  smart-cache pool's refresh path) refuses to write failures too. Empty
  server lists / leaderboards on first request after a deploy are gone.

## [4.2.0] — 2026-05-15

### Added

- **Smart-cache pool warm-up on registration.** `RegisterCachePool` now
  accepts an optional `warmUp: Func<Task>` callback that fires as a
  fire-and-forget task right after the pool starts. The callback just
  calls the queries that should be warm — they auto-register with the
  pool via their normal `.SmartCache(name)` decoration — so the cache
  is hot before the first user request hits it. Exceptions are caught
  and logged; the pool stays lazy if warm-up fails.
- `SmartCachePool.WarmUp(Func<Task>)` — public method exposing the same
  behaviour for callers that want to warm a pool independently of
  registration.

## [4.1.2] — 2026-05-15

### Added

- **Smart cache pools** — named groups of cached queries kept warm by a
  background timer (`mysql.RegisterCachePool("dashboard", refreshEvery: 30s)`,
  opt in per-query with `.SmartCache("dashboard")`). Reads after the first
  populate the cache never block on the DB — the pool's timer re-runs every
  registered query in the background and overwrites the entry.
- `SmartCachePool.RefreshNowAsync()` — out-of-schedule refresh, useful right
  after a deploy to prime the cache before the first user hits the page.
- `MySQL2Library.GetCachePoolStats()` — diagnostic snapshot per pool
  (entry count, ticks fired, ticks failed, last tick UTC).
- `QueryCache.SetDirectAsync(...)` — internal cache write API used by pools.

### Notes

- Smart cache is mutually exclusive with `.WithCache(TimeSpan)` — if both are
  set, the pool wins and the TTL comes from `refreshEvery * 2`.
- Unknown pool name on `.SmartCache(name)` logs a warning and falls back to
  non-cached execution (no exception).
- Per-pool eviction policy: an entry that has not been read for
  `MaxIdleFires` (default 3) consecutive ticks is dropped from the refresh
  list. Bounds cardinality on parameterized queries.
- Smart cache is disabled inside a transaction scope (same as `.WithCache`).
- Single-node only in v4.2. Multi-node coordination is on the roadmap.

## [4.1.1] — 2026-04-17

### Fixed

- Qualify LHS columns in upsert SET clauses so `UpsertAsync` no longer
  generates ambiguous column references when the table has columns whose
  names clash with parameter placeholders.

## [4.1.0] — 2026-04-17

### Added

- **Typed upsert** — `UpsertAsync` + `UpsertWithIncrementsAsync` on the
  repository. Compiles to `INSERT ... ON DUPLICATE KEY UPDATE ...` with
  full LINQ-shaped value/increment expressions on the update side.

### Changed

- `LibraryManifest.Version` now reads from the assembly's `AssemblyVersion`
  attribute at runtime instead of being a hard-coded string. Keeps the
  manifest honest across rebuilds.

## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh across every CodeLogic library for the v4 baseline.
- No functional changes vs 4.0.3.

## [4.0.1] — 2026-04-09

### Fixed

- Drop the `Expression.Compile().DynamicInvoke()` fast-path inside the SQL
  expression visitor — it broke on closures over generic types. The visitor
  now always walks the tree.

## [4.0.0] — 2026-04-09

Major rewrite. Breaking.

### Added

- **Projection pushdown** — `.Select<TResult>(x => new { ... })` emits a
  real `SELECT col1, col2, ...` column list instead of `SELECT *`. Combined
  with compiled materializers this often cuts row-transfer bandwidth by 80%+.
- **SQL-side aggregation** — `.GroupBy(...).Select(g => new { g.Key,
  g.Sum(...), g.Average(...), ... })` translates to real `GROUP BY` +
  aggregate functions. No client-side row materialization.
- **`SqlFn` helpers** — server-side function markers (`SqlFn.DayOfWeek`,
  `SqlFn.Hour`, `SqlFn.BucketUtc`, `SqlFn.Coalesce`, `SqlFn.Round`, etc.)
  recognized by the translator — mirrors EF's `EF.Functions` pattern.
- **`[Index]` attribute** — declare named, unique, and covering indexes
  (with `Include = new[] { ... }`) at the column level.
- **`[RetainDays]` attribute** — opt entities into a daily background purge
  worker that runs batched `DELETE` until drained.
- **Working result cache** — `.WithCache(TimeSpan)` with two correctness
  fixes from prior versions:
  - DateTime closures near `UtcNow` are time-quantized to a configurable
    window (default 60s) so `.Where(x => x.At >= UtcNow.AddDays(-30))`
    stops producing a unique cache key per call.
  - Mutations bump a per-table version that participates in the cache
    key — invalidation is free (old keys become un-hittable, no eviction
    loop).
- **`EntityMetadata<T>` + compiled `Materializer<T>`** — reflection runs
  once per entity at first use; subsequent reads use a compiled
  reader-to-entity function.
- **Observability events** — `QueryExecutedEvent`, `SlowQueryEvent`,
  `CacheHitEvent`, `CacheMissEvent`, `N1QueryDetectedEvent`,
  `TableSyncedEvent` publish to the CodeLogic event bus.
- **`MaxBatchInsertSize`, `MaxInClauseValues`, `PreparedStatementCacheSize`,
  `N1DetectorThreshold`, `CaptureExplainOnSlowQuery`, `DefaultStringSize`,
  `CacheEnabledOverride`** — per-database config knobs.
- **`CacheConfiguration`** — global cache settings (`Enabled`,
  `MaxEntries`, `DefaultTtlSeconds`, `TimeQuantizeSeconds`,
  `PublishEvents`).

### Changed

- Republished as v4.0.0 to reset the version line with the new package shape.
- All public APIs refreshed under the v4 baseline.

## Earlier releases

Pre-4.0 history is retained in the
[git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.MySQL2)
but is not documented in detail here — the library shape changed
significantly in the v4 rewrite.
