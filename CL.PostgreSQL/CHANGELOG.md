# CL.PostgreSQL — Changelog

All notable changes to **CodeLogic.PostgreSQL** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 2026-10-24

### Added

- **`Repository<T>.InsertIgnoreAsync` and `InsertManyIgnoreAsync`** (API parity with CL.MySQL2
  and CL.MSSQL). A duplicate key (any UNIQUE constraint, unique index or PRIMARY KEY) is a normal
  outcome: `InsertIgnoreAsync` returns `InsertOutcome.Inserted` (auto PK populated from
  `RETURNING`) or `InsertOutcome.Duplicate`; `InsertManyIgnoreAsync` returns the number of rows
  actually inserted, and duplicates within the batch itself count as 0 too. No exception, no Error log.
  - Built on `INSERT ... ON CONFLICT DO NOTHING` with no conflict target, so every unique
    arbiter is covered.
  - **Atomic on failure**: the call runs in its own transaction, or a savepoint inside an explicit
    `TransactionScope`; any non-duplicate error (NOT NULL, CHECK, FK, ...) rolls back everything
    the call wrote, so a failure `Result` means nothing was written. Inside a scope the savepoint
    also keeps the caller's transaction usable, which PostgreSQL otherwise aborts on any error.
- **Opt-in `DistinguishDuplicateKeyError`** (`PostgreSqlDatabaseConfig` or `Repository<T>`
  property): a unique violation (SQLSTATE `23505`) in `InsertAsync` / `InsertManyAsync` returns
  error code `postgresql.duplicate_key` and logs at Warning. Default `false` keeps
  `postgresql.insert_failed` / `postgresql.bulk_insert_failed` at Error.
- **Nullable aggregate results**: `MaxAsync` / `MinAsync` / `SumAsync` accept nullable selectors
  such as `r => (DateTime?)r.CreatedUtc` and return `null` for SQL NULL (for example no matching
  rows). Non-nullable selectors still return `default(TResult)`.
- **`[CompositeIndex]` column drift warning**: when an index declared on the model exists under
  the same name but with different columns or column order, schema sync logs a Warning naming
  the table, the index and both column lists. The index is never dropped or rebuilt automatically.

## 2026-09-13

### Changed

> **Read this before upgrading if you set `defaultSchema`.**

- **BREAKING — `defaultSchema` now moves your entities.** It used to be applied only as the
  Npgsql `SearchPath` on the connection string while every generated statement was qualified
  with the hard-coded constant `public`. Setting `"defaultSchema": "app"` therefore gave you
  a `search_path` of `app` while all DDL and DML targeted `public`. It now does what it says:
  an entity **without** `[Table(Schema = "…")]` is created in, and every statement for it
  qualified with, the configured schema. `[Table(Schema = "…")]` still wins where present,
  and the schema is created with `CREATE SCHEMA IF NOT EXISTS` on first sync.

  *Migration.* If you left `defaultSchema` at its default `public`, nothing changes. If you
  set it to anything else, your live tables are in `public` and the library will now look for
  them in the configured schema — it will create empty tables there on the next sync. Either
  set `defaultSchema` back to `public` (and pin per-entity schemas with
  `[Table(Schema = "…")]` if you want them elsewhere), or move the tables first:

  ```sql
  CREATE SCHEMA IF NOT EXISTS app;
  ALTER TABLE public.users SET SCHEMA app;   -- per table
  ```

  Resolution is per connection, so two named connections may now map the same entity types
  into different schemas. `PostgreSQLLibrary.RestoreSchemaAsync` follows suit: its
  `schemaName` parameter defaults to `null`, meaning "the connection's configured schema",
  instead of the literal `public`.

- **BEHAVIOUR — `Repository<T>.CountAsync` now applies the soft-delete filter.** It emitted
  a bare `SELECT COUNT(*)` while `GetAllAsync` and `GetPagedAsync` in the same class filtered
  `IS NULL`, so the two contradicted each other on a `[SoftDelete]` entity. `CountAsync()`
  and `GetAllAsync().Count` now agree. If you were relying on it to report the physical row
  count, use `Query<T>().IncludeDeleted().CountAsync()`.

- **DEFAULT — `captureExplainOnSlowQuery` now defaults to `false`.** It was declared `true`
  but nothing read it, so no plan was ever captured. Now that the capture is implemented,
  defaulting it to `true` would have started running an `EXPLAIN` for every slow query on
  upgrade; the default was flipped so runtime behaviour is unchanged. Set it to `true` to
  opt in.

  *Caveat.* Changing the declared default only covers configs that never wrote the key. A
  `config.postgresql.json` persisted by an earlier version may already contain
  `"captureExplainOnSlowQuery": true` on disk — that value is now honoured, and such an
  install **will** start capturing plans on upgrade. Set it to `false` explicitly if that is
  not what you want. The same applies to `n1DetectorThreshold` if a persisted config carries
  a non-zero value.

- `CacheConfiguration.MaxMemoryMb` and `PostgreSqlDatabaseConfig.PreparedStatementCacheSize`
  are marked `[Obsolete]`. Neither is read: the in-process cache evicts by entry count
  (`MaxEntries`), and statement caching is Npgsql's, configured on the connection string via
  `Max Auto Prepare` / `Auto Prepare Min Usages`. Both still compile and round-trip.

### Added

- **Slow-query `EXPLAIN` capture.** With `captureExplainOnSlowQuery` on, a query that
  crosses `slowQueryThresholdMs` has `EXPLAIN (FORMAT JSON) <sql>` run with the same bound
  parameters on a separate connection, and the plan attached to `SlowQueryEvent.ExplainJson`.
  Strictly best-effort: fetched off the query path, skipped entirely for a query inside a
  transaction scope, skipped for statements `EXPLAIN` cannot accept (DDL, utility commands,
  multi-statement batches) and for parameterized statements whose values were not captured,
  and any failure leaves the event publishing with a null payload.
- **N+1 detection.** With `n1DetectorThreshold` above 0, executions of the same normalized
  SQL template are counted per connection over a rolling one-second window, and
  `N1QueryDetectedEvent` is published once per window when the count reaches the threshold.
  `0` (the default) disables it at the cost of a single bool read per query; the bookkeeping
  is capped at 512 templates and pruned by age.
- **`WithCache()` with no arguments**, on both the query builder and a projected query, using
  `postgresql.cache.defaultTtlSeconds` (60). Purely additive — `WithCache(TimeSpan)` is
  unchanged.
- `BackupManager.CleanupOldBackupsAsync` and `GetLatestBackupFile` take a `connectionId` so
  they resolve the same backup directory the writes used.

### Fixed (configuration that was declared but never read)

- **`queryTimeoutMs` is applied** as the command timeout on the commands the library creates
  (rounded up to whole seconds; `0` = no timeout). The 30 000 ms default matches both
  `commandTimeout` and Npgsql's own 30-second default, so nothing changes unless you change it.
- **`maxBatchInsertSize` reaches the repository.** `GetRepository<T>()` passed
  `slowQueryThresholdMs` but not the batch size, so the constructor default of 500 always
  won whatever the config said. Now plumbed through in both overloads (the `connectionId`
  one and the `TransactionScope` one).
- **`defaultStringSize` drives type inference.** `TypeConverter.InferColumn` hard-coded 255;
  schema sync now threads the connection's configured value through. Same default, so no
  change unless you set it.
- **`cacheEnabledOverride` is honoured** — when non-null it wins over the cache section's
  `enabled` for that connection. `null` (the default) inherits as before.
- **`backupDirectory` is honoured** by `BackupManager`. `null` keeps `DataDirectory/backups`.
- **`maxInClauseValues` is reported.** A generated `IN` list wider than the ceiling logs a
  warning once per query build, naming the entity and the value count. It deliberately does
  **not** throw or chunk — that would break callers who exceed it today.
- **`postgresql.cache.defaultTtlSeconds` and `publishEvents` are read.** The former backs the
  new parameterless `WithCache()`; the latter gates `CacheHitEvent` / `CacheMissEvent`
  publication (still `true` by default, so unchanged).
- `sslCertificatePath` / `sslKeyPath` / `sslRootCertificatePath` map to Npgsql's
  `SslCertificate`, `SslKey` and `RootCertificate`. This was already wired; the XML and docs
  now say which option each one is, since the field names do not match one-for-one.

### Fixed (correctness)

- **Retention never ran.** `RetentionWorker` snapshotted the registered entity set with
  `.ToList()` at construction, and the worker is constructed during `OnStartAsync` — but
  entities only register through `SyncTableAsync` / `SyncSchemaAsync`, which every documented
  flow calls *after* `CodeLogic.StartAsync()`. `HasWork` was therefore always false, the loop
  never started, and `[RetainDays]` was dead in normal usage. The worker's entry list is now
  live: registration hands each entity to it as it happens and starts the loop (idempotently)
  on the first `[RetainDays]` entity. The 5-minute initial delay, 24-hour interval,
  `RunOnceAsync()` and clean disposal are unchanged.
- **`ProjectedQuery` and `JoinedQuery` dropped the caller's `CancellationToken`** when
  handing work to `ConnectionManager.ExecuteWithConnectionAsync`, so opening the connection
  and the transient-retry backoff around it ignored cancellation. Forwarded, matching
  `QueryBuilder` and `Repository`.
- **`WhereExists(...).Select(...).WithCache(...)` could serve stale cross-table results.**
  `ShouldCache` / `ShouldSmartCache` deliberately refuse to cache a subquery-filtered query —
  the cache stamps an entry with one table's version counter, so a mutation on the inner
  table cannot invalidate it — but `Select` forwarded the cache decoration into the projection
  without that guard, and `.WithCache` called on the projection afterwards re-armed it. The
  verdict now travels with the query into `Select` and `GroupBy`, and a `WithCache` /
  `SmartCache` call on such a projection is ignored and logged instead of silently caching.

### Documentation

- Corrected the `SqlFn` XML documentation for the date-part helpers, which still described
  MySQL's `DAYOFWEEK(d) - 1` adjustment. PostgreSQL's `EXTRACT(DOW …)` already matches .NET's
  numbering and no adjustment is applied.
- **Raw SQL never joins a `TransactionScope`.** `SqlQueryAsync`, `SqlScalarAsync` and
  `ExecuteSqlAsync` have no scope overload and always take their own pooled connection, so a
  call inside an `await using` scope commits independently and is not rolled back with it.
  The query-builder page now warns about this and points at `GetRepository<T>(tx)`,
  `Query<T>(tx)` and `IMigrationContext`.
- **`[RetainDays]` only runs for entities synced before `CodeLogic.StartAsync()`.** The
  retention worker is constructed at start-up from a snapshot of the registered entity
  types, and entities register through `SyncTableAsync` / `SyncSchemaAsync`. Syncing after
  `StartAsync()` — the order every quick-start shows — leaves the worker with nothing to do.
  The schema page now states the ordering requirement and the `RunOnceAsync` alternative.
- **`RegisterDatabase` does not exist.** The README and overview showed
  `pg.RegisterDatabase(id, config)` for adding a connection at runtime; the real call is
  `pg.ConnectionManager.RegisterConfiguration(config, id)` — config first, id second.
- **The typed join sample would not compile.** `Join<TRight, TKey, TResult>` takes a left
  key selector, a right key selector and a result selector; the sample passed a two-argument
  join *condition* instead. `WhereIn` was shown with a two-parameter outer selector for the
  same reason — it takes `u => u.Column`.
- **Offset paging on the query builder is `ToPagedListAsync(page, pageSize)`**, not
  `GetPagedAsync` (which is the repository's method).
- **`RollbackAsync` takes a `MigrationVersion`**, not a `toVersion` string; the migrations
  sample now shows `new MigrationVersion("1.3.0", 0)`. `pg.RestoreSchemaAsync(...)` is the
  library-level restore entry point, not `pg.RestoreTableSchemaAsync`.
- **`RegisterCachePool`'s third argument is `maxIdleFires` (an `int`), and its warm-up
  callback is a `Func<Task>`** — the caching sample passed a query lambda in the int slot.
  The default `maxIdleFires` is 10, not 3, and it retires an idle *entry* rather than
  stopping the pool's timer.
- **There is no `AnyAsync` terminal** on the query builder; `Any` exists only inside a
  grouped projection as `g.Any()`.
- **Date-part translations normalise to UTC.** The query-builder page now shows the real
  SQL — `EXTRACT(… FROM (x) AT TIME ZONE 'UTC')::int`, `((x) AT TIME ZONE 'UTC')::date`, and
  `ROUND(v::numeric, d)::double precision` for `SqlFn.Round`.
- **`IN` lists are never chunked.** Several pages claimed `Contains` chunked at
  `maxInClauseValues`; every value is emitted in one list and the setting is not consulted.
  Chunk large sets yourself.
- **`EXPLAIN` capture and the N+1 detector are not implemented.** `captureExplainOnSlowQuery`
  and `n1DetectorThreshold` are reserved: no call site runs `EXPLAIN`, so
  `SlowQueryEvent.ExplainJson` is always null, and `N1QueryDetectedEvent` is never published.
  The feature list, event table and performance page no longer advertise them as working.
- **Documented several configuration keys that nothing reads.** `maxBatchInsertSize`,
  `maxInClauseValues`, `defaultStringSize`, `queryTimeoutMs`, `preparedStatementCacheSize`,
  `backupDirectory`, `cacheEnabledOverride`, and the cache section's `defaultTtlSeconds` and
  `publishEvents` are now marked as not currently applied, naming the value actually in force.
- **`defaultSchema` does not move entities.** It is applied as the connection's
  `search_path`; an entity without `[Table(Schema = …)]` always maps to the literal `public`.
- **`GetCacheStats()` returns structure, not counters** — total entries, entries by table
  and table-version counters. It has never reported hits, misses or evictions.
  `GetCachePoolStats()` likewise reports interval, entry count and tick counts, not hit counts.
- **Transient retry also covers SQLSTATE `55P03`** (lock not available), alongside `40001`
  and `40P01`. The config field's XML still named MySQL's error numbers 1213 and 1205.
- **`UpsertWithIncrementsAsync` return value.** Its XML documented MySQL's
  "2 = update" affected-row convention; PostgreSQL counts an `ON CONFLICT … DO UPDATE` row
  once, so the result is 1 whether the row was inserted or updated.
- Removed further MySQL leftovers from the XML comments: the config file was named
  `config.mysql.json` and the localization file `mysql.{culture}.json`; the column-reference
  builder claimed backtick quoting (PostgreSQL uses double quotes); the CRC, schema-state and
  sync comments said `information_schema` where the analyzer reads `pg_catalog`; the
  retention worker described a `DELETE … LIMIT` friendly to "InnoDB's undo log" rather than
  its actual `ctid` + `FOR UPDATE SKIP LOCKED` batching; and the query-builder samples called
  `mysql.Query<T>()`.
- `[Column(Charset = …)]` is documented as what it emits — a `COLLATE` clause — and
  `[Column(Unsigned = …)]` as inert, since PostgreSQL has no unsigned integer types.
- `IMigrationContext` offers `ExecuteAsync`, `QueryAsync<T>`, `ScalarAsync<T>` and
  `SyncTableAsync<T>` plus the raw connection and transaction; it has no `TableExistsAsync`
  and does not expose the analyzer.
- Retention's schedule is stated: a first pass five minutes after startup, then every 24 hours.
- Removed a duplicated `<summary>` block on the schema analyzer's column-diff check whose
  first copy still described MySQL facets (auto-increment, charset).
- Corrected the `connectionLifetime` XML (it is the pooled *idle* lifetime) and the
  `TypeConverter` example type strings (`character varying(255)`, not `VARCHAR(255)`).

### Fixed (found while completing PostgreSQL coverage)

- **A migration registered twice ran twice.** `Register` and `RegisterFrom` both appended
  unconditionally, so the documented pairing of `RegisterMigrationsFrom(assembly)` with an
  explicit `RegisterMigration(...)` held two copies of the same migration — and because the
  apply pass filters candidates against a snapshot of applied ids taken before it starts,
  both copies passed the filter. A non-idempotent body (an INSERT, a backfill, an ALTER
  without IF NOT EXISTS) would be applied twice. Registration now deduplicates by
  migration id.
- **`HealthChangedEvent` was declared but never raised**, so anything subscribing to it
  waited forever. It is now published when the aggregate health state transitions — not on
  every poll — and a failing subscriber cannot turn a healthy library unhealthy.

### Fixed (SQL functions)

- **`SqlFn.Round(value, digits)` generated invalid SQL.** PostgreSQL's two-argument
  `round` accepts `numeric` only, so `round(double precision, integer)` does not exist.
  The value is now cast for the call and back for the result.
- **Date-part functions read the session time zone, not UTC.** `EXTRACT` over a
  `timestamptz` uses the server's `TimeZone` setting, so on a server set to `Europe/Paris`
  an instant stored as 15:09 UTC reported hour 16 — and around midnight the day, month and
  year shifted too. Since the library writes every `DateTime` as UTC, `Year`, `Month`,
  `Day`, `Hour`, `Minute`, `DayOfWeek` and `Date` now read back in UTC.
- `ConnectionManager.GetServerInfoAsync` still ran the MySQL query
  `SELECT VERSION(), @@version_comment, DATABASE(), @@hostname`, which PostgreSQL rejects
  outright. Replaced with `version()`, `current_setting`, `current_database()` and
  `inet_server_addr()`.

### Fixed (schema scoping)

- **The schema-state sentinel was keyed on the bare table name.** Two entities with the
  same table name in different schemas therefore shared one row: whichever synced last
  owned the CRC, and a later model change to the other was skipped by the fast path. The
  key is now `schema.table`. `SchemaStateStore` resolves an unqualified name against the
  default schema, so the public diagnostic API still accepts a bare table name.
- **Sync now creates the schema it needs.** An entity declaring
  `[Table(Schema = "...")]` failed on first run with `3F000: schema does not exist`, even
  though sync already creates tables, indexes, constraints and triggers. `CL.MSSQL` had
  always created its own; PostgreSQL now matches.
- **Restoring a table from backup did not clear its CRC sentinel**, so the next sync would
  skip a table that had just been rebuilt from possibly-different DDL.

### Changed

- **Rebuilt on the `CL.MySQL2` architecture.** The library's internals were replaced with a
  dialect-swapped port of `CL.MySQL2`, the same way `CL.MSSQL` was built, taking the source
  tree from 17 files to 45 and bringing the three database libraries onto one codebase shape.

### Added

- `RetentionWorker.RunOnceAsync()` — the retention pass was only reachable from a
  background loop that wakes once a day behind an initial delay, so there was no way for an
  operator to trigger a purge (or for a test to exercise one deterministically).

- Query cache with table-version invalidation, named smart-cache pools with background
  refresh, and a pluggable cache store / coordinator for multi-node deployments.
- Database-backed migrations: `IMigration`, `MigrationRunner`, `IMigrationContext`,
  version-ordered plans, and rollback. Migration history now lives in a table rather than a
  local JSON file, so instances of the same application no longer each keep their own copy
  and re-run everything.
- CRC-gated schema state tracking plus a `pg_advisory_lock`-based sync lock, so several
  instances starting at once no longer race on DDL.
- Joins, typed projections, grouping, cursor (keyset) pagination, `WhereIn`, `WhereNotIn`,
  `WhereExists` and `WhereNotExists`.
- `ON CONFLICT` upserts (`UpsertAsync`, `UpsertManyAsync`, `UpsertWithIncrementsAsync`),
  soft delete, a retention worker, and query observability events.
- `EntityMetadata<T>` with compiled property accessors and a compiled row materializer,
  replacing the previous per-row reflection -- the old mapper ran a linear property scan
  with a `GetCustomAttribute` call for every column of every row.
- PostgreSQL-native type support: `uuid`, `timestamptz`, `jsonb`, arrays, ranges, `inet`,
  `macaddr`, identity columns, and `INCLUDE` covering indexes.

### Security

- **Fixed SQL injection through unvalidated column names.** `GetByColumnAsync`,
  `GetPagedAsync(orderByColumn)` and the dictionary overload of `QueryBuilder.UpdateAsync`
  interpolated caller-supplied strings directly into SQL. All string-typed column APIs now
  resolve through an `EntityMetadata<T>` allow-list, and identifiers are rendered through
  `PostgreSqlDialect.Quote` and validated where they enter the metadata.
- **Fixed connection-string injection.** Connection strings were assembled by string
  concatenation, so a `;` in a password or database name could append arbitrary connection
  options. They are now built with `NpgsqlConnectionStringBuilder`.
- **`AllowDestructiveSync` is no longer a dead setting.** It was declared, surfaced in the
  configuration UI as a guard against `DROP` during schema sync, and never read anywhere in
  the library. Destructive DDL is now gated by `SyncMode` / `SchemaSyncLevel`, with
  `AllowDestructiveSync` honoured for backwards compatibility.
- LIKE metacharacters in user-supplied values are escaped, so a `%` in a search term no
  longer silently changes the result set.
- Backup filenames are sanitised rather than interpolated from schema and table names.

### Fixed

- **Retention purged every entity against the `Default` connection.** The registered-entity
  set recorded types with no connection association, so an entity synced against a named
  connection was purged from the wrong database — in practice the `DELETE` hit a database
  where the table did not exist, the failure was caught and logged, and the retention the
  `[RetainDays]` attribute described silently never happened. Registrations now carry the
  connection they were made against, and the same entity synced to two connections is two
  registrations. Only reachable since the worker began running at all in this same release.
  `RetentionWorker.Register(Type, string)` and a `Registrations` view are added; the existing
  type-only overload keeps its meaning and registers against the worker's own connection.
- **`ids.Contains(x.Id)` on a `List<T>` or `HashSet<T>` threw instead of emitting `IN`.**
  The expression visitor's first `Contains` case matched any single-argument instance call,
  so a collection membership test took the string `LIKE` branch and tried to emit the
  collection itself as a column. Arrays were unaffected because they bind to the static
  two-argument `Enumerable.Contains`, which had its own case. The `LIKE` branch is now
  restricted to a string receiver, and both membership shapes share one emitter.
- **Schema sync no longer rewrites every table on every startup.** The analyzer compared
  `information_schema.data_type` against the generated DDL with a lowercase string compare.
  Those vocabularies never match -- PostgreSQL reports `character varying`, `numeric`,
  `timestamp with time zone`; the generator emitted `VARCHAR(255)`, `NUMERIC(10,2)`,
  `TIMESTAMPTZ` -- so every string, decimal, timestamp, time and array column was issued an
  `ALTER COLUMN ... TYPE` on each sync, taking an `ACCESS EXCLUSIVE` lock and rewriting the
  table. Types are now canonicalised through an alias table before comparison.
- Unique columns produced two unique constraints: one inline in the column definition and
  one as a separate named constraint.
- DDL scripts were split on bare `;`, which broke any statement containing a semicolon in a
  default or comment, and each fragment ran on its own connection so a table and its indexes
  were not created atomically.
- `Contains()` over an empty collection emitted `IN ()`, a syntax error. It now emits a
  false literal.
- `ToPagedListAsync` returned the first group's row count instead of the number of groups
  when combined with `GroupBy`.
- `InsertManyAsync` issued one round trip per row and was not transactional, so a failure
  part-way through left earlier rows committed. Inserts are now batched and bounded by
  PostgreSQL's 65535-parameter statement limit.
- Bitwise `&` and `|` in a predicate were translated to logical `AND` / `OR`, silently
  corrupting integer bitmask comparisons.
- The retention worker used `DELETE ... LIMIT`, which PostgreSQL does not support; batches
  are now selected by `ctid` with `FOR UPDATE SKIP LOCKED`.
- Cancellation tokens were not forwarded to connection acquisition, so opening a connection
  could not be cancelled.
- `ConnectionManager` held its configuration map in a non-concurrent `Dictionary` that could
  be written by `RegisterConfiguration` while another thread read it.
- `ExecuteWithConnectionAsync` disposed the connection twice.

### Verified against a live server

Every integration test runs against PostgreSQL 18.4 (156 tests, none skipped), covering
all 45 `DataType` members, every inferred CLR mapping, value round-trips, the full set of
`ALTER` operations, both sync modes, migrations, caching, retention and the observability
events. Three defects only execution could surface were fixed:

Every integration test now runs against PostgreSQL 18.4 (115 tests, none skipped). Two
defects that only execution could surface were fixed in the process:

- `SchemaSyncLock` issued `SET lock_timeout = @ms`. `SET` is parsed before parameters are
  bound, so the server saw `SET lock_timeout = $1` and raised `42601`. The advisory lock is
  taken at the start of every sync, so this broke schema synchronisation outright. Now uses
  `set_config()`, which takes the value as a bound argument.
- The catalog readers in `SchemaAnalyzer` and `BackupManager` read `a.attidentity` as a
  string. It is the internal `"char"` type, which Npgsql will not return as one, and this
  broke every `ALTER` path. Both now cast to `text` in SQL.
- A `daterange` column returns `NpgsqlRange<DateTime>`, so a property declared
  `NpgsqlRange<DateOnly>` failed with a message naming both sides as ``NpgsqlRange`1`` — a
  message that names neither type usefully. Range bounds are now converted element-wise,
  and conversion failures report full generic type names.

### Added (API)

- `GetRepository<T>(TransactionScope)` and `Query<T>(TransactionScope)`.
  `BeginTransactionAsync` returned a scope that neither accessor took, so callers had to
  construct `Repository<T>` by hand to do any work inside a transaction.

### Migration notes

This release is **not source-compatible**. Renames and behaviour changes:

| Before | After |
|--------|-------|
| `PostgreSQLConfig` | `DatabaseConfiguration` |
| `DatabaseConfig` | `PostgreSqlDatabaseConfig` |
| config section `mysql` (a port leftover) | `postgresql` |
| `SslMode` | `PostgreSqlSslMode` |
| default port `3306` | `5432` |
| `QueryRaw()` | `SqlQueryAsync<T>()` / `ExecuteSqlAsync()` |
| `Models/Configuration.cs` | `Configuration/DatabaseConfiguration.cs` |

- The `DataType` enum is now PostgreSQL's type set. `DataType.Unspecified` is the default
  and infers from the CLR property type; `Guid` infers `uuid` rather than `CHAR(36)`, and
  `DateTime` infers `timestamptz`.
- Upserts need a conflict target. `ON CONFLICT` arbitrates on one named unique key rather
  than MySQL's "any duplicate key". It is inferred when the entity has exactly one candidate
  and must otherwise be passed as `conflictTarget`.
- Non-nullable CLR value types now generate `NOT NULL` columns.
- Identifiers are emitted double-quoted and are therefore case-sensitive.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- Query-builder parameter re-keying could corrupt SQL when a predicate emitted
  11+ parameters (`@p1` substring-collided with `@p10`/`@p11`); parameters are
  now renamed longest-name-first.
- The expression translator wiped the entire WHERE buffer for a `null == x.Prop`
  comparison (it called `_sql.Clear()`), producing malformed SQL when combined
  with other clauses; null comparisons in both operand orders now translate to
  `IS [NOT] NULL` without discarding accumulated SQL.

### Documentation

- Full README rewrite to the unified house style: concise NuGet + MIT badges,
  one-line tagline, `Install` / `Quick start` / `Features` / `Configuration`
  (table + JSON) / `Documentation` / `Requirements` / `License`, with the API
  detail moved to the docs site (no full API dump in the README).
- Replaced the single `docs/libs/postgresql.md` guide with a three-page docs set
  mirroring CL.MySQL2's depth model: **Overview** (load, multi-database,
  repository CRUD, entry points, config, health, events), **Query Builder**
  (fluent methods, terminals, aggregates, bulk update/delete, raw SQL via
  `QueryRaw`/repository raw, transactions), and **Schema & Sync** (entity
  attributes, the `DataType` enum, table/set/namespace sync, `SyncResult`,
  schema backups, the migration tracker).
- The old `docs/libs/postgresql.md` is now a thin redirect to the new Overview.
- No API changes — documentation only.

## [4.5.2] — 2026-06-20

### Documentation

- Documented the full **query builder** surface: `OrderByDescending`, `Limit`/`Offset`
  (and `Take`/`Skip` aliases), `Join`, `Select`, `GroupBy`, `WithConnection`,
  `ToPagedListAsync`, `FirstOrDefaultAsync`, the `CountAsync`/`MaxAsync`/`MinAsync`/
  `SumAsync`/`AverageAsync` aggregates, and bulk `UpdateAsync`/`DeleteAsync`. Earlier
  docs listed only `Where`/`OrderBy`/`ToListAsync`.
- Documented raw SQL access via `QueryRaw()` (`QueryAsync`/`ExecuteAsync`).
- Documented the **repository** beyond basic CRUD: `InsertManyAsync`, `GetByColumnAsync`,
  `GetPagedAsync`, `FindAsync`, `IncrementAsync`/`DecrementAsync`, and
  `RawQueryAsync`/`RawExecuteAsync`.
- Documented the schema attributes `[Table]`, `[Column]`, `[ForeignKey]`,
  `[CompositeIndex]`, and `[Ignore]`, plus the `DataType` enum.
- Documented **table sync / migrations**: `SyncTablesAsync`, `SyncNamespaceAsync`,
  `SyncResult`, the `BackupManager` (schema backups + cleanup), and the
  `MigrationTracker` JSON history.
- Documented **transactions** via `BeginTransactionAsync` (auto-rollback on dispose).
- Documented previously-omitted configuration: `MaxIdleTime`, `AllowDestructiveSync`,
  multi-database `connectionId` selection, and runtime `RegisterDatabase`.

### Notes

- The 4.0.0 "repository CRUD only" note is superseded — the query builder
  (joins, aggregation, paging, bulk update/delete) is present and now documented.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated PostgreSQL configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Repository pattern + attribute-driven schema sync,
mirroring the CL.MySQL2 surface.

### Notes

- The MySQL2 4.0 query-builder rewrite (projection pushdown, SQL aggregation,
  smart-cache pools) has not been ported to CL.PostgreSQL yet — repository
  CRUD only.
- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.PostgreSQL).
