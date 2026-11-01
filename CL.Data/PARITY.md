# CL.Data parity checklist

This is the finished parity sweep (WP-B0, 2026-10-31). It maps every feature of the 4.x database libraries to CL.Data:
- **Libraries covered:** CL.MySQL2, CL.MSSQL, CL.PostgreSQL and CL.SQLite.
- **Sources:** their public APIs and their test suites, read from git history (`ApiSurfaceTests`, `LiveServiceSurfaceTests`, the `ParityFix`, `BulkUpdateKeySafety`, `ContainsTranslation` and `SpanContainsTranslation` suites, and the others).
- **Status values:** each feature is **done** (with the conformance suite that covers it on all four providers), **dropped on purpose** (with the replacement), or **partial** (with what's missing).

The 4.x code remains on `support/4.x`, and CodeLogic.MySQL2 4.9 stays on nuget.org. The WP-3B benchmarks compare against it.

## Shared feature set (CL.MySQL2 = CL.MSSQL = CL.PostgreSQL; CL.SQLite a subset)

| 4.x feature | CL.Data | Status / covered by |
|---|---|---|
| Repository: Insert/InsertMany (identity write-back), Update, Delete (soft)/HardDelete, GetById, GetAll, GetPaged, Count, Find | `db.Repository<T>()` | done: `RepositoryConformance` |
| Composite keys (`GetByKeysAsync`, `DeleteByKeysAsync`) | `GetByKeyAsync` / `DeleteByKeyAsync(EntityKey)` | done: `RepositoryConformance` |
| Upsert, UpsertMany, UpsertWithIncrements, expression upsert | `UpsertAsync` (options), `UpsertWithIncrementsAsync`; set-based `UpdateAsync` | done: `UpsertConformance` |
| InsertIgnore / InsertManyIgnore (`InsertOutcome`) | same names | done: `UpsertConformance` |
| Increment / Decrement / Adjust | same names (generic math) | done: `RepositoryConformance` |
| `GetByColumnAsync(string, value)` | `FindAsync(x => x.Col == value)` | dropped on purpose (no string-based APIs) |
| QueryBuilder: Where, WhereExists/NotExists, WhereIn/NotIn, IncludeDeleted, OrderBy, Exclude, Take/Skip | `db.Query<T>()` (plus ThenBy) | done: `QueryConformance` |
| `After` / cursor pagination, `ToPagedList`, aggregates, Any, Count | `ToCursorPageAsync`, `ToPagedListAsync` (COUNT only on request), aggregates | done: `PagingConformance`, `QueryConformance` |
| Aggregates over empty sets | `Max`/`Min`/`Average` return `null`; `Sum` returns 0; `Count` returns 0 | done: `QueryConformance` |
| `Contains` over arrays and lists, C# 14 span `Contains` (enum and nullable arrays) | translator: 2- and 3-argument `MemoryExtensions.Contains` | done: `PredicateTranslationTests`, `PlanCacheTests` |
| Typed joins (2 tables) | joins over 2–5 tables, left/right | done: `JoinConformance` |
| Raw-string `Join(table, condition)` | typed joins | dropped on purpose |
| `Select` projections | computed projections | done: `ProjectionConformance` |
| `GroupBy` + grouped Select | `GroupBy` + `Having` + `IDataGroup` | done: `GroupingConformance` |
| ForUpdate / ForShare (Wait/NoWait/SkipLocked), UpdateAndGetAsync | same | done: `TransactionConformance`, `QueryConformance` |
| Set-based update refusing key columns (`BulkUpdateKeySafety`) | `UpdateAsync` rejects key and tenant columns (`data.validation`) | done: `QueryConformance` (`Set_based_updates_refuse_key_columns`) |
| `UpdateAsync(Dictionary)` | member-initializer `UpdateAsync` | dropped on purpose |
| SqlFn in projections and updates | `SqlFn` (also in `Where`), JSON and full-text functions | done: `ProjectionConformance`, `QueryConformance` |
| Raw SQL: `SqlQueryAsync`, `ExecuteSqlAsync`, `SqlScalarAsync` | same, with parameterized interpolation | done: `RawSqlTests` and the conformance suites |
| `WithConnection` (raw driver connection) | `db.WithConnectionAsync` (joins a transaction view) | done: `ServerConformance` |
| `GetServerInfoAsync` / `TestConnectionAsync` | `db.Admin.ServerVersionAsync`, `PingAsync`, `CheckHealthAsync` | done: `ServerConformance`, `RoutingTests` |
| `GetOpenConnectionCount` / `GetAllConnectionCounts` | drivers' own pool metrics (MySqlConnector, Npgsql, SqlClient meters) | dropped on purpose |
| Transactions (`BeginTransactionAsync`, `TransactionScope`) | `DataTransaction`, `InTransactionAsync(retryOnDeadlock)`, savepoints | done: `TransactionConformance` |
| `DistinguishDuplicateKeyError` option | always `data.duplicate_key` | dropped (always on) |
| Multiple named connections, `GetConnectionIds`, `HasConfiguration`/`GetConfiguration` | `DataLibrary.Db(name)`, `Connections`, `Db(name).Config` | done: `DataLibraryTests` |
| Live config reload | `DataLibrary` per-connection reload; a cache change rebuilds | done: `DataLibraryTests` |
| Health checks | per-node `CheckHealthAsync`; library health; `NodeHealthChangedEvent` | done: `DataLibraryTests`, `RoutingTests` |
| Localization of log messages | `DataStrings`, registered by `DataLibrary` | done |
| Transient retry with backoff | resilience step (retries, a circuit breaker per node) | done: `ResilienceTests` |
| Query cache (`WithCache`), SmartCache pools, cache coordinator | `DataCache`: `WithCache`, entity cache, warm queries (`Refresh`), invalidation bus | done: `CacheConformance` |
| Observability: query and slow-query events, EXPLAIN on slow queries, N+1 detector | telemetry (OpenTelemetry spans and metrics, `SlowQueryEvent` with a plan, `NPlusOneDetectedEvent`) | done: `TelemetryTests`, `TelemetryConformance` |
| Table sync (`SyncTableAsync`, `SyncSchemaAsync`, namespace sync, sync modes, CRC state, schema lock, backup before alter) | `db.Schema.SyncAsync<T>()` / `SyncAsync(maps)`, `SchemaSyncLevel`, `cl_data_schema`, advisory locks on MySQL, PostgreSQL and SQL Server (SQLite has none: run sync from one process) | done: `SchemaConformance`, `SyncConformance` |
| Introspection (`GetDatabaseColumnsAsync`, `SchemaAnalyzer`) | `SchemaIntrospector` ×4 | done: `SchemaConformance` |
| Migrations (`IMigration`, `RegisterMigrationsFrom`, runner, tracker, `GetPendingMigrationsAsync`, rollback) | `MigrationRunner` (`FromAssembly`, `PendingAsync`, `AppliedAsync`, `MigrateAsync`, `RollbackAsync`) | done: `MigrationConformance` |
| BackupManager (DDL backup, restore) | `db.Backup` (plus `VACUUM INTO` snapshots on SQLite) | done: `BackupConformance` |
| DatabaseAdmin (create/drop/exists for databases and tables, truncate), `CreateDatabaseIfMissing` | `db.Admin` | done: `AdminConformance` |
| RetentionWorker (`[RetainDays]`) | `DataRetention.PurgeAsync` / `Schedule(scheduler, maps, every)` | done: `AdminConformance` |
| Bulk insert | `db.Bulk<T>()` (native ×4, batched fallback); `InsertManyAsync` switches automatically | done: `BulkConformance` |
| Seeding | `IDataSeed` / `db.Seeds` | done (new): `SeedConformance` |

## Provider-specific

| 4.x feature | CL.Data | Status |
|---|---|---|
| **MySQL:** charset, engine and collation per table and column | `[MySqlTable]` (engine, charset, collation, row format) / `[MySqlColumn]` (collation) hints, used by the DDL | **partial**: a column charset without a collation (`[MySqlColumn(Charset)]`) is not applied; set the collation |
| **MySQL:** `GET_LOCK` schema lock, `information_schema` introspection | advisory lock, introspector | done |
| **MySQL:** OPTIMIZE / ANALYZE TABLE | `db.MySql.OptimizeTableAsync` / `AnalyzeTableAsync` | done (new): `MySqlExtensionTests` |
| **PostgreSQL:** `DefaultSchema` scoping | `PostgreSql.DefaultSchema`; tables are schema-qualified | done |
| **PostgreSQL:** `ON CONFLICT` upserts, `RETURNING` identities | dialect and write planner | done |
| **PostgreSQL:** jsonb, uuid, timestamptz, bytea | type mapper | done |
| **PostgreSQL:** inet, interval, arrays, ranges | `[PostgresColumn(Type = ...)]` hints; values bound as-is | **partial**: no LINQ translation of array or range operators (planned for 5.1) |
| **PostgreSQL:** `pg_advisory_lock`, `pg_catalog` introspection | advisory lock, introspector | done |
| **PostgreSQL:** LISTEN / NOTIFY | `db.Postgres.ListenAsync` / `NotifyAsync` | done (new): `PostgreSqlExtensionTests` |
| **PostgreSQL:** runtime options | `PostgreSqlOptions` (auto-prepare, multiplexing) | done |
| **PostgreSQL:** unlogged tables, fill factor, tablespace | `[PostgresTable]` hints, applied when a table is created | done (new): `DdlGoldenTests` |
| **SQL Server:** SQL login, integrated and Azure AD authentication | `SqlServer.Authentication` | done (Entra password dropped: deprecated upstream) |
| **SQL Server:** `TOP` / `OFFSET FETCH`, table-hint locks, `OUTPUT INSERTED`, serializable upsert | dialect | done |
| **SQL Server:** 2100-parameter chunking | `ParameterLimit` 2098 in batch planning | done |
| **SQL Server:** JSON as `nvarchar(max)` with an `ISJSON` check, `rowversion`, `xml`, `geography` | type mapping; `CHECK (ISJSON(col) = 1)` in the DDL | done: `SqlServerJsonCheckTests` |
| **SQL Server:** `sp_getapplock`, `sys.*` introspection | advisory lock, introspector | done |
| **SQL Server:** file group, data compression, sparse columns | `[SqlServerTable]` hints, applied when a table is created | **partial**: `[SqlServerColumn(Sparse)]` is not applied |
| **SQL Server:** explicit identity inserts | `IDENTITY_INSERT` handling, reset on failure | done: `RepositoryConformance` |
| **SQLite:** pool, WAL, foreign keys, busy timeout, PRAGMAs | `SqliteDataSource` (PRAGMAs per connection) | done |
| **SQLite:** `CacheMode` (shared/private) | shared cache for `:memory:`; otherwise the driver default | **partial**: no setting for file databases (rarely useful with WAL) |

## Behaviour differences (for the 4.x → 5.x port guide)

- **No string-based APIs:**
  - `GetByColumn(string)`, the raw-string `Join` and `UpdateAsync(Dictionary)` are gone.
  - An untranslatable lambda is a build warning (CLD1xx) and a `data.translation_failed` error at run time, never evaluated on the client.
- **Paging:** paged queries run `COUNT` only when asked (`withTotal: true`). Keyset paging (`ToCursorPageAsync`) is the documented default.
- **Dates:** `DateTimeOffset` and UTC are the default. On SQLite, `DateTime` and `DateTimeOffset` are stored as UTC ISO-8601 text.
- **SQLite storage:** decimals use NUMERIC affinity, and GUIDs are stored as 16-byte BLOBs.
- **MySQL row counts:** CL.Data connects with `UseAffectedRows`, so MySQL counts rows *changed*, not rows matched. Entity saves are unaffected: an unchanged save is confirmed with a match check. But set-based `UpdateAsync` returns only the rows whose values changed, and `AdjustAsync(..., 0)` returns false.
- **Schema sync reports drift as success:** `SyncAsync` returns a successful `SchemaSyncReport` when changes were held back by the sync level. Check `report.HasDrift` (or listen for `SchemaDriftDetectedEvent`); CL.MySQL2 callers that only checked for failure miss it.
- **Schema DDL differs from CL.MySQL2:** time types are `DATETIME(6)` unless `[Column(Precision = 0)]`; `DataType.Text` is `LONGTEXT`; a non-nullable `string` is `NOT NULL` (CL.MySQL2 made strings NULL unless `NotNull`). Existing databases need a migration: generate it with `db.Schema.ScriptAsync(maps)` against a copy and review it.
- **Retention is not automatic:** `[RetainDays]` moves to the property, and purges run only through `db.Retention.PurgeAsync`/`Schedule` (CL.MySQL2 ran a 24-hour worker).
- **Set-based writes refuse what they cannot honour:** `DeleteAsync` with `Skip`, a limited soft delete, and `UpdateAsync` with `Take`/`Skip` fail with `data.validation`.
- **Config, state tables and errors:**
  - Config moves to the `data` and `data.cache` sections.
  - State tables are renamed: `cl_data_schema`, `cl_data_migrations`, `cl_data_seeds` and `cl_data_cache_bus`.
  - Error codes are `data.*`.
- **Retries are on by default** (`RetryCount = 3`). Writes are retried only when they certainly didn't apply.
- **Bulk copy doesn't read back server-generated identities.** `InsertManyAsync` uses bulk copy only when nothing has to be read back.
