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
