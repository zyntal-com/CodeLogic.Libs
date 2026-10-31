# CL.MySQL2

> A typed data-access layer for MySQL, MariaDB, and Percona — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

`CL.MySQL2` is the flagship data library for CodeLogic 5. Map a plain class with attributes and the library keeps the live table in shape, generates reflection-free row mappers, translates LINQ-shaped expressions to real SQL, and caches results with version-stamped invalidation. It builds on [MySqlConnector](https://www.nuget.org/packages/MySqlConnector) and works against MySQL, MariaDB, and Percona. Every fallible operation returns a framework `Result<T>` — no exceptions for the expected failure paths.

| | |
|---|---|
| **Package** | [`CodeLogic.MySQL2`](https://www.nuget.org/packages/CodeLogic.MySQL2) |
| **Library class** | `CL.MySQL2.MySQL2Library` |
| **Config files** | `config.mysql.json` · `config.mysql.cache.json` |
| **Dependencies** | MySqlConnector 2.x |
| **Engines** | MySQL 5.7+ · MariaDB 10.3+ · Percona |

This overview covers loading, the entry points, and configuration. The deep material lives on three sub-pages:

- **[Query Builder](queries.md)** — `Where` / subquery filters / ordering / paging / typed and raw joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Migrations](schema-migrations.md)** — entity attributes, `SyncMode` & `SchemaSyncLevel`, `SyncTableAsync` / `SyncSchemaAsync`, the CRC sentinel, soft delete, retention, imperative migrations, backups & restore.
- **[Performance & Caching](performance.md)** — the result cache, time quantization, table-version invalidation, `SmartCachePool`, multi-node coordination, transient retry, slow-query reporting, compiled materializers, projection pushdown.

## Install & load

```bash
dotnet add package CodeLogic.MySQL2
```

```csharp
using CL.MySQL2;

await Libraries.LoadAsync<MySQL2Library>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mysql = Libraries.Get<MySQL2Library>();
```

Set your connection in `config.mysql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.MySQL2.Models`. The full attribute set is documented on the [Schema & Migrations](schema-migrations.md) page.

```csharp
using CL.MySQL2.Models;

[Table(Name = "users")]
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
Result<SyncResult> sync = await mysql.SyncTableAsync<User>();
// or reconcile a whole set as one pass under a single cross-node lock:
await mysql.SyncSchemaAsync(typeof(User), typeof(Order), typeof(Customer));
```

## Repository basics

`GetRepository<T>()` returns a `Repository<T>` covering the common CRUD, paging, count, upsert, increment, and delete operations. All return `Result<…>`.

```csharp
var repo = mysql.GetRepository<User>();

// Create
Result<User> created = await repo.InsertAsync(new User { Email = "ada@example.com", DisplayName = "Ada" });
Result<int>  many    = await repo.InsertManyAsync(batch);   // chunked at 500 rows per INSERT

// Idempotent insert: a duplicate unique/primary key is a normal outcome, not an error
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

// InsertIgnoreAsync / InsertManyIgnoreAsync use INSERT IGNORE. Any warning other than a
// duplicate key (truncation, missing NOT NULL value) fails the call and rolls back what it
// wrote, so a failure Result means nothing was written. Inside an explicit transaction the
// call rolls back to a savepoint and leaves the rest of the transaction intact.

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

The `connectionId` selects one of the named databases in `config.mysql.json`; it defaults to `"Default"`. Library properties expose the underlying machinery for advanced use: `ConnectionManager`, `TableSync`, `MigrationTracker`, `BackupManager`, `SchemaState`, and `Migrations`.

## Configuration summary

Two files are written on first run.

`config.mysql.json` (section `mysql`) holds a `Databases` dictionary keyed by connection id — `Default` is created automatically.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Per-database master switch. |
| `Host` / `Port` | `localhost` / `3306` | Server endpoint. |
| `Database` / `Username` / `Password` | `""` | Credentials. |
| `EnablePooling` | `true` | Connection pooling. |
| `MinPoolSize` / `MaxPoolSize` | `1` / `100` | Pool bounds. |
| `ConnectionLifetime` | `300` | Seconds before a pooled connection is recycled. |
| `ConnectionTimeout` / `CommandTimeout` | `30` / `30` | Seconds. |
| `EnableSsl` | `false` | Sets `SslMode=Required`. `AllowPublicKeyRetrieval` is applied too. |
| `SslCertificatePath` | `null` | PEM CA file used to verify the server certificate. Applied only when `EnableSsl` is true, as MySqlConnector's `SslCa`; raises the SSL mode to `VerifyCA`. |
| `CharacterSet` / `Collation` | `utf8mb4` / `utf8mb4_unicode_ci` | `CharacterSet` goes into the connection string; `Collation` is informational only. |
| `SyncMode` | `Production` | `Developer` · `Production` · `Migration`. See [Schema & Migrations](schema-migrations.md). |
| `SchemaSyncLevel` | `Safe` | Low-level cap: `None` · `Safe` · `Additive` · `Full`. |
| `AllowDestructiveSync` | `false` | Legacy flag honoured under `SyncMode` mapping. |
| `BackupDirectory` | `null` | Blank = `DataDirectory/backups`. When set, backups for this connection are written to and read from that directory (a relative path resolves under the data directory). |
| `SlowQueryThresholdMs` | `1000` | Threshold for `SlowQueryEvent`. |
| `CaptureExplainOnSlowQuery` | `false` | When true, a slow query also runs `EXPLAIN FORMAT=JSON` on a separate connection and attaches the plan to `SlowQueryEvent.ExplainJson`. Best-effort; see [Performance](performance.md). |
| `QueryTimeoutMs` | `30000` | Command timeout for the commands the library creates, rounded up to whole seconds. 0 = inherit the connection string's `CommandTimeout`. The default equals that 30s default. |
| `MaxBatchInsertSize` | `500` | Rows per batched `INSERT` in `InsertManyAsync` / `UpsertManyAsync` / `InsertManyIgnoreAsync`. |
| `DistinguishDuplicateKeyError` | `false` | When true, MySQL error 1062 in `InsertAsync` / `InsertManyAsync` returns error code `mysql.duplicate_key` and logs a Warning instead of `mysql.insert_failed` / `mysql.bulk_insert_failed` at Error. Also settable per repository via `Repository<T>.DistinguishDuplicateKeyError`. |
| `MaxInClauseValues` | `1000` | Advisory. A generated `IN (...)` list above this logs one warning per query build naming the entity and the count; nothing is chunked or rejected. |
| `PreparedStatementCacheSize` | `256` | **Obsolete and ignored.** Statement caching is configured on the MySqlConnector connection string (`IgnorePrepare=false`). |
| `TransientRetryCount` | `3` | Deadlock/lock-wait retries (0 disables). |
| `TransientRetryBaseDelayMs` | `50` | Base backoff; exponential + jitter. |
| `N1DetectorThreshold` | `0` | Publish `N1QueryDetectedEvent` when one normalized query template repeats this many times on the connection within a one-second window. 0 disables. |
| `CacheEnabledOverride` | `null` | Per-database override of the global cache switch. Null inherits it. |
| `DefaultStringSize` | `255` | VARCHAR length for an inferred string column with no explicit `Size`. Applied process-wide from the `Default` database (DDL generation is not connection-scoped). |

`config.mysql.cache.json` (section `mysql.cache`) controls the result cache.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Global cache switch. |
| `MaxEntries` | `10000` | Entry ceiling. |
| `MaxMemoryMb` | `256` | **Obsolete and ignored.** The in-process store evicts by entry count — use `MaxEntries`. |
| `DefaultTtlSeconds` | `60` | TTL used by the parameterless `WithCache()` overload on the query builder and on a projection. |
| `TimeQuantizeSeconds` | `60` | Quantization bucket for DateTime params (see [Performance](performance.md)). |
| `PublishEvents` | `true` | Publish `CacheHitEvent` / `CacheMissEvent`. False keeps the cache working but silences the events. |

Full caching behaviour is on the [Performance & Caching](performance.md) page.

### Changing config while running

With CodeLogic 4.9+, both sections apply live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher — and only the databases that changed are touched:

- **Connection settings changed** (endpoint, credentials, pooling, timeouts, SSL, charset): the idle pool is cleared, the connection tested, and the schema-state tables ensured. In-flight queries finish on their old connections.
- **Other settings changed**: updated in place, no reconnect.
- **Added / removed** (or enabled / disabled): connected / disconnected.
- **Untouched databases**: nothing happens; their connections stay open.

An unreachable database leaves the change saved and reports `ConfigApplyResult.Applied = false`; the other databases are still applied. A runtime `SetSyncMode` survives unrelated changes. Enabling the first database when the library started with none requires a restart.

## Health check

```csharp
HealthStatus status = await mysql.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy

Result<bool> ok = await mysql.TestConnectionAsync("Default");
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
| `SlowQueryEvent` | A query exceeds `SlowQueryThresholdMs`. `ExplainJson` carries the `EXPLAIN FORMAT=JSON` plan when `CaptureExplainOnSlowQuery` is on and the statement can be explained; null otherwise. |
| `CacheHitEvent` | A cached result satisfies a read. |
| `CacheMissEvent` | A cacheable read misses the cache. |
| `N1QueryDetectedEvent` | One query template repeats `N1DetectorThreshold` times within a second on a connection (0, the default, disables detection). |
| `HealthChangedEvent` | The health status transitions. |

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
