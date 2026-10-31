# CL.MSSQL

> A typed data-access layer for SQL Server 2019+, SQL Server 2022/2025, and Azure SQL — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

`CL.MSSQL` is the flagship data library for CodeLogic 5. Map a plain class with attributes and the library keeps the live table in shape, generates reflection-free row mappers, translates LINQ-shaped expressions to real SQL, and caches results with version-stamped invalidation. It builds on [Microsoft.Data.SqlClient](https://www.nuget.org/packages/Microsoft.Data.SqlClient) and works against SQL Server 2019+, SQL Server 2022/2025, and Azure SQL. Every fallible *execution* returns a framework `Result<T>` — no exceptions for the expected failure paths. Programming errors caught while a query is being composed (an unsupported expression, `.Join` after `.OrderBy`, `WhereExists` against the outer query's own table, a bad property name passed to `UpsertWithIncrementsAsync`) still throw.

| | |
|---|---|
| **Package** | [`CodeLogic.MSSQL`](https://www.nuget.org/packages/CodeLogic.MSSQL) |
| **Library class** | `CL.MSSQL.MSSQLLibrary` |
| **Config files** | `config.mssql.json` · `config.mssql.cache.json` |
| **Dependencies** | Microsoft.Data.SqlClient 7.x |
| **Engines** | SQL Server 2019, 2022, 2025 · Azure SQL Database |

This overview covers loading, the entry points, and configuration. The deep material lives on four sub-pages:

- **[Query Builder](queries.md)** — `Where` / subquery filters / ordering / paging / typed and raw joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Migrations](schema-migrations.md)** — entity attributes, `SyncMode` & `SchemaSyncLevel`, `SyncTableAsync` / `SyncSchemaAsync`, the CRC sentinel, soft delete, retention, imperative migrations, backups & restore.
- **[Performance & Caching](performance.md)** — the result cache, time quantization, table-version invalidation, `SmartCachePool`, multi-node coordination, transient retry, the N+1 detector, slow-query / `EXPLAIN`, compiled materializers, projection pushdown.
- **[Capability parity](parity.md)** — the capability matrix for this library and its SQL Server-native differences.

## Install & load

```bash
dotnet add package CodeLogic.MSSQL
```

```csharp
using CL.MSSQL;

await Libraries.LoadAsync<MSSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mssql = Libraries.Get<MSSQLLibrary>();
```

Set your connection in `config.mssql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.MSSQL.Models`. The full attribute set is documented on the [Schema & Migrations](schema-migrations.md) page.

```csharp
using CL.MSSQL.Models;

[Table(Name = "users", Schema = "dbo")]
public class User
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 120, Unique = true)]            public string Email { get; set; } = "";
    [Column(Size = 80, Index = true)]              public string DisplayName { get; set; } = "";
    [Column] public int LoginCount { get; set; }
    [Column] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

Reconcile it to the database once, at startup:

```csharp
Result<SyncResult> sync = await mssql.SyncTableAsync<User>();
// or reconcile a whole set as one pass under a single cross-node lock:
await mssql.SyncSchemaAsync(typeof(User), typeof(Order), typeof(Customer));
```

## Repository basics

`GetRepository<T>()` returns a `Repository<T>` covering the common CRUD, paging, count, upsert, increment, and delete operations. All return `Result<…>`.

```csharp
var repo = mssql.GetRepository<User>();

// Create
Result<User> created = await repo.InsertAsync(new User { Email = "ada@example.com", DisplayName = "Ada" });
Result<int>  many    = await repo.InsertManyAsync(batch);   // chunked at 500 rows, capped by the 2,100-parameter limit

// Idempotent insert: a duplicate unique/primary key (2627/2601) is a normal outcome, not an error.
// Any other error rolls back everything the call wrote (own transaction, or a savepoint inside
// an explicit TransactionScope), so a failure Result means nothing was written.
Result<InsertOutcome> outcome = await repo.InsertIgnoreAsync(evt);      // Inserted | Duplicate
Result<int>           added   = await repo.InsertManyIgnoreAsync(batch); // rows actually inserted

// Read
Result<User?>       byId   = await repo.GetByIdAsync(1L);
Result<List<User>>  byCol  = await repo.GetByColumnAsync(nameof(User.DisplayName), "Ada");
Result<List<User>>  all    = await repo.GetAllAsync();
Result<long>        count  = await repo.CountAsync();
Result<List<User>>  found  = await repo.FindAsync(u => u.LoginCount > 10);

// Paged
Result<PagedResult<User>> page =
    await repo.GetPagedAsync(page: 1, pageSize: 25, orderByColumn: nameof(User.CreatedUtc), descending: true);

// Update / upsert
Result<User> updated = await repo.UpdateAsync(created.Value!);
Result<User> upserted = await repo.UpsertAsync(new User { Email = "ada@example.com", DisplayName = "Ada L." });
Result<int>  upmany   = await repo.UpsertManyAsync(rows);

// Atomic counter adjustments (single UPDATE … SET col = col ± delta)
Result<int> inc = await repo.IncrementAsync(1L, u => u.LoginCount, 1);
Result<int> dec = await repo.DecrementAsync(1L, u => u.LoginCount, 1);
Result<int> adj = await repo.AdjustAsync(1L, u => u.LoginCount, -5);

// Delete — soft if the entity declares [SoftDelete], otherwise a physical row delete
Result<bool> deleted = await repo.DeleteAsync(1L);
Result<bool> purged  = await repo.HardDeleteAsync(1L);   // always physical
```

`UpsertWithIncrementsAsync` performs an insert-or-accumulate — useful for counters seeded on first sight:

```csharp
await repo.UpsertWithIncrementsAsync(
    insertSeed: new DailyHit { Day = today, Hits = 1 },
    incrementProperties: new[] { nameof(DailyHit.Hits) });
```

`PagedResult<T>` carries `Items`, `PageNumber`, `PageSize`, `TotalItems`, `TotalPages`, `HasPreviousPage`, and `HasNextPage`.

For keyset paging without `OFFSET` or `COUNT(*)`, compose an explicitly ordered
`Query<T>()`, call `ToCursorPagedListAsync(pageSize)`, and pass the returned
`NextCursor` to `.After(cursor)` for the next page. See [Queries](queries.md#ordering--paging).

## Entry points

Everything flows through three methods on the library, plus a raw-SQL escape hatch.

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetRepository<T>(connectionId = "Default")` | `Repository<T>` | CRUD / paging / upsert / delete. |
| `Query<T>(connectionId = "Default")` | `QueryBuilder<T>` | Fluent LINQ-to-SQL queries. See [Query Builder](queries.md). |
| `BeginTransactionAsync(connectionId, ct)` | `Task<TransactionScope>` | Explicit transaction (`IAsyncDisposable`, auto-rollback). |
| `SqlQueryAsync<T>(sql, parameters, …)` | `Result<List<T>>` | Raw query materialized into `T`. |
| `ExecuteSqlAsync(sql, parameters, …)` | `Result<int>` | Raw non-query, returns affected rows. |
| `SqlScalarAsync<T>(sql, parameters, …)` | `Result<T?>` | Raw single-value query. |

The `connectionId` selects one of the named databases in `config.mssql.json`; it defaults to `"Default"`. Library properties expose the underlying machinery for advanced use: `ConnectionManager`, `TableSync`, `MigrationTracker`, `BackupManager`, `SchemaState`, and `Migrations`.

## Configuration summary

Two files are written on first run.

`config.mssql.json` (section `mssql`) holds a `Databases` dictionary keyed by connection id — `Default` is created automatically.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Per-database master switch. |
| `ConnectionString` | `null` | Authoritative secret override; enables all SqlClient/Entra modes through the included `Microsoft.Data.SqlClient.Extensions.Azure` 7.x provider. |
| `Host` / `Port` / `Instance` | `localhost` / `1433` / `null` | Structured server endpoint. |
| `Database` / `Username` / `Password` | `""` | Credentials. |
| `AuthenticationMode` | `SqlLogin` | `SqlLogin` or `IntegratedSecurity`. |
| `EnablePooling` | `true` | Connection pooling. |
| `MinPoolSize` / `MaxPoolSize` | `0` / `100` | Pool bounds. |
| `ConnectionLifetime` | `300` | Seconds before a pooled connection is recycled. |
| `ConnectionTimeout` / `CommandTimeout` | `30` / `30` | Seconds. |
| `Encrypt` / `TrustServerCertificate` | `true` / `false` | TLS validation. |
| `DefaultSchema` | `dbo` | Default schema convention. |
| `SyncMode` | `Production` | `Developer` · `Production` · `Migration`. See [Schema & Migrations](schema-migrations.md). |
| `SchemaSyncLevel` | `Safe` | Low-level cap: `None` · `Safe` · `Additive` · `Full`. |
| `AllowDestructiveSync` | `false` | Legacy flag honoured under `SyncMode` mapping. |
| `BackupDirectory` | `null` | Where schema backups are written. `null` uses `<DataDirectory>/backups`; a relative path is resolved against the data directory. |
| `SlowQueryThresholdMs` | `1000` | Threshold for `SlowQueryEvent`. |
| `CaptureExplainOnSlowQuery` | `true` | Attach best-effort estimated `SHOWPLAN_XML`. |
| `QueryTimeoutMs` | `30000` | Command timeout for the query commands the library issues (repositories, query builder, projections, joins, grouped queries). Overrides the connection string's `Command Timeout` for those; `0` leaves it in place. |
| `MaxBatchInsertSize` | `500` | Rows per multi-row `INSERT`/upsert statement, capped further by the 2,100-parameter limit. |
| `DistinguishDuplicateKeyError` | `false` | When true, a duplicate key (2627/2601) in `InsertAsync` / `InsertManyAsync` returns error code `mssql.duplicate_key` and logs a Warning instead of `mssql.insert_failed` / `mssql.bulk_insert_failed` at Error. Also settable per repository via `Repository<T>.DistinguishDuplicateKeyError`. |
| `MaxInClauseValues` | `1000` | Advisory cap on a generated `IN (...)` list. Exceeding it logs one warning per query build naming the entity and the count — nothing is chunked, truncated or rejected. |
| `PreparedStatementCacheSize` | `256` | **Obsolete, not applied.** `Microsoft.Data.SqlClient` has no client-side statement cache to size; plan caching is the server's. Configure pooling on the connection string. |
| `TransientRetryCount` | `3` | Deadlock/lock-wait retries (0 disables). |
| `TransientRetryBaseDelayMs` | `50` | Base backoff; exponential + jitter. |
| `N1DetectorThreshold` | `0` | Executions of one normalized statement within a one-second window that raise `N1QueryDetectedEvent`. `0` disables the detector entirely (and costs nothing on the query path). |
| `CacheEnabledOverride` | `null` | Per-database override of the global cache `Enabled` switch. `null` = no override. |
| `DefaultStringSize` | `255` | `nvarchar` length inferred for a string property with no explicit `[Column(Size = ...)]`. Changing it changes the generated DDL and therefore the schema CRC. |

`config.mssql.cache.json` (section `mssql.cache`) controls the result cache.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Global cache switch. |
| `MaxEntries` | `10000` | Entry ceiling. |
| `MaxMemoryMb` | `256` | **Obsolete, not applied.** The in-process store evicts by entry count; use `MaxEntries`. |
| `DefaultTtlSeconds` | `60` | TTL used by the parameterless `.WithCache()` overload. `.WithCache(ttl)` still wins when a query names its own. |
| `TimeQuantizeSeconds` | `60` | Quantization bucket for DateTime params (see [Performance](performance.md)). |
| `PublishEvents` | `true` | Publish `CacheHitEvent` / `CacheMissEvent` on every cache lookup. Turn off to silence them; the cache itself is unaffected and `QueryExecutedEvent` still carries `CacheHit`. |

Full caching behaviour is on the [Performance & Caching](performance.md) page.

### Changing config while running

With CodeLogic 4.9+, both sections apply live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher — and only the databases that changed are touched:

- **Connection settings changed** (the effective connection string — `connectionString`, or endpoint, credentials, encryption, pooling, timeouts): the idle pool is cleared, the connection tested, and the schema-state tables ensured. In-flight queries finish on their old connections.
- **Other settings changed**: updated in place, no reconnect.
- **Added / removed** (or enabled / disabled): connected / disconnected.
- **Untouched databases**: nothing happens; their connections stay open.

An unreachable database leaves the change saved and reports `ConfigApplyResult.Applied = false`; the other databases are still applied. A runtime `SetSyncMode` survives unrelated changes. Enabling the first database when the library started with none requires a restart.

## Health check

```csharp
HealthStatus status = await mssql.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy

Result<bool> ok = await mssql.TestConnectionAsync("Default");
```

`HealthCheckAsync` probes the configured databases; `TestConnectionAsync` opens a single named connection and reports success.

## Events

All events implement `IEvent` and publish to the CodeLogic event bus.

| Event | Published when |
|-------|----------------|
| `DatabaseConnectedEvent` | A database connection is established. |
| `DatabaseDisconnectedEvent` | A connection is closed or lost. |
| `TableSyncedEvent` | A table is reconciled by schema sync. |
| `QueryExecutedEvent` | Any query completes (carries a `CacheHit` flag). |
| `SlowQueryEvent` | A query exceeds `SlowQueryThresholdMs` (carries `ExplainJson`). |
| `CacheHitEvent` | A cached result satisfies a read. |
| `CacheMissEvent` | A cacheable read misses the cache. |
| `N1QueryDetectedEvent` | One normalized statement runs `N1DetectorThreshold` times on a connection within a second (published once per window; the detector is off at the default threshold of `0`). |
| `HealthChangedEvent` | The health status transitions. |

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MSSQL)
