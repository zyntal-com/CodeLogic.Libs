# Caching

One result cache (`DataCache`) serves every connection that uses it:
- query results, opted into per query
- an entity cache for key lookups
- warm queries that refresh themselves in the background
- cross-node invalidation

Every write through CL.Data invalidates the results it affects, so cached reads are never stale after a write made through CL.Data.

## Turning it on

With `DataLibrary`, the cache comes from `config.data.cache.json`:

```json
{
  "enabled": true,
  "maxEntries": 10000, "maxMemoryMb": 256, "defaultTtlSeconds": 60,
  "entityCache": true, "entityCacheTtlSeconds": 300,
  "invalidationBus": "Database", "busConnection": "Main", "busPollIntervalMs": 500,
  "publishEvents": false
}
```

**Per connection:** `"cacheEnabled": false` opts a connection out of the shared cache. `"cacheEnabled": true` gives it a cache even when the cache is off for everyone else.

**Without the runtime:** `new DatabaseOptions { Cache = new DataCache(new DataCacheConfig { EntityCache = true }) }`. One `DataCache` can be shared by several databases.

## Cached queries

```csharp
var active = await db.Query<Product>().WithCache().Where(p => p.Active).ToListAsync();              // default TTL
var top = await db.Query<Product>().WithCache(TimeSpan.FromMinutes(5)).OrderByDescending(p => p.Sales).Take(10).ToListAsync();
var count = await db.Query<Order>().WithCache().CountAsync();

var report = await db.Query<Order>().WithCache()
    .Join<Customer>((o, c) => o.CustomerId == c.Id)             // projections, groups and joins inherit the policy
    .Select((o, c) => new { c.Name, o.Total })
    .ToListAsync();
```

**Entity cache:** with `entityCache` on, `GetByIdAsync` and `GetByKeyAsync` answer repeated lookups from memory.

**How results are stored:**
- **Fresh objects:** results are kept as raw rows and materialized on every hit, so callers get fresh objects and can't change what's cached.
- **One load:** concurrent misses for the same query run it once.
- **Measured on MySQL:** a cached key lookup takes about **1.7 µs** against 1.4 ms from the database, and a cached 100-row list about **18 µs**.

## `CachePolicy`

```csharp
var policy = new CachePolicy
{
    Ttl = TimeSpan.FromMinutes(10),
    Sliding = false,                       // true: the lifetime restarts on every hit
    Tags = ["catalog"],                    // extra tags you can invalidate yourself
    Refresh = TimeSpan.FromMinutes(1),     // warm query: re-read in the background every minute
    IdleRefreshes = 3,                     // stop refreshing after 3 periods without a hit
};
var categories = await db.Query<Category>().WithCache(policy).ToListAsync();

db.Options.Cache!.Invalidate(["catalog"]);   // drop everything tagged "catalog"
```

**Warm queries:** a warm query never makes a caller wait for the database. `DataLibrary` runs the refresh loop. Without the runtime, call `cache.StartRefreshing(scheduler)`, or call `RefreshDueAsync()` from your own scheduler.

**Stale-while-refresh (`InvalidateOnWrite = false`):** for dashboards and leaderboards over tables that are written all
the time, a cold read after every write costs more than a few seconds of staleness. With `InvalidateOnWrite = false` a
write to the tables a result reads does not drop it: callers keep getting the current result, and the refresh loop reads
it again at its next tick (so the lag is about one tick, not one `Refresh` period). Without `Refresh` the result simply
lives until its `Ttl`. Invalidating one of the policy's own `Tags` still drops it.

```csharp
var board = new CachePolicy { Refresh = TimeSpan.FromSeconds(30), InvalidateOnWrite = false, Tags = ["leaderboard"] };
var top = await db.Query<Player>().OrderByDescending(p => p.Score).Take(50).WithCache(board).ToListAsync();

await db.Options.Cache!.RefreshAsync(["leaderboard"]);   // reload now, in place: readers never wait
```

`RefreshAsync(tags)` reloads warm and shielded results carrying the tags in place and drops the other results that carry
them. `WithCache` also works after `Select` (`q.Select(...).WithCache(policy)`).

## Invalidation

- **Automatic tags:** every cached result is tagged with each table it reads, including joined and sub-queried tables.
- **Entity writes, upserts, set-based updates and deletes, and bulk copy** drop the tags of the tables they touch.
- **Raw `ExecuteSqlAsync`** drops everything cached for that connection; `ExecuteSqlAsync(sql, tables)` drops only the results reading those tables. So does a raw query that runs as a write (`SqlIntent.Write`, or a statement `Auto` does not see as a plain read).
- **Keys:** a cached result is keyed by the connection, the SQL and every parameter value; array and list parameters are keyed element by element.
- **Transactions:** reads inside a transaction never use the cache. Invalidations wait for commit, and a rollback drops nothing.
- **Races:** a write that happens while a query is loading keeps that (possibly stale) result out of the cache.

**Writes CL.Data doesn't see** (other applications, manual SQL, triggers) aren't invalidated. Use a TTL that fits, or invalidate tags yourself.

## Several application nodes

Tags include the connection name, so every node must use the same connection names. `invalidationBus` picks how
invalidations travel:

| Bus | How | Use when |
|---|---|---|
| `None` | stays in this process | a single node |
| `Events` | `CacheInvalidatedEvent` on the CodeLogic event bus (`EventBusCacheBus`) | in-process, or the host bridges the event bus between nodes |
| `Database` | rows in a small `cl_data_cache_bus` table that every node polls (`DatabaseCacheBus`) | several nodes, with no extra infrastructure |

**Delivery:**
- Publishing never blocks or fails a write. If a publish fails, the other nodes fall back to their TTL.
- `DatabaseCacheBus` delivers within one poll interval, and it prunes old rows.
- In code, `cache.UseBus(new InProcessCacheBus("channel"))` links caches inside one process.

## Statistics

```csharp
var cache = db.Options.Cache!;
Console.WriteLine($"{cache.Hits} hits, {cache.Misses} misses, {cache.WarmQueries} warm, {cache.Statistics.Count} entries");
```

**Events and metrics:**
- With `publishEvents`, every cached read publishes `CacheHitEvent` or `CacheMissEvent`.
- The meters `cl.data.cache.hits` and `cl.data.cache.misses` always count (see [Telemetry](telemetry.md)).
