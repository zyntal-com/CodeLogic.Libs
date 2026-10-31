# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **Errors:** `DataErrors` holds every `data.*` error code, each always paired with the same error kind
  (NotFound, Conflict, Timeout, Unavailable, Unauthorized, Validation, Internal). Messages never contain
  parameter values or secrets.
- **Events:** `DataEvent` is a closed event hierarchy (query executed, slow query, N+1, cache hit/miss, schema
  synced and drift, migration, seed, retention, bulk copy, node health, circuit state). Each event carries the
  connection, provider and node.
- **Localization:** `DataStrings`, the localizable log messages (section `data`).
- **Configuration:**
  - `data` section (`DataConfig`): named connections (`DataConnectionConfig`) with provider, server, failover hosts,
    TLS, pooling, naming policy, sync mode, performance, diagnostics, resilience and replica settings, plus typed
    per-provider options (`MySql`, `PostgreSql`, `SqlServer`, `Sqlite`).
  - `data.cache` section (`DataCacheConfig`).
  - Validation reports field paths. Connection names stay case-insensitive after loading from JSON.
- **Mapping attributes:**
  - Entity and column attributes: `[Table]`, `[DataRow]`, `[Column]` (composite keys via `KeyOrder`), `[Ignore]`.
  - Relationship and index attributes: `[ForeignKey]` and `[ForeignKey<TEntity>]`, `[Index]` (multi-column, direction,
    included columns), `[CompositeIndex]`.
  - Behavior attributes: `[SoftDelete]`, `[RetainDays]`, `[ConcurrencyToken]`, `[CreatedAt]`/`[UpdatedAt]`/`[CreatedBy]`/
    `[UpdatedBy]` and `[TenantKey]`.
  - Types: a provider-neutral `DataType` enum, plus provider hint attributes (`[MySqlTable]`/`[MySqlColumn]`,
    `[PostgresTable]`/`[PostgresColumn]`, `[SqlServerTable]`/`[SqlServerColumn]`, `[SqliteTable]`/`[SqliteColumn]`).
- **Metadata model:**
  - `EntityMap` / `ColumnMap` / `KeyMap`: composite keys are first-class. Column roles: identity, soft delete,
    concurrency token, audit, tenant, retention. Also indexes and foreign keys.
  - Names are resolved per `NamingPolicy` (AsIs, SnakeCase, CamelCase) and cached.
  - Invalid models report every problem at once.
- **Entity keys:** `EntityKey` holds single and composite key values, normalized so `5` equals `5L`.
- **Accessors:** `IEntityAccessor<T>` with an `EntityAccessorRegistry` for generated accessors.
  - Types without generated mapping fall back to reflection (compiled delegates where dynamic code is available).
  - The fallback can be switched off with `CLDataReflectionMapping=false`; Native AOT publishes switch it off by default.
- **Value conversion:** `ValueReader<T>` / `ValueConversion` handle driver differences: integers as booleans or enums,
  bytes or text as GUIDs, `DateTime` as `DateTimeOffset` (UTC), and double → decimal through round-trip text.
- **Provider contract:**
  - `DataProvider` with `ProviderCapabilities`, `ITypeMapper`, `IErrorClassifier`, connection-string building per
    `NodeEndpoint` (the primary or a replica) and a `DbDataSource` per node.
  - `DataProviderRegistry` checks the contract version of each provider.
  - `SqlDialect` base with ANSI quoting, parameter names and LIKE escaping.
- **Database and raw SQL:**
  - `Database` (one per named connection) owns a pooled `DbDataSource` for the primary and each replica.
  - Raw SQL with `SqlQueryAsync<T>`, `ExecuteSqlAsync`, `SqlScalarAsync<T>` and `PingAsync`. Raw SQL is written as
    interpolated strings (`SqlText`): every hole becomes a typed parameter, and `Sql.Identifier(...)` holes become
    quoted identifiers. Values never reach the SQL text.
  - Failures and cancellation come back as `Result` errors, never exceptions.
- **Data sources:** `InitializingDataSource` is a base for data sources that need per-connection setup.
- **Command pipeline:** every command runs through one middleware chain (`ICommandMiddleware`, `CommandContext`,
  allocation-free `CommandNext<T>`). Application `IDataInterceptor`s run outermost: they can rewrite the SQL,
  refuse a command or observe its result. Caching, routing, resilience and telemetry plug into this chain.
- **Value converters:** one conversion layer for every boundary (reading rows, entity writes, raw SQL parameters,
  scalars).
  - `ValueConverter<TModel, TStore>` with a global `ValueConverters` registry and per-column `[Column(Converter = ...)]`.
  - Typed ids implement `IDataValue<TSelf, TStore>` (static abstract members, no reflection).
  - `EnumToStringConverter<TEnum>` stores enums by name.
  - `JsonValueConverter<T>` stores JSON columns through System.Text.Json source-gen metadata.
- **Background jobs:** `DataScheduler` runs CL.Data's background work on CL.Common's `JobScheduler`, with one
  clock (`TimeProvider`), no overlapping runs and failures reported per run. CL.Data now depends on CodeLogic.Common.
- **SQL tree:** statements are closed record trees (`SqlExpr`, `SqlStatement`: select with joins, grouping,
  HAVING, paging and locks; insert with RETURNING; update; limited delete; upsert).
  - `SqlWriter` renders them through dialect hooks with exhaustive switches, adding only the parentheses that
    precedence needs.
  - Limits and offsets are parameters, so the SQL text does not change with their values.
  - Literals accept only numbers and booleans.
- **LINQ translation** (`SqlTranslator`, strict). Anything that touches a column must translate or fail with
  `data.translation_failed`; values independent of the row are evaluated once and bound as parameters.
  - **Null handling:** comparisons with null (including captured nulls) become `IS NULL`.
  - **Booleans:** boolean columns used as conditions become `= TRUE`.
  - **Strings:** `Contains`/`StartsWith`/`EndsWith` become escaped LIKE patterns.
  - **Collections:** collection `Contains` becomes `IN`. This covers C# 14+ span-bound array calls, including the
    three-argument `MemoryExtensions.Contains` that nullable and enum arrays bind to. Long lists are split, and a
    null entry in the list matches NULL rows.
  - **Converters:** compared values go through the column's converter (enums stored as text bind their names).
  - **Functions:** arithmetic, string, math and date/time functions, plus `??` and `?:`.
- **SQL functions:** `SqlFn` holds query functions that work in `Where` too, not only in projections and updates.
  - Date parts, `BucketUtc`, `UtcNow`, `Coalesce`/`IfNull`, `Least`/`Greatest`, string functions, `Like`, and generic-math
    `Round`/`Floor`/`Ceiling`/`Abs`.
  - `SqlFn.Json.Value/Exists/Contains` and `SqlFn.FullText.Match` translate to neutral function nodes that each
    provider renders.
- **Dialect hooks:** conditions and boolean literals are rendered through dialect hooks (`RenderConditionLiteral`),
  `SqlPredicates.IsPredicate` is public, and dialects declare when an upsert needs a transaction
  (`UpsertRequiresTransaction`, `UpsertParameterMultiplier`).
- **Unified write path:** `WritePlanner` plans every entity write (insert, update, soft/hard delete, delete by key,
  upsert with overwrite, increments and ignore) in one place. It covers:
  - audit columns, tenant stamping and checks, and optimistic concurrency (integer tokens bumped after success;
    rowversion tokens returned);
  - server-generated identity (`RETURNING`/`OUTPUT`, or last-insert-id);
  - batching by the provider's parameter limit;
  - cache tags (`table:` and `entity:`).
- **Write context:** `IDataUserAccessor` and `IDataTenantAccessor` supply the current user and tenant.
- **Repositories:** `db.Repository<T>()`.
  - Writes: `InsertAsync`/`InsertManyAsync` write generated identities back, `UpdateAsync` (also for selected
    properties), `DeleteAsync` (soft), `HardDeleteAsync`, `DeleteByIdAsync`/`DeleteByKeyAsync`.
  - Reads: `GetByIdAsync`/`GetByKeyAsync` (composite keys), `GetAllAsync`, `FindAsync`, `CountAsync`, and
    `GetPagedAsync`, which counts only when asked and always sets `HasMore`.
  - Reads hide soft-deleted rows and other tenants' rows. A stale update fails with `data.concurrency`; a duplicate
    unique value fails with `data.duplicate_key`.
- **Write execution:** multi-statement upserts run in a serializable transaction. Generated values come from
  RETURNING/OUTPUT, or from the provider's last-insert-id. When the provider's RETURNING does not keep insert order,
  rows with generated keys are inserted one per statement.
- **Upserts and counters:** `UpsertAsync`/`UpsertManyAsync` (conflict on the key, a unique index or chosen properties),
  `UpsertWithIncrementsAsync`, `InsertIgnoreAsync` (`InsertOutcome.Inserted`/`Duplicate`) and `InsertManyIgnoreAsync`,
  plus atomic `IncrementAsync`/`DecrementAsync`/`AdjustAsync` (generic math). Each update also writes the audit
  columns and bumps the concurrency token.
- **Query builder:** `db.Query<T>()` is immutable and translates its lambdas at the terminal call.
  - Shaping: `Where`, `OrderBy`/`ThenBy` (and descending), `Take`/`Skip`, `Exclude`, `IncludeDeleted`.
  - Subqueries: correlated `WhereExists`/`WhereNotExists`, and `WhereIn`/`WhereNotIn` (the inner entity's filters apply).
  - Reads: `ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync`, `Max`/`Min`/`Sum`/`AverageAsync`.
    Aggregates over no rows return null, and sums return zero.
  - Set-based writes: `UpdateAsync(x => new T { ... })` (values may use the row's columns; audit columns are written and
    the token bumped) and `DeleteAsync` (soft unless `hard`).
- **Paging and streaming:** `ToPagedListAsync` (offset paging, count only on request), `ToCursorPageAsync` and
  `AsAsyncEnumerable`.
  - Cursor paging is keyset paging: the order is completed with the key, so ties are broken and the cost does not grow
    with depth. Cursors from a differently ordered query fail with `data.invalid_cursor`.
  - Streams hold one connection while you enumerate. A failure mid-stream throws `DataQueryException`, the one place
    CL.Data throws.
- **Projections:** `Select` projects into scalars, anonymous types, records and DTOs.
  - Projected values can be computed: arithmetic, string concatenation, comparisons, `?:` and `SqlFn` functions.
  - `Distinct()` removes duplicate results.
  - A projected column keeps its column converter, so enums stored as text come back as enums.
  - Rows are built with the constructor and setters named in the expression (no extra reflection; Native AOT safe).
- **Joins:** typed `Join`/`LeftJoin` over 2 to 5 tables, including self-joins and key-selector joins (composite keys
  through anonymous types).
  - Lambdas take one parameter per table.
  - `Where`/`OrderBy`/`ThenBy`/`Take`/`Skip`, `Select` projections, `CountAsync`, and `ToListAsync` returning
    entity tuples (null for an unmatched LEFT JOIN).
  - Joined entities' soft-delete and tenant filters go into their ON clause.
- **Grouping:** `GroupBy` takes a value, an anonymous type of columns, or a computed key.
  - `Having`, `OrderBy`/`ThenBy` over aggregates, `Take`/`Skip`, and `Select` into any projection.
  - Group lambdas receive `IDataGroup<TKey, T>`: `Key`, `Count`, `Count(predicate)`, `LongCount`, `CountDistinct`,
    `Sum`, `Average`, `Min`, `Max`, `Any`.
  - Sums of NULLs are zero, and integer averages are not truncated (SQL Server).
- **Transactions:** `BeginTransactionAsync` returns a `DataTransaction` whose `Db`, `Repository` and `Query` run
  inside it; disposing without commit rolls back. Also savepoints.
  - `InTransactionAsync` commits on success, rolls back on failure or exception, and retries after deadlocks and lock
    timeouts. Nested calls join the outer transaction.
- **Locking reads:** `ForUpdate`/`ForShare` with `LockWait.NoWait`/`SkipLocked` need a transaction.
- **`UpdateAndGetAsync`:** locks the matching keys, updates them and returns the updated rows (RETURNING where
  available). With `OrderBy`/`Take`/`SkipLocked` it is a safe job-queue claim.
- **Batches:** `db.Batch()` sends several queries in one round trip: `Add(query)`, `Count(query)`, `Sql<T>(...)`, then
  `ExecuteAsync` and each result's `Value`.
  - It uses `DbBatch` where the driver supports it, otherwise one multi-statement command with globally numbered
    parameters.
  - A planning error (such as a translation failure) runs nothing.
- **Fixes found on MySQL:**
  - Upserts qualify target columns (shared `QualifyColumns`), because the incoming-row alias has the same column names.
  - Savepoints fall back to standard `SAVEPOINT` SQL when a driver has no savepoint API.
- **Upsert fix (found on PostgreSQL):** `ON CONFLICT DO UPDATE` qualifies target columns, because PostgreSQL treats a
  bare name as ambiguous with `EXCLUDED`.
- **Grouping fix (found on SQL Server):** a condition used as a group key is grouped as a CASE value, because SQL
  Server cannot group by a predicate.
- **Breaking:** `SqlServerAuthentication.EntraPassword` was removed (deprecated by Microsoft).
- **Schema model:** `TableSchema` (columns, keys, indexes, foreign keys, options), `SchemaBuilder.FromMap` (the
  desired table per provider and naming policy, generated index and key names cut to the provider's identifier
  limit with a hash) and `SchemaBuilder.Fingerprint`.
  - `SchemaDiff.Compare` plans closed `SchemaChange`s by sync level. Safe: create, add, rename through
    `PreviousName`, widen, add indexes and foreign keys. Additive: also drop indexes and foreign keys. Full: also drop
    columns and narrow.
  - Indexes and foreign keys are matched by shape; indexes that back foreign keys are kept.
- **Provider hooks:** `NormalizeStoreType` and `TableOptions`; PostgreSQL normalizes its type aliases.
- **DDL generation:** `DdlGenerator` writes standard DDL for every `SchemaChange`; `DataProvider.Ddl` exposes it.
  - **Implicit defaults:** a NOT NULL column added without a default gets one, so existing rows stay valid.
  - **Covering columns:** where the provider has no `INCLUDE`, they are appended to the index key.
- **Schema introspection:** `SchemaIntrospector` (`ReadTableAsync`, `ListTablesAsync`), exposed as `DataProvider.Introspector`.
  - **Type normalization:** `StoreTypeNormalizer` converts the type names catalogs report into the names the type mappers write.
  - **New query method:** `Database.SqlReadAsync(sql, read)` builds each row of a raw query with your own reader function.
  - **Schema conformance:** generated DDL is run, the table is read back, and the diff against the model must be empty; changes apply with the rows kept.
- **Table sync:** `db.Schema.SyncAsync` (and `SyncAsync<T>`, `PlanAsync<T>`) creates and alters tables to match their entity models.
  - **Order:** tables referenced by foreign keys come first.
  - **Fingerprint skip:** a table whose fingerprint matches its row in `cl_data_schema` is skipped. `Force` inspects every table.
  - **Drift:** changes the sync level does not allow are reported as drift (`SchemaDriftDetectedEvent`) and are not recorded, so the next run looks again.
  - **One transaction per table** where DDL is transactional. On SQLite, foreign keys are off during the change and checked before commit, so a rebuild never cascades.
  - **One process at a time:** the run holds an advisory lock.
- **Advisory locks:** `db.AcquireLockAsync(name, timeout)` returns a `DataLock`, held on its own connection. It uses GET_LOCK on MySQL, pg_try_advisory_lock on PostgreSQL and sp_getapplock on SQL Server; SQLite returns `data.unsupported`.
- **Events:** `DatabaseOptions.Events` receives CL.Data events.
- **Backup before alter:** moved to WP-75, together with backup and restore.
- **Migrations:** `IMigration`/`Migration` with `MigrationVersion` (semantic app version plus order, written `1.2.0/003`).
  - **Context:** `IMigrationContext` gives a `Db` bound to the migration's transaction, plus `ExecuteAsync`, `ApplyAsync(SchemaChange)`, `ReadTableAsync` and `SyncTableAsync<T>`.
  - **Runner:** `db.Migrations(...)` creates a `MigrationRunner` with `PendingAsync`, `AppliedAsync`, `MigrateAsync` and `RollbackAsync(target)`.
  - **History:** each migration commits in one transaction with its row in `cl_data_migrations`.
  - **Checksums** (XxHash128 of version and description) detect migrations edited after they were applied.
  - **Safety:** `AppVersion` filters which migrations run; a rollback that includes an irreversible migration is refused before anything changes; runs take an advisory lock.
  - **Discovery:** `MigrationRunner.FromAssembly` finds migrations by reflection (annotated for trimming).
- **`DataTransaction`:** can run session setup and teardown statements around the transaction (SQLite foreign keys).
- **Administration (`db.Admin`):**
  - **Databases:** `DatabaseName`, `DatabaseExistsAsync`, `EnsureDatabaseAsync`, `CreateDatabaseAsync` and `DropDatabaseAsync` (the name is always explicit).
  - **Tables:** `ListTablesAsync`, `TableExistsAsync`, `DropTableAsync` and `TruncateAsync`, each by name or entity type.
  - **Provider hook:** `DatabaseAdmin`; `ServerDatabaseAdmin` connects to the server-level database for the server providers.
- **Retention (`db.Retention`):** `PurgeAsync<T>` deletes rows past their `[RetainDays]` limit in batches of `BatchSize` and publishes `RetentionPurgedEvent`. `Schedule(scheduler, maps, every)` runs it on `DataScheduler`.
- **Dialect hooks:** `RenderTruncate` and `RenderBatchedDelete`.
- **Backups (`db.Backup`):** `BackupAsync(directory, tables?, schema?)` writes a portable logical backup.
  - **Contents:** `manifest.json` holds the table definitions in dependency order, and each table's rows go to a typed JSON-lines file, streamed with sequential access. Values whose type differs from their column's (SQLite) are tagged inline.
  - **Restore:** `RestoreAsync(directory)` recreates the tables on the same provider and reinserts the rows in multi-row chunks, one transaction per table, keeping identity values.
  - **Snapshot:** `SnapshotAsync(file)` makes a native whole-file copy (SQLite `VACUUM INTO`).
- **Backup before alter:** with `BackupDirectory` set, table sync backs up a table before dropping or altering its columns. If the backup fails, nothing is changed.
- **Provider hooks:** `DdlGenerator.IdentityInsertSetup`/`IdentityInsertTeardown`; `DataProvider.SnapshotSql`.
- **Query plan cache (`QueryPlanCache`):** a lambda whose shape was translated before is not translated again.
  - **How it works:** its captured values are read again and bound into the cached SQL tree.
  - **Variants:** shapes that depend on a captured value (a null that becomes `IS NULL`, the length of an `IN` list, a null `LIKE` argument, nulls in a params array) keep one plan per variant.
  - **Constants:** literals written in code are part of the shape. Values computed at call time (`DateTimeOffset.UtcNow`) are computed again on every use.
  - **Not cached:** grouped-query lambdas and unusual expression nodes are translated every time.
  - **Settings and stats:** `Enabled`, `Capacity`, `MaxVariantsPerShape`, `Hits`, `Misses`, `Count`, `Clear()`.
  - **Effect on a typical predicate:** about 20% less translation time and 56% fewer allocations.
- **Source generator:** every `[Table]` / `[DataRow]` class gets a generated `IEntityAccessor<T>`, registered by a module initializer.
  - **Map:** built on first use, from the same attribute instances reflection reads.
  - **Typed access:** materialization uses `GeneratedMapping.Read<T>`. Columns that aren't selected keep their property initializers.
  - **Constructors and members:** positional records bind through the constructor, `required` members are set in the object initializer, and init-only setters go through `UnsafeAccessor` (no reflection).
  - **Writes:** column writes are typed and don't allocate.
  - **Fallback:** generic, abstract, private nested and file-local types stay on reflection mapping.
- **Shared mapping logic:** `GeneratedMapping.Column`/`Entity` hold the column and entity rules; reflection mapping uses them too, so both describe a type identically. A parity test compares them for every generated type.
- **Performance (in-memory reader):** about 1.9x faster than reflection when materializing; parameter writes allocate nothing (reflection boxed every value).
- **Mapping analyzer:** mapping mistakes in `[Table]`/`[DataRow]` types are build errors, reported at the offending member, instead of `InvalidOperationException` at first use.
  - **CLD002** constructor cannot bind
  - **CLD003** bad converter
  - **CLD004** nullable key
  - **CLD005** composite key without (or with repeated) KeyOrder
  - **CLD006** column type doesn't fit its role
  - **CLD007** role used twice
  - **CLD008** unknown property in an index
  - **CLD009** duplicate or unsafe name
  - **CLD010** SET NULL on a non-nullable column
  - **CLD011** table maps no columns
  - **CLD001** (warning): the type cannot use generated mapping, so it falls back to reflection, which Native AOT doesn't support.
- **Native AOT:** the `samples/Data.AotSmoke` app does CRUD, LINQ queries (expression trees run interpreted), plan-cached repeats, transactions, schema sync and raw SQL on SQLite as a 6.6 MB native executable. The publish has no trim/AOT warnings from CL.Data, CL.Common or CodeLogic, and CI (`data-aot`) publishes and runs it.
  - **MySqlConnector and Npgsql:** add no warnings either.
- **Changed-column updates:** these write only the columns that changed, plus audit columns and the concurrency token, and send nothing when nothing changed.
  - **`Repository.UpdateAsync(entity, change)`:** applies your change, then writes the difference.
  - **`UpdateAsync(entity, snapshot)`:** writes what changed since `Repository.Snapshot(entity)`.
  - **`UpdateChangedAsync(original, modified)`:** for detached copies.
  - **Accessors:** both kinds implement `IEntityAccessor<T>.Snapshot`. Generated snapshots keep typed fields and compare without boxing.
  - **In-place changes:** byte arrays are copied and converter columns are compared in stored form, so they are detected.
  - **Effect:** a concurrent change to another column is no longer overwritten.
- **Breaking:** `IEntityAccessor<T>` gains `Snapshot`.
- **Compiled queries:** `CompiledQuery.For<T, TArgs>((q, args) => q.Where(p => p.Id == args)...)` plans a query once per database (and tenant) and replays it.
  - **Per call:** only the arguments are bound. Building expression trees, translating and rendering SQL are skipped.
  - **Terminals:** `ToListAsync`, `FirstOrDefaultAsync`, `CountAsync` and `AnyAsync`.
  - **Variants:** shapes that depend on an argument (null checks, `IN` list lengths) get their own variant.
  - **Restriction:** non-lambda arguments in the chain (`Take(10)`) must not depend on the arguments.
  - **Measured on in-memory SQLite (by id):** 41.7 → 30.4 µs per call and 8.5 → 4.2 KB allocated.
- **Query lambda analyzer:** `QueryBuilder` predicate and ordering lambdas that can't be translated are reported at build time instead of as `data.translation_failed` at runtime.
  - **CLD101** unsupported method, or `Contains` on a collection that depends on the row
  - **CLD102** unsupported member on a row value
  - **CLD103** unmapped or `[Ignore]` property
  - **Severity:** warnings (raise them to errors with `.editorconfig`). The checks mirror the translator's supported surface; parts that don't read the row are never flagged.
- **Entity save hooks:** `IDataInterceptor.OnSavingAsync`/`OnSavedAsync` with `EntitySaveContext` (kind, entities, changed properties) run around inserts, updates, upserts and deletes.
  - **Before planning:** `OnSavingAsync` runs first, so changes it makes to the entities are saved, and returning an error cancels the save.
  - **Cost:** nothing extra without interceptors.
- **Auditing conformance:** `[CreatedAt]`/`[UpdatedAt]` (from `DatabaseOptions.Time`) and `[CreatedBy]`/`[UpdatedBy]` (from `IDataUserAccessor`) are verified on insert, update, both upsert paths, set-based updates and counters. An update never writes the created columns.
- **Global filters:** `DataFilter.For<T>(predicate, name)` in `DatabaseOptions.Filters` adds standing conditions to every read of the targeted entities.
  - **Targets:** `T` can be an entity, a base class or an interface; one filter covers every mapped type that implements it.
  - **Where they apply:** queries, key lookups, joins, subqueries, set-based updates and deletes, and compiled queries. Entity writes by key aren't filtered, and `UpdateAndGet` returns the rows it updated even if they no longer match.
  - **`QueryBuilder.IgnoreFilters()`** lifts all user filters, or only the named ones. The tenant filter always applies.
- **`Database.ForTenant(tenant)`:** a cheap view scoped to one tenant (filters and stamps `[TenantKey]` entities). It shares the connection pools; disposing the view does nothing.
- **Seeding:** `IDataSeed`/`DataSeed` (id, order, version) and `DataSeed.Rows<T>(id, rows)`, an idempotent upsert seed.
  - **Runner:** `db.Seeds(...).RunAsync()` runs seeds that are new or have a higher version than recorded, in order. Each seed commits in one transaction with its row in `cl_data_seeds`, under an advisory lock.
  - **Failures:** a failed seed rolls back and is retried on the next run.
- **Internal:** schema sync, migrations and seeds share one helper for their bookkeeping tables and locks.
- **Result cache:** `DataCache` (shareable, `DatabaseOptions.Cache`), built on CL.Common's `MemoryCache`.
  - **Opting in:** `QueryBuilder.WithCache(policy | ttl)` caches lists, first-or-default, count/any and aggregates, plus projections, groups and joins built from that query. `DataCacheConfig.EntityCache` caches repository key lookups.
  - **Fresh objects:** results are stored as raw rows and materialized fresh on every hit, so callers can't change the cache.
  - **Invalidation:** every write through CL.Data drops the results of the tables it touches, including the tables a join or subquery reads. Raw `ExecuteSqlAsync` drops the connection's entries.
  - **Transactions:** reads inside a transaction bypass the cache, and invalidations wait for commit; a rollback drops nothing.
  - **Concurrency:** concurrent misses run once, and a write during a load keeps the stale result out of the cache.
  - **Measured on MySQL:** a key lookup takes 1.66 µs from cache against 1.4 ms from the database; a 100-row filter takes 18 µs against 1.6 ms.
- **Warm queries:** set `CachePolicy.Refresh` to keep a result warm.
  - The cache re-reads it in the background every period, so hits never wait for the database. It is dropped after `IdleRefreshes` periods without a hit.
  - Start the loop with `DataCache.StartRefreshing(scheduler)`, or call `RefreshDueAsync` from your own scheduler.
- **Cross-node invalidation:** `ICacheInvalidationBus` plus `DataCache.UseBus(bus)`. Local invalidations are published, and remote ones drop matching results without being re-published.
  - `InProcessCacheBus` links caches inside one process.
  - `DatabaseCacheBus` polls a shared `cl_data_cache_bus` table: no extra infrastructure, old rows are pruned, and polling starts with `Start(scheduler)`.
  - A failed publish leaves the other nodes on their TTL; the local write is unaffected.
- **Resilience (WP-90):** an innermost pipeline step, built on CL.Common `RetryPolicy` and `CircuitBreaker`, configured by `RetryCount`, `RetryBaseDelayMs`, `CircuitBreakerThreshold` and `CircuitBreakSeconds`.
  - **Retries:** reads retry transient failures with backoff and jitter. Writes retry only when the statement certainly didn't take effect: the connection couldn't be opened, or the server rolled it back (deadlock, lock timeout). A failed commit is never retried.
  - **Transactions:** nothing retries inside a user transaction.
  - **Errors:** after the last attempt the original `data.*` error is returned unchanged.
  - **Circuit breaker:** each node (`DataNode.Breaker`, `IsHealthy`) has one, and it counts only node failures (unreachable, connection lost). While it is open, commands fail fast with `data.circuit_open` until a trial call succeeds.
- **Read routing (WP-91):** on connections with replicas, reads go to a healthy replica, chosen at random by weight.
  - **Skipped replicas:** those whose circuit is open, and those lagging beyond `MaxReplicaLagMs` (`DataNode.LagMs`).
  - **Always the primary:** writes and transactions.
  - **Read preference:** set per connection (`ReadPreference`), per view (`db.ReadFrom(preference)`) or per query (`QueryBuilder.ReadFrom`).
  - **Reads-your-writes:** for `StickyAfterWriteMs` after a write, reads stay on the primary.
  - **No usable replica:** `PreferReplica` falls back to the primary and `ReplicaOnly` fails with `data.no_healthy_node`. A read whose replica turns out to be down runs again on the primary.
- **Replica lag probes (WP-92):** `DataProvider.ReplicaProbe` measures lag on each server provider. SQLite has none.
  - **MySQL:** `SHOW REPLICA STATUS`.
  - **PostgreSQL:** replay LSN and timestamp.
  - **SQL Server:** `sys.dm_hadr_database_replica_states`.
- **Running probes:** `db.ProbeReplicasAsync()`, or `db.StartReplicaProbes(scheduler)` to run them every `ReplicaProbeIntervalSeconds`.
  - Each probe updates `DataNode.LagMs` and feeds the replica's circuit breaker, so a recovered replica gets back into rotation through the probe.
  - When replication is stopped, the probe reports `ReplicaProbe.Broken`, which keeps that replica out of rotation.
- **Node health (WP-94):** `db.CheckHealthAsync()` pings every node, skipping routing and retries. For each node it reports reachability, circuit state, lag, latency and any error (`NodeHealth.IsUsable`).
  - Primary failover through the drivers was already mapped: MySQL failover hosts, Npgsql `TargetSessionAttributes=primary`, and SQL Server `MultiSubnetFailover` and `FailoverPartner` (primary only, now tested).
- **Live replica tests (WP-93):** `tests/replicas/docker-compose.yml` starts a MySQL 8.4 GTID pair and a PostgreSQL 18 streaming pair.
  - `LiveReplicaSuite` covers replica reads seeing primary writes, live lag, skipping a paused replica until it catches up, and falling back to the primary when the replica is down.
  - It runs with `CL_DATA_REPLICAS=1`, in the non-required CI job `data-replicas`.
- **Telemetry (WP-A0):** `DataTelemetry` exposes `ActivitySource` and `Meter` "CL.Data", following the OpenTelemetry database conventions.
  - **Spans:** one client span per logical command (retries included), tagged `db.system.name`, `db.namespace`, `db.operation.name`, `db.collection.name`, parameterized `db.query.text`, `server.address`, returned rows, `error.type` and attempts.
  - **Metrics:** `db.client.operation.duration`, `cl.data.rows`, `cl.data.retries`, `cl.data.cache.hits` and `cl.data.cache.misses`, `cl.data.circuit.opened`, and the gauge `cl.data.replica.lag`.
  - **Cost:** nothing is built without a listener.
- **Diagnostics:**
  - `SlowQueryEvent` fires above `SlowQueryThresholdMs`. With `CaptureExplainOnSlowQuery`, the plan is captured in the background by each provider's `QueryExplainer`: MySQL `EXPLAIN FORMAT=TREE`, PostgreSQL `EXPLAIN`, SQLite `EXPLAIN QUERY PLAN`, SQL Server `SHOWPLAN_TEXT`.
  - `NPlusOneDetectedEvent` fires once per one-second window.
  - `QueryExecutedEvent` is opt-in (`PublishQueryEvents`).
  - `CircuitStateChangedEvent` is published on every breaker transition.
- **Bulk copy (WP-A1):** `db.Bulk<T>().InsertAsync(IEnumerable<T> | IAsyncEnumerable<T>, BulkOptions)` returns a `BulkCopyReceipt`.
  - **Native paths:** MySqlBulkCopy, SqlBulkCopy, PostgreSQL `COPY BINARY` with each column's real type, and prepared inserts in one transaction on SQLite. Without a native path, rows are batched into multi-row inserts.
  - **Same rows as an insert:** values are stamped and converted exactly as an insert does.
  - **Large input:** input is streamed in chunks.
  - **Transactions and cache:** a copy joins a transaction view and drops the table's cached results.
  - **`InsertManyAsync`** switches to bulk at `BulkThreshold` when nothing has to be read back.
- **Faster write path:** placeholder names are cached per dialect, and the SQL writer is reused on each thread. Inserting 100 rows went from 5.90 ms / 298 KB to 5.51 ms / 226 KB, which beats CL.MySQL2 4.9.
- **Performance budgets (WP-A4):** `benchmarks/budgets.json` sets limits per benchmark: allocations per operation, and time as a ratio to the category's Dapper or ADO.NET baseline.
  - `Data.Benchmarks --check-budgets` runs a short BenchmarkDotNet pass and exits 1 when a benchmark is over budget.
  - It runs in the CI job `data-perf` (MySQL, not required yet).
- **Hosting (WP-A2):** `DataLibrary` (`ILibrary`, `IConfigReloadable`) owns the `data` and `data.cache` config sections and provides `Db(name)`.
  - **Start:** it warms each node's pool up to `MinPoolSize` and starts replica probes, warm-query refresh and the cache invalidation bus (`None`, `Events` through the new `EventBusCacheBus`, or `Database`), all on one `DataScheduler`.
  - **Events:** CL.Data events are forwarded to the CodeLogic event bus under their concrete types, and slow queries, N+1 patterns, drift and circuit changes are logged.
  - **Live reload:** applied per connection.
  - **Health:** checked per node, with `NodeHealthChangedEvent` published on each change.
  - **Provider libraries:** they now declare `Dependencies = [Required("CL.Data")]`.
- **Provider extensions (WP-A3):** C# extension members expose provider-only operations. Each fails with `data.unsupported` on another provider.
  - `db.MySql`: `OptimizeTableAsync<T>` and `AnalyzeTableAsync<T>`, which return the per-table messages.
  - `db.Postgres`: `NotifyAsync`, `ListenAsync` (on a dedicated connection, until disposed) and `VacuumAnalyzeAsync<T>`.
  - `db.SqlServer`: `UpdateStatisticsAsync<T>(fullScan)`.
  - `DataAdmin.QuotedTable<T>()` is now public for provider-specific statements.
  - Maintenance runs as a read, so cached results stay valid.
