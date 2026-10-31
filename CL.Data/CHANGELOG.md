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
