# CodeLogic.MySQL2

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2)](https://www.nuget.org/packages/CodeLogic.MySQL2)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> A typed MySQL data layer for CodeLogic 4: repositories, LINQ-shaped SQL, cursor paging, schema synchronization, migrations, caching, resilience, and operational diagnostics in one library.

`CodeLogic.MySQL2` sits between a micro-ORM and a lightweight application data platform. Map ordinary C# classes with attributes, then use repositories or a fluent query builder while the library handles parameterized SQL, compiled row materialization, schema drift, cache invalidation, retries, health checks, and events.

It is built on [MySqlConnector](https://www.nuget.org/packages/MySqlConnector) and supports MySQL, MariaDB, and Percona. Fallible operations return CodeLogic `Result<T>` values so expected failures can be handled without exception-driven control flow.

## What the library covers

| Area | Capabilities |
|------|--------------|
| Entity persistence | CRUD repositories, batch insert, upsert, batch upsert, insert-or-increment, atomic counters, soft and hard delete. |
| Querying | Typed filters, string and collection predicates, ordering, offset paging, cursor paging, subqueries, typed and raw joins, projections, grouping, aggregates, and set-based writes. |
| Schema management | Attribute-driven tables and columns, type inference, keys, foreign keys, indexes, covering indexes, column renames, schema diffing, and three synchronization modes. |
| Migrations | Versioned up/down migrations, discovery, pending plans, checksums, rollback preflight, migration tracking, and a cross-node schema lock. |
| Data lifecycle | Soft-delete filtering, retention-based background purging, schema backup, and schema restore. |
| Performance | Compiled materializers, projection pushdown, batched writes, result caching, single-flight misses, warm smart-cache pools, and time-quantized cache keys. |
| Reliability | Connection pooling, named databases, explicit transactions, deadlock/lock-timeout retry, command cancellation, and health checks. |
| Operations | Query timing, slow-query detection, cache statistics, pool statistics, and CodeLogic events. |
| Escape hatches | Parameterized raw queries, commands, scalar reads, direct connection access, and pluggable cache/coordinator interfaces. |

## Install

```bash
dotnet add package CodeLogic.MySQL2
```

## Quick start

```csharp
using CL.MySQL2;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using CodeLogic;
using CodeLogic.Core.Results;

[Table(Name = "users")]
public class User
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "email", DataType = DataType.VarChar, Size = 160, NotNull = true, Unique = true, Index = true)]
    public string Email { get; set; } = "";

    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

await Libraries.LoadAsync<MySQL2Library>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mysql = Libraries.Get<MySQL2Library>()!;

// Reconcile the table with the mapped model.
Result<SyncResult> sync = await mysql.SyncTableAsync<User>();

// Repository persistence.
Repository<User> users = mysql.GetRepository<User>();
Result<User> inserted = await users.InsertAsync(
    new User { Email = "ada@example.com" });

// Typed query translated and executed in MySQL.
Result<List<User>> recent = await mysql.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(20)
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();
```

Configuration files are generated on first run. Add the connection details to `config.mysql.json`, restart, and the named connection is ready for repositories, queries, schema sync, and migrations.

## Entity mapping and schema

The model is the schema source of truth. `CL.MySQL2.Models` provides attributes for the table shape and lifecycle:

- `[Table]` controls the table name, engine, charset, collation, and comment.
- `[Column]` controls the physical name, type, length, precision, scale, primary/auto-increment flags, nullability, uniqueness, indexing, defaults, unsigned values, charset, comments, and binary storage.
- `[ForeignKey]`, `[Index]`, and `[CompositeIndex]` describe constraints and single, composite, unique, or covering indexes.
- `[Ignore]` excludes a property from persistence.
- `[SoftDelete]` marks a nullable timestamp used for automatic read filtering and repository soft deletes.
- `[RetainDays]` enables scheduled batch deletion of expired rows (first pass 5 minutes after start, then every 24 hours).
- `PreviousName` on `[Column]` performs an in-place column rename so existing data is preserved.

Properties without `[Column]` use CLR type inference and their property name as the column name. When `[Column]` is used for explicit mapping, its `DataType` selects from the MySQL integer, decimal, floating point, bit, character, text, binary/blob, date/time, enum/set, JSON, and geometry families. `StorageType.Binary` can store a `Guid` as `BINARY(16)`; `SequentialGuid.NewId()` creates time-ordered UUIDv7 values suitable for indexed primary keys.

### Schema synchronization modes

| Mode | Intended environment | Behavior |
|------|----------------------|----------|
| `Production` | Normal production operation | Additive reconciliation; never drops. Destructive drift is reported through `DriftPending`. |
| `Developer` | Local and disposable environments | Fully reconciles the model, including removed columns, indexes, and foreign keys. |
| `Migration` | Deliberate production maintenance | Performs a backed-up, one-shot destructive reconciliation, then becomes a no-op once current. |

```csharp
Result<Dictionary<string, SyncResult>> schema = await mysql.SyncSchemaAsync(
    typeof(User), typeof(Order), typeof(Customer));

mysql.SetSyncMode(SyncMode.Production, connectionId: "Default");
```

Indexes are matched by name. If an index keeps its name but its definition changes (columns, their order, or uniqueness), sync logs a drift warning. `Production` leaves the existing index as it is and reports `DriftPending`; `Developer` and `Migration` rebuild it.

Every desired schema is hashed into `__schema_state`. An unchanged model takes the CRC fast path and skips `information_schema` inspection and DDL entirely. `SyncResult` reports operations, errors, duration, CRC, whether work was skipped, and whether destructive drift remains pending.

## Repository API

`mysql.GetRepository<T>(connectionId)` provides the conventional persistence surface:

| Operation | Purpose |
|-----------|---------|
| `InsertAsync` / `InsertManyAsync` | Insert one row with its generated key populated, or insert chunked batches and return the affected count. |
| `InsertIgnoreAsync` / `InsertManyIgnoreAsync` | Insert one row or batches while ignoring duplicate-key conflicts; returns `InsertOutcome` (`Inserted` with generated Id populated, or `Duplicate`) or the inserted count. Duplicates are a normal outcome, not an error. Any other warning (truncation, missing NOT NULL value) fails the call and rolls back everything it wrote. |
| `UpsertAsync` / `UpsertManyAsync` | Insert or update on duplicate key. |
| `UpsertWithIncrementsAsync` | Insert a seed or atomically accumulate selected numeric columns. |
| `GetByIdAsync` / `GetByColumnAsync` / `GetAllAsync` / `FindAsync` | Typed entity retrieval. |
| `GetPagedAsync` | Page-number/offset paging with totals. |
| `CountAsync` | Count table rows. Applies the `[SoftDelete]` filter like the other reads, so it agrees with `GetAllAsync`. |
| `UpdateAsync` | Update an entity by its mapped primary key. |
| `IncrementAsync` / `DecrementAsync` / `AdjustAsync` | Atomic server-side counter changes. |
| `DeleteAsync` | Soft delete when `[SoftDelete]` is present; otherwise physically delete. |
| `HardDeleteAsync` | Always physically delete. |

Idempotent writes (for example re-sent event batches keyed by a unique client id) should use `InsertIgnoreAsync` / `InsertManyIgnoreAsync`. Callers that keep `InsertAsync` can set `DistinguishDuplicateKeyError` (database config or `Repository<T>` property) to get the error code `mysql.duplicate_key`, logged as a Warning, instead of the generic `mysql.insert_failed` Error for MySQL error 1062. It is off by default, so existing behavior is unchanged.

## Query builder

`mysql.Query<T>()` translates supported expression trees into parameterized SQL. Filtering and materialization remain server-side; the library does not load rows and apply LINQ in memory.

```csharp
string[] countries = ["DK", "SE", "NO"];

Result<List<Order>> orders = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && countries.Contains(o.Country))
    .Where(o => o.Reference.StartsWith("WEB-"))
    .OrderByDescending(o => o.CreatedUtc)
    .ToListAsync();
```

Supported filters include comparisons, boolean composition, negation, null checks, captured values, string `Contains`/`StartsWith`/`EndsWith`, and collection `Contains` translated to `IN (...)`.

### Subqueries

```csharp
var shipped = await mysql.Query<Order>()
    .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent")
    .ToListAsync();

var vipOrders = await mysql.Query<Order>()
    .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
    .ToListAsync();
```

`WhereExists`, `WhereNotExists`, `WhereIn`, and `WhereNotIn` generate SQL subqueries and compose with normal filters.

### Ordering and pagination

Offset paging returns totals and page-number metadata:

```csharp
Result<PagedResult<Order>> page = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);
```

Cursor paging performs a keyset seek without `OFFSET` or `COUNT(*)`, making it the better fit for deep or frequently changing result sets:

```csharp
Result<CursorPagedResult<Order>> first = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToCursorPagedListAsync(pageSize: 25);

Result<CursorPagedResult<Order>> next = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .After(first.Value!.NextCursor)
    .ToCursorPagedListAsync(pageSize: 25);
```

Cursor ordering supports multiple ASC/DESC and nullable columns. A mapped primary key is appended automatically as a stable tie-breaker. Continuation tokens are versioned Base64URL JSON bound to the entity/table and exact ordering; they are opaque paging state, but they are not signed or encrypted. Encoded tokens longer than 4,096 characters are rejected before decoding.

### Joins, projections, grouping, and aggregates

Typed joins support inner, left, and right equi-joins, composite keys, two-entity filters and ordering, and compiled projection into a DTO:

```csharp
Result<List<OrderView>> views = await mysql.Query<Order>()
    .Where(o => o.Total > 100)
    .Join<Customer, long, OrderView>(
        o => o.CustomerId,
        c => c.Id,
        (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total },
        JoinType.Left)
    .Where((o, c) => c.IsVip)
    .OrderByDescending((o, c) => o.Total)
    .ToListAsync();
```

Projection pushdown selects only referenced columns, while grouped projections translate aggregate operations to SQL:

```csharp
Result<List<DailyTotal>> totals = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30))
    .GroupBy(o => o.Day)
    .Select(g => new DailyTotal
    {
        Day = g.Key,
        Count = g.Count(),
        Revenue = g.Sum(o => o.Total),
        Average = g.Average(o => o.Total)
    })
    .ToListAsync();
```

Single-value terminals include `CountAsync`, `MinAsync`, `MaxAsync`, `SumAsync`, and `AverageAsync`. Aggregates (`MaxAsync`, `MinAsync`, `SumAsync`) accept nullable selectors such as `r => (DateTime?)r.CreatedUtc` or `r => (int?)r.Score`, returning `null` on an empty set while non-nullable selectors preserve their standard defaults (`DateTime.MinValue`, `0`). `Select(...)` also supports anonymous or DTO projections without first materializing the entity.

### Set-based updates and deletes

```csharp
Result<int> updated = await mysql.Query<Order>()
    .Where(o => o.Status == "draft")
    .UpdateAsync(o => new Order
    {
        Status = "open",
        UpdatedUtc = DateTime.UtcNow
    });

Result<int> deleted = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();
```

These operations issue one server-side statement and do not materialize matching rows.
Setter values may read the row (`Tokens = Math.Min(cap, t.Tokens + refill)`); captured values
are always sent as parameters. `UpdateAndGetAsync` runs the same update and returns the rows
as they are afterwards, and `AnyAsync` checks for a match with `SELECT 1 … LIMIT 1`.

Inside a transaction, `ForUpdate()` / `ForShare()` lock the rows a read returns
(`LockWait.NoWait` / `LockWait.SkipLocked` for job queues), and
`Repository.UpsertAsync(entity, (current, incoming) => new T { … })` inserts or updates from
the current row in one statement.

## Raw SQL, transactions, and multiple databases

Use the raw SQL APIs when a query is outside the typed builder's scope. Values remain parameterized and executions still flow through retries and observability:

```csharp
Result<List<User>> rows = await mysql.SqlQueryAsync<User>(
    "SELECT * FROM users WHERE email LIKE @pattern",
    new Dictionary<string, object?> { ["@pattern"] = "%@example.com" });

Result<int> affected = await mysql.ExecuteSqlAsync(
    "UPDATE users SET verified = 1 WHERE id = @id",
    new Dictionary<string, object?> { ["@id"] = 42L });

Result<long> count = await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM users");
```

`BeginTransactionAsync` returns an async-disposable transaction that rolls back automatically unless committed. Pass the scope to `GetRepository<T>` or `Query<T>` to enlist that work in it:

```csharp
await using TransactionScope tx = await mysql.BeginTransactionAsync();
var accounts = mysql.GetRepository<Account>(tx);

await accounts.AdjustAsync(1L, a => a.Balance, -100m);
await accounts.AdjustAsync(2L, a => a.Balance, 100m);
await mysql.Query<Audit>(tx).Where(a => a.Stale).DeleteAsync();
await tx.CommitAsync();
```

Configure multiple named database connections and select one through `GetRepository<T>(connectionId)`, `Query<T>(connectionId)`, the raw SQL `connectionId` argument, or `.WithConnection(connectionId)`.

## Migrations, backups, and data lifecycle

Declarative sync handles structural model drift. Imperative `IMigration` implementations handle seeds, backfills, data transforms, and semantic changes that a schema diff cannot infer.

```csharp
public sealed class SeedRoles() : Migration("1.4.0", 1, "Seed default roles")
{
    public override Task UpAsync(IMigrationContext context, CancellationToken ct) =>
        context.ExecuteAsync(
            "INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);

    public override Task DownAsync(IMigrationContext context, CancellationToken ct) =>
        context.ExecuteAsync(
            "DELETE FROM roles WHERE name IN ('admin', 'user')", ct: ct);
}

mysql.RegisterMigration(new SeedRoles())
     .RegisterMigrationsFrom(typeof(Program).Assembly);

IReadOnlyList<MigrationPlanItem> pending = await mysql.GetPendingMigrationsAsync();
Result<MigrationRunResult> applied = await mysql.MigrateAsync();
```

Migrations run in version/order sequence, are tracked in `__migrations`, verify checksums, and execute under the same cross-node lock as schema sync. `RollbackAsync(target)` preflights the complete range before running `DownAsync` newest-first.

Before destructive schema reconciliation, the backup manager writes DDL snapshots. `RestoreSchemaAsync` can replay the latest or a named snapshot and then clears the CRC state so the next sync performs a full comparison. These are schema backups only; they do not preserve table rows.

`mysql.Admin` creates, drops and checks databases and tables (`CreateDatabaseAsync`,
`DropDatabaseAsync`, `DatabaseExistsAsync`, `CreateTableAsync<T>`, `DropTableAsync<T>`,
`TruncateTableAsync<T>`, `TableExistsAsync<T>`), and `CreateDatabaseIfMissing` in config
creates the database at startup.

For row lifecycle management, `[SoftDelete]` changes repository deletion into a timestamp update and filters ordinary reads by default. `.IncludeDeleted()` opts a query back into those rows. `[RetainDays]` registers an entity for background batch purging based on its timestamp column; the worker's entry list is live, so an entity reconciled by `SyncTableAsync` / `SyncSchemaAsync` after `CodeLogic.StartAsync()` is still picked up. `RunRetentionOnceAsync()` triggers a pass on demand.

## Caching and performance

### Cache-aside queries

```csharp
Result<List<User>> cached = await mysql.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-30))
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();
```

- Cache keys include the connection, SQL, and parameters.
- Table version stamps invalidate cached results after repository or query-builder mutations.
- Concurrent misses for one key collapse into a single database execution.
- `DateTime` parameters within 365 days of now are quantized to a `TimeQuantizeSeconds` bucket (60s by default) so rolling-window queries reuse cache entries.
- `ICacheStore` and `ICacheCoordinator` provide seams for shared stores, cross-node invalidation, and refresh leases.

### Smart cache pools

```csharp
SmartCachePool dashboard = mysql.RegisterCachePool(
    name: "dashboard",
    refreshEvery: TimeSpan.FromSeconds(30),
    maxIdleFires: 10);

Result<List<User>> warm = await mysql.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-1))
    .SmartCache("dashboard")
    .ToListAsync();

await mysql.RefreshCachePoolAsync("dashboard");
QueryCacheStats cacheStats = mysql.GetCacheStats();
IReadOnlyList<SmartCachePoolStats> poolStats = mysql.GetCachePoolStats();
```

Smart pools refresh registered queries in the background, retire idle entries, and optionally coordinate a single refresh owner across nodes. Compiled materializers, projection pushdown, chunked insert/upsert operations, and connection pooling apply independently of result caching.

## Reliability and observability

- Deadlocks (`1213`) and lock-wait timeouts (`1205`) on individual non-transactional statements are retried with exponential backoff and jitter.
- `TestConnectionAsync` and the CodeLogic library health check expose connection health.
- `SlowQueryEvent` reports the SQL and elapsed milliseconds when the configured threshold is exceeded. With `CaptureExplainOnSlowQuery` on (default off), it also carries the `EXPLAIN FORMAT=JSON` plan in `ExplainJson`, captured best-effort on a separate connection.
- `QueryExecutedEvent` reports SQL, duration, row count, connection, and cache-hit status.
- `CacheHitEvent`, `CacheMissEvent`, `DatabaseConnectedEvent`, `DatabaseDisconnectedEvent`, `TableSyncedEvent`, and `HealthChangedEvent` integrate with the CodeLogic event bus. `N1QueryDetectedEvent` fires when one query template repeats `N1DetectorThreshold` times within a second (0, the default, disables detection).
- `GetCacheStats()` and `GetCachePoolStats()` expose cache entries, versions, refreshes, failures, and activity.

## Important behavior boundaries

- Cursor pagination is forward-only and applies to plain entity queries, not joined, projected, or grouped result shapes.
- Continuation tokens are encoded and validated but are not signed, encrypted, or bound to filter values.
- Typed joins and subquery-filtered queries are not result-cacheable because their dependencies span tables.
- Explicit transactions disable result caching, smart caching, and per-statement transient retry; retry the complete transaction at the application boundary.
- `Repository.DeleteAsync` honors `[SoftDelete]`; query-builder `DeleteAsync` is always a hard, set-based delete.
- Query-builder bulk updates/deletes intentionally bypass the soft-delete read filter so deleted rows can be restored or purged.
- Schema backups contain DDL only. Use a database backup strategy for row-level recovery.

## Configuration

The library generates `config.mysql.json` (`mysql`) and `config.mysql.cache.json` (`mysql.cache`). A minimal database entry looks like this:

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "Host": "localhost",
      "Port": 3306,
      "Database": "myapp",
      "Username": "app",
      "Password": "",
      "SyncMode": "Production",
      "MaxPoolSize": 100,
      "SlowQueryThresholdMs": 1000,
      "TransientRetryCount": 3,
      "DistinguishDuplicateKeyError": false
    }
  }
}
```

Applied database settings include endpoint and credentials, pooling, connection and command timeouts, SSL mode and CA path, connection charset, sync mode, the transient retry policy, the slow-query threshold and its optional `EXPLAIN` capture, the query timeout, the insert batch size, the backup directory, the N+1 detector threshold, the default string size, and the per-database cache override. Two fields are `[Obsolete]` and ignored: `PreparedStatementCacheSize` (configure statement caching on the connection string) and the cache section's `MaxMemoryMb` (the store evicts by entry count — use `MaxEntries`). `MaxInClauseValues` is advisory: an oversized generated `IN (...)` list warns rather than chunking. `Collation` remains informational. See the [overview](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/index.html) for the per-field status.

The cache configuration's global switch, entry limit, `DateTime` quantization window, default TTL (used by the parameterless `.WithCache()`), and hit/miss event switch are all applied at startup.

### Changing config while running

Both sections apply live — no restart — when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the CodeLogic config file watcher (CodeLogic 4.9+). Only the databases that changed are touched:

| Change to a database | What happens |
|----------------------|--------------|
| Connection settings (host, credentials, pooling, timeouts, SSL, charset) | Its idle pool is cleared, the connection is tested, and its schema-state tables are ensured. In-flight queries finish on their old connections. |
| Other settings (slow-query threshold, batch size, cache override, N+1 threshold, …) | Updated in place; no reconnect. |
| Added / enabled | Connects. |
| Removed / disabled | Its pool is cleared and the ID is unregistered. |
| Untouched | Nothing — its connections stay open. |

If a changed database cannot be reached, the change stays saved, the other databases are still applied, and the result reports `Applied = false` (the library shows Degraded until a later change connects). A sync mode set at runtime with `SetSyncMode` survives unrelated changes. Enabling the first database when the library started with none still requires a restart.

## Main entry points

| Member | Purpose |
|--------|---------|
| `GetRepository<T>(connectionId)` | Create a CRUD repository. |
| `Query<T>(connectionId)` | Start a typed entity query. |
| `SqlQueryAsync<T>` / `ExecuteSqlAsync` / `SqlScalarAsync<T>` | Execute parameterized raw SQL. |
| `BeginTransactionAsync` | Start an explicit transaction scope. |
| `SyncTableAsync<T>` / `SyncSchemaAsync` | Reconcile mapped schemas. |
| `RegisterMigration` / `MigrateAsync` / `RollbackAsync` | Manage imperative migrations. |
| `RestoreSchemaAsync` | Restore a DDL snapshot. |
| `RegisterCachePool` / `RefreshCachePoolAsync` | Manage warm query pools. |
| `GetCacheStats` / `GetCachePoolStats` | Inspect cache behavior. |
| `TestConnectionAsync` | Verify a named database connection. |

## Documentation

- [Overview](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/index.html)
- [Queries, joins, paging, raw SQL, and transactions](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/queries.html)
- [Schema synchronization, migrations, backups, and retention](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/schema-migrations.html)
- [Caching, performance, resilience, and diagnostics](https://zyntal-com.github.io/CodeLogic.Libs/libs/mysql2/performance.html)
- [Generated API reference](https://zyntal-com.github.io/CodeLogic.Libs/api/CL.MySQL2.html)

## Requirements

- .NET 10
- CodeLogic 4.9+
- MySqlConnector 2.x
- MySQL 5.7+, MariaDB 10.3+, or a compatible Percona release

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
