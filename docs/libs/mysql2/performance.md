# CL.MySQL2 — Performance & Caching

> A self-invalidating result cache, warm smart-cache pools, multi-node coordination, transient retries, and the diagnostics that surface slow queries.

See the [overview](index.md) for loading, repositories, configuration, and events.

CL.MySQL2 is built to be fast by default: reflection runs once per entity, projections transfer only the columns you select, and read results cache with invalidation that costs nothing. This page covers the caching model and the resilience and observability features around it.

## Result cache

`.WithCache(ttl)` caches a single-table query's result for the given TTL; the parameterless
`.WithCache()` uses the cache configuration's `DefaultTtlSeconds` (60s by default). A
per-database `CacheEnabledOverride` wins over the global `Enabled` switch for queries on that
connection, and `PublishEvents` controls whether hits and misses raise events. The cache is a cache-aside read path keyed on the translated SQL plus its parameters; a failure `Result` is never cached.

```csharp
Result<List<Server>> servers = await mysql.Query<Server>()
    .Where(s => s.Region == "eu")
    .OrderBy(s => s.Name)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();
```

The cache reads `Enabled`, `MaxEntries`, `TimeQuantizeSeconds`, `DefaultTtlSeconds`, and `PublishEvents` from `config.mysql.cache.json` at startup via `QueryCache.Configure(...)`, and registers each database's `CacheEnabledOverride` with `QueryCache.SetConnectionOverride(...)`. `MaxMemoryMb` is `[Obsolete]` and ignored — the in-process store evicts by entry count, so use `MaxEntries`. The public surface of the static `QueryCache` facade is `Configure`, `SetConnectionOverride`, `UseStore`, `UseCoordinator`, `Count`, `Invalidate`, `GetStats`, and `Clear`; `Enabled`, `DefaultTtl`, `PublishEvents`, and `TimeQuantizeSeconds` are internal.

> Caching is available on single-table `QueryBuilder<T>` reads and on `ProjectedQuery` (single-table `Select`). It is **not** available on joined queries or subquery-filtered (`WhereExists` / `WhereIn`) queries — those stamp a single table's version and could not be invalidated when the other table mutates. It is also disabled inside a transaction scope.

### Table-version invalidation

Every cacheable table carries a version counter that is mixed into the cache key. Any mutation through the library bumps that counter, so all existing entries for the table instantly become un-hittable — there is no key tracking and no eviction sweep on the hot path. Old entries fall out by TTL or LRU later.

This makes invalidation free: you never call an `Invalidate(...)` yourself for ordinary CRUD; an `InsertAsync` / `UpdateAsync` / `DeleteAsync` / bulk write bumps the version as a side effect.

### Time quantization

A naive `Where(x => x.At >= DateTime.UtcNow.AddDays(-30))` would produce a unique cache key on every call because `UtcNow` changes each tick. CL.MySQL2 quantizes `DateTime` parameters that fall within 365 days of now to the nearest `TimeQuantizeSeconds` bucket (default 60s), so the same rolling-window query reuses one cache entry for the duration of the bucket.

```csharp
// Cacheable: the AddDays(-30) bound is quantized to a 60s bucket
await mysql.Query<Event>()
    .Where(e => e.At >= DateTime.UtcNow.AddDays(-30))
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();
```

### Cache stampede protection

Concurrent misses on the same cold key collapse to a single factory execution (single-flight) instead of a thundering herd of identical DB queries. This is transparent — no API change.

## Smart cache pools

A `SmartCachePool` is a named group of cached queries kept warm by a background timer. Reads after the first never block on the DB — the pool re-runs each registered query in the background and overwrites the entry.

```csharp
// Register a pool that refreshes every 30s, optionally warming it immediately
SmartCachePool pool = mysql.RegisterCachePool(
    name: "dashboard",
    refreshEvery: TimeSpan.FromSeconds(30),
    maxIdleFires: 10,
    warmUp: async () =>
    {
        await mysql.Query<Server>().SmartCache("dashboard").ToListAsync();
        await mysql.Query<Player>().SmartCache("dashboard").CountAsync();
    });

// Opt a query into the pool
Result<List<Server>> servers = await mysql.Query<Server>()
    .Where(s => s.Online)
    .SmartCache("dashboard")
    .ToListAsync();

// Force an out-of-schedule refresh (e.g. right after a deploy)
await mysql.RefreshCachePoolAsync("dashboard");
```

- `maxIdleFires` (default 10): an entry not read for that many consecutive ticks is dropped from the refresh list, bounding cardinality on parameterized queries.
- Smart cache is mutually exclusive with `.WithCache(ttl)` — if both are set the pool wins. An unknown pool name on `.SmartCache(name)` logs a warning and falls back to non-cached execution (no exception).
- Like `.WithCache`, smart cache is disabled inside a transaction scope.

`ProjectedQuery` also exposes `.SmartCache(pool)`.

### Pool & cache diagnostics

```csharp
IReadOnlyList<SmartCachePoolStats> pools = mysql.GetCachePoolStats();   // per-pool: entries, ticks, failures, last tick
QueryCacheStats stats = mysql.GetCacheStats();                          // totals, entries by table, table versions
```

## Multi-node coordination

By default the table-version counter is per-process, which is correct for a single node. For a cluster, plug in an `ICacheStore` (shared store such as Redis) and an `ICacheCoordinator` (cross-node invalidation seam):

```csharp
QueryCache.UseStore(myRedisStore);
QueryCache.UseCoordinator(myCoordinator);
```

- **`ICacheStore`** — the pluggable backing store for cache entries.
- **`ICacheCoordinator`** — `PublishInvalidationAsync` (a local mutation fans out so peers bump their version counters and evict matching entries without re-broadcasting), `OnInvalidation` (receive peers' broadcasts), and `TryAcquireRefreshLeaseAsync` (single-flight pool refresh — only the lease holder hits the DB; idle-entry retirement still runs on every node).
- The default `NullCacheCoordinator` is single-node: no fan-out, always grants the lease — identical behaviour off-cluster.

Pair a coordinator with a shared `ICacheStore` so non-leader nodes read the entry the leader writes.

## Transient-error retry

Single non-transactional statements that fail with a deadlock (`1213`) or lock-wait timeout (`1205`) are auto-retried with exponential backoff plus jitter.

| Setting | Default | Purpose |
|---------|---------|---------|
| `TransientRetryCount` | `3` | Attempts (0 disables). |
| `TransientRetryBaseDelayMs` | `50` | Base backoff; grows exponentially with jitter. |

> Statements inside an explicit transaction scope are **never** auto-retried — the whole transaction is the caller's to retry, since an inner statement can't be replayed in isolation.

## N+1 detection

Set the per-database `N1DetectorThreshold` above 0 and every executed statement is folded into
a normalized template (parameters and numeric literals collapse to `?`). When one template runs
that many times on the same connection inside a **one-second rolling window**, an
`N1QueryDetectedEvent` is published carrying the connection id, the template and the count.

- `0` — the default — disables detection entirely, and costs nothing on the query path: no
  allocation and no bookkeeping.
- The event fires **once per window**, not on every further execution.
- Tracking is bounded (a fixed number of templates, pruned by age), so a long-running process
  cannot accumulate state here.

```csharp
events.Subscribe<N1QueryDetectedEvent>(e =>
    logger.Warn($"N+1: {e.Count} x {e.QueryTemplate}"));
```

The shape it catches — a loop issuing one query per row — is usually fixed with a single
`WhereIn` / join instead of the loop; see [Query Builder](queries.md).

## Slow-query reporting

Queries slower than `SlowQueryThresholdMs` (default 1000ms) are logged at warning level and
publish a `SlowQueryEvent` carrying the connection id, the SQL, and the elapsed milliseconds.

Turn on the per-database `CaptureExplainOnSlowQuery` (default off) and a slow query also runs
`EXPLAIN FORMAT=JSON` for the same statement and parameters, attaching the plan to the event's
`ExplainJson`. Capture is strictly best-effort and deliberately out of the caller's way:

- it runs on a **separate pooled connection**, never inside the caller's transaction, and never
  on the caller's thread;
- statements MySQL cannot explain — DDL, and multi-statement batches such as the repository's
  `INSERT ...; SELECT LAST_INSERT_ID();` — are skipped silently;
- any failure is swallowed. The `SlowQueryEvent` always publishes, with `ExplainJson` null when
  no plan could be obtained.

```csharp
// Subscribe on the CodeLogic event bus
events.Subscribe<SlowQueryEvent>(e =>
{
    logger.Warn($"Slow query {e.ElapsedMs:n0}ms: {e.Query}");
});
```

Every query also publishes a `QueryExecutedEvent` carrying a `CacheHit` flag, so you can measure cache effectiveness in aggregate.

## Compiled materializers & projection pushdown

- **Compiled materializers** — reflection runs once per entity at first use to build `EntityMetadata<T>` and a compiled reader-to-entity function. Subsequent reads map rows with no per-row reflection.
- **Projection pushdown** — `.Select(...)` emits a real `SELECT col1, col2, …` column list rather than `SELECT *`, transferring only the columns referenced. Combined with compiled mapping this often cuts row-transfer bandwidth substantially. To keep the entity type and drop only a few wide columns, use `.Exclude(...)` (see [Queries](queries.md#leaving-columns-out--exclude)).

Both apply automatically to the builder, projections, joins, and raw `SqlQueryAsync<T>`.

## Batch sizes & limits

`config.mysql.json` declares several per-database throughput knobs:

| Setting | Default | Status |
|---------|---------|--------|
| `CommandTimeout` | `30` | **Applied** — written to the connection string as `DefaultCommandTimeout` (seconds). |
| `ConnectionTimeout` | `30` | **Applied** — connection-string `ConnectionTimeout` (seconds). |
| `MinPoolSize` / `MaxPoolSize` | `1` / `100` | **Applied** — connection-string pool bounds. |
| `MaxBatchInsertSize` | `500` | **Applied** — `GetRepository<T>` passes it to `Repository<T>`, which chunks `InsertManyAsync` / `UpsertManyAsync` at that many rows per statement. |
| `MaxInClauseValues` | `1000` | **Applied as a warning only** — a generated `IN (...)` list larger than this logs one warning per query build naming the entity and the count. Nothing is chunked or rejected, so no existing query changes its result. |
| `PreparedStatementCacheSize` | `256` | **Obsolete and ignored** — statement caching is a MySqlConnector connection-string concern (`IgnorePrepare=false`). |
| `QueryTimeoutMs` | `30000` | **Applied** — set as `CommandTimeout` (rounded up to whole seconds) on the commands the repository, query builder, projections, joins and the raw-SQL helpers create. 0 falls back to the connection string's `CommandTimeout`; the 30000ms default equals that 30s default. |

The chunk size remains a private field on `Repository<T>` set from a constructor parameter —
there is no public property for it.

For large inserts, prefer `InsertManyAsync` (chunked) over a loop of `InsertAsync` — fewer round trips, and each chunk is eligible for transient retry.

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
