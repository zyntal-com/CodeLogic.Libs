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
