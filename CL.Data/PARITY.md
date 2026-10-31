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
