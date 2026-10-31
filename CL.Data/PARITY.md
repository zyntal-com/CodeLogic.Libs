# CL.Data parity checklist

This file tracks the features of the 4.x database libraries (CL.MySQL2, CL.MSSQL, CL.PostgreSQL, CL.SQLite) that
CL.Data must cover before the 5.0 release. Each old library is removed from `main` once CL.Data covers its provider.
Anything still open is listed here, with the work package that closes it. The 4.x code remains on `support/4.x` and in
git history.

## CL.SQLite (removed 2026-10-31, after WP-38)

| CL.SQLite feature | CL.Data | Status |
|---|---|---|
| `GetRepository<T>()`: Insert, Update, Delete, GetById, GetAll, Find, Count, GetPaged | `db.Repository<T>()` | done |
| `GetByKeysAsync` / `DeleteByKeysAsync` (composite keys) | `GetByKeyAsync` / `DeleteByKeyAsync(EntityKey)` | done |
| `UpsertAsync` | `UpsertAsync` / `UpsertManyAsync` | done |
| `UpdateAndGetAsync` | `QueryBuilder.UpdateAndGetAsync` | done |
| Query builder: Where, OrderBy/ThenBy, Take/Skip, Any, Count, FirstOrDefault, ToPagedList | `db.Query<T>()` | done |
| `RawQueryAsync` / `RawExecuteAsync` | `SqlQueryAsync` / `ExecuteSqlAsync` (parameterized interpolation) | done |
| Connection pool, WAL, foreign keys, busy timeout | `SqliteDataSource` (driver pool, PRAGMAs per connection) | done |
| `CacheMode` (shared/private) | Shared cache for `:memory:`; otherwise the driver default | partly; reopen if needed |
| Live config reload | `DataLibrary` reload (WP-A2) | open, WP-A2 |
| Health check | `Database.PingAsync`; library health (WP-A2) | open, WP-A2 |
| `SyncTablesAsync` / `SyncNamespaceAsync` (table sync) | Table sync (WP-73) | **open, WP-73** |
| `GetDatabaseColumnsAsync` (introspection) | SQLite introspector (WP-71) | **open, WP-71** |
| Migration ledger (`RecordMigration`, `GetAppliedMigrations`, `HasMigrationBeenApplied`, `RemoveMigrationRecord`) | Migrations (WP-74) | **open, WP-74** |
| `CreateDatabaseAsync` / `DropDatabaseAsync` / `DatabaseExistsAsync` / `TableExistsAsync` (file-based) | Admin (WP-75) | **open, WP-75** |
| Observability (`SQLiteObservability`, slow-query log) | Telemetry middleware (WP-A0) | **open, WP-A0** |

**Behavior differences to document in the port guide:**
- Text values are compared case-insensitively by SQLite's `LIKE` for ASCII, as before.
- `DateTimeOffset`/`DateTime` values are stored as UTC ISO-8601 text.
- Decimals use NUMERIC affinity.
- GUIDs are stored as 16-byte BLOBs.

## CL.MySQL2 (removed 2026-10-31, after WP-50)

The CodeLogic.MySQL2 4.9 package stays on nuget.org and on `support/4.x`. The WP-3B benchmark compares against it.

| CL.MySQL2 feature | CL.Data | Status |
|---|---|---|
| Repository: Insert/InsertMany (identity write-back), Update, Delete (soft)/HardDelete, GetById, GetAll, GetPaged, Count, Find | `Repository<T>` | done |
| Upsert, UpsertMany, UpsertWithIncrements, expression upsert | `UpsertAsync` (options), `UpsertWithIncrementsAsync`; set-based `UpdateAsync` covers expression updates | done |
| InsertIgnore / InsertManyIgnore (`InsertOutcome`) | same names | done |
| Increment / Decrement / Adjust | same names (generic math) | done |
| `GetByColumnAsync(string, value)` | `FindAsync(x => x.Col == value)` (string-based API dropped on purpose) | dropped |
| QueryBuilder: Where, WhereExists/NotExists, WhereIn/NotIn, IncludeDeleted, OrderBy, Exclude, Take/Skip, After | `db.Query<T>()` (ThenBy added; `After` is `ToCursorPageAsync`) | done |
| Typed joins (2 tables), raw-string `Join(table, condition)` | joins over 2-5 tables; raw-string join dropped on purpose | done / dropped |
| `Select` projections (columns only) | computed projections | done |
| `GroupBy` + grouped Select (no HAVING) | `GroupBy` + `Having` + `IDataGroup` | done |
| ForUpdate / ForShare (Wait/NoWait/SkipLocked), UpdateAndGetAsync | same | done |
| ToPagedList, ToCursorPagedList, aggregates, Any, Count | same | done |
| `UpdateAsync(Dictionary)` | member-initializer `UpdateAsync` (dictionary form dropped on purpose) | dropped |
| SqlFn (in projections/updates) | `SqlFn` (also in `Where`), plus JSON and full text | done |
| Raw SQL: `SqlQueryAsync`, `ExecuteSqlAsync`, `SqlScalarAsync` | same, parameterized interpolation | done |
| Transactions (`BeginTransactionAsync`, `TransactionScope`) | `DataTransaction`, `InTransactionAsync`, savepoints | done |
| `DistinguishDuplicateKeyError` option | always `data.duplicate_key` | dropped (always on) |
| Multiple named connections, live config reload, health, localization | `DataLibrary` | **open, WP-A2** |
| Transient retry with backoff | resilience middleware | **open, WP-90** |
| Query cache (`WithCache`), SmartCache pools, cache coordinator | unified cache | **open, WP-80 to WP-84** |
| Observability: query/slow-query events, EXPLAIN on slow, N+1 detector | telemetry middleware | **open, WP-A0** |
| Table sync (`SyncTableAsync`, sync modes, CRC state, `GET_LOCK`, backup before alter) | schema management | **open, WP-70 to WP-73** |
| Migrations (`IMigration`, runner, tracker, rollback) | migrations | **open, WP-74** |
| BackupManager (DDL backups, restore), DatabaseAdmin (create/drop/exists db and table, truncate), `CreateDatabaseIfMissing` | admin and backup | **open, WP-75** |
| RetentionWorker (`[RetainDays]`) | retention on `DataScheduler` | **open, WP-75** |
| Bulk insert (batched multi-row) | batched `InsertManyAsync`; native `MySqlBulkCopy` | batched done; native **open, WP-A1** |
| Charset/engine/collation per table and column | `[MySqlTable]`/`[MySqlColumn]` hints (used by DDL in WP-72) | mapping done; DDL **open, WP-72** |

## CL.PostgreSQL (removed 2026-10-31, after WP-51)

CL.PostgreSQL had the same feature set as CL.MySQL2, minus `Exclude`, so the CL.MySQL2 table above applies.
PostgreSQL-specific items:

| CL.PostgreSQL feature | CL.Data | Status |
|---|---|---|
| `DefaultSchema` scoping | `PostgreSql.DefaultSchema`; tables are schema-qualified | done |
| `ON CONFLICT` upserts, `RETURNING` identities | dialect and write planner | done |
| jsonb, uuid, timestamptz, bytea | type mapper | done |
| inet, interval, arrays, ranges | `[PostgresColumn(Type = ...)]` hints, values bound as-is | partly (no array/range translation) |
| N+1 detector, EXPLAIN on slow queries (`SlowQueryExplain`) | telemetry middleware | **open, WP-A0** |
| `pg_advisory_lock` schema-sync lock, `pg_catalog` introspection, DDL | schema management | **open, WP-71 to WP-73** |
| Runtime options (`PostgreSqlRuntimeOptions`) | `PostgreSqlOptions` (auto-prepare, multiplexing) | done |
