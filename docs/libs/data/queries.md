# Queries & writes

Every example runs unchanged on MySQL, PostgreSQL, SQL Server and SQLite. All calls are async, take a
`CancellationToken`, and return `Result`/`Result<T>`.

## Repositories

`db.Repository<T>()` handles key lookups and entity writes. Auditing, concurrency tokens, tenant stamping, soft delete
and cache invalidation are applied on every write path.

```csharp
var repo = db.Repository<Product>();

var p = (await repo.InsertAsync(new Product { Sku = "A-1", Price = 10m })).ValueOrThrow();   // Id filled in
await repo.InsertManyAsync(batch);                    // multi-row batches; bulk copy above BulkThreshold when nothing is read back
var one = (await repo.GetByIdAsync(p.Id)).ValueOrThrow();
var key = await repo.GetByKeyAsync(EntityKey.Of(orderId, lineNo));   // composite keys

p.Price = 12m;
await repo.UpdateAsync(p);                            // data.concurrency when someone else changed it ([ConcurrencyToken])
await repo.DeleteAsync(p);                            // soft delete when the entity has [SoftDelete]
await repo.HardDeleteAsync(p);

var page = (await repo.GetPagedAsync(page: 1, pageSize: 20)).ValueOrThrow();
var found = await repo.FindAsync(x => x.Sku.StartsWith("A-"));
```

**Upserts and counters:**

```csharp
await repo.UpsertAsync(product);                                  // insert or update by key (or UpsertOptions.ConflictProperties)
await repo.UpsertManyAsync(products);
await repo.UpsertWithIncrementsAsync(stat, [s => s.Hits]);        // insert, or add to the counters
var outcome = await repo.InsertIgnoreAsync(product);              // InsertOutcome.Inserted / Duplicate
await repo.IncrementAsync(p.Id, x => x.Stock, by: 5);             // atomic, no read
await repo.AdjustAsync(p.Id, x => x.Stock, -2);
```

**Changed-column updates:** only the columns that differ are written, and nothing is sent when nothing changed.

```csharp
var before = repo.Snapshot(p).ValueOrThrow();
p.Price = 15m;
await repo.UpdateAsync(p, before);                    // UPDATE ... SET Price = @p0 ... only
await repo.UpdateAsync(p, x => x.Stock = 0);          // change and save in one call
```

## Querying

```csharp
var q = db.Query<Product>()
    .Where(p => p.Price > min && skus.Contains(p.Sku))     // captured values become parameters
    .OrderBy(p => p.Status).ThenByDescending(p => p.Price)
    .Skip(20).Take(20);

var list = await q.ToListAsync();
var first = await q.FirstOrDefaultAsync();
var count = await q.CountAsync();
var any = await q.AnyAsync(p => p.Stock == 0);
var max = await q.MaxAsync(p => p.Price);                  // null over no rows; SumAsync returns 0
```

**Translation:**
- **Supported:** comparisons, arithmetic, `Contains` (strings and collections, including C# 14 span calls), `StartsWith`/`EndsWith`, null checks, `Math`, CASE expressions and the `SqlFn` functions (JSON, full-text and more).
- **Unsupported calls fail:** anything else is a build warning (CLD101–CLD103) and returns `data.translation_failed`. CL.Data never evaluates part of a query on the client.

**Other filters:** `WhereIn`/`WhereNotIn` (sub-queries or lists), `WhereExists`/`WhereNotExists`, `Exclude(p => p.Body)` (skip large columns), `IncludeDeleted()` and `IgnoreFilters()`.

## Projections, joins, grouping

```csharp
var rows = await db.Query<Product>()
    .Select(p => new { p.Sku, Gross = p.Price * 1.25m })
    .ToListAsync();

var lines = await db.Query<Order>()
    .Join<Customer>((o, c) => o.CustomerId == c.Id)               // LeftJoin, and up to 5 tables
    .Select((o, c) => new { o.Id, c.Name, o.Total })
    .ToListAsync();

var byStatus = await db.Query<Product>()
    .GroupBy(p => p.Status)
    .Having(g => g.Count() > 10)
    .Select(g => new { g.Key, Count = g.Count(), Avg = g.Average(p => p.Price) })
    .ToListAsync();
```

## Paging and streaming

```csharp
var page = (await q.ToPagedListAsync(page: 2, pageSize: 50)).ValueOrThrow();        // no COUNT unless withTotal: true
var withTotal = await q.ToPagedListAsync(2, 50, withTotal: true);                     // TotalCount, TotalPages

var cursor = (await q.OrderBy(p => p.Id).ToCursorPageAsync(pageSize: 100)).ValueOrThrow();
var next = await q.OrderBy(p => p.Id).ToCursorPageAsync(100, after: cursor.NextCursor);   // keyset paging

await foreach (var p in db.Query<Product>().AsAsyncEnumerable()) { /* one row at a time, one connection */ }
```

Streams bypass the result cache but otherwise run like any read: interceptors, telemetry, read routing (`ReadFrom`, replicas) and retries of the open all apply. A failure while rows are being read throws `DataQueryException`.

## Set-based writes

```csharp
await db.Query<Product>().Where(p => p.Stock == 0).UpdateAsync(p => new Product { Status = ProductStatus.SoldOut });
await db.Query<Session>().Where(s => s.ExpiresAt < now).DeleteAsync();

// Claim work from a queue: lock, update and return in one statement where the database allows it.
var job = await db.Query<Job>().Where(j => j.State == JobState.Pending).OrderBy(j => j.Id).Take(1)
    .ForUpdate(LockWait.SkipLocked)
    .UpdateAndGetAsync(j => new Job { State = JobState.Running });
```

Set-based updates refuse key and tenant columns (`data.validation`).

## Transactions, locks, batches

```csharp
var tx = (await db.BeginTransactionAsync()).ValueOrThrow();
await using (tx)
{
    await tx.Repository<Order>().InsertAsync(order);
    await tx.Query<Product>().Where(p => p.Id == id).ForUpdate().FirstOrDefaultAsync();
    (await tx.CommitAsync()).ThrowIfFailure();        // disposing without commit rolls back
}

// Retries the whole block on deadlock (maxAttempts).
await db.InTransactionAsync(async (tx, ct) => await Transfer(tx, from, to, amount, ct));

await using var lease = (await db.AcquireLockAsync("nightly-import", TimeSpan.FromSeconds(10))).ValueOrThrow();   // advisory lock

var batch = db.Batch();                                // several queries, one round trip
var products = batch.Add(db.Query<Product>().Take(10));
var orders = batch.Count(db.Query<Order>());
(await batch.ExecuteAsync()).ThrowIfFailure();
Console.WriteLine($"{products.Value.Count} products, {orders.Value} orders");
```

**Transaction rules:**
- **Inside a transaction:** reads skip the cache, nothing is retried, and everything runs on the primary.
- **Savepoints:** available on every provider.
- **Cache invalidations:** published on commit.

## Raw SQL

Interpolated values become parameters:

```csharp
var names = await db.SqlQueryAsync<string>($"SELECT Name FROM products WHERE Price > {min}");
var total = await db.SqlScalarAsync<decimal>($"SELECT SUM(Price) FROM products");
await db.ExecuteSqlAsync($"UPDATE products SET Stock = 0 WHERE Sku = {sku}");     // drops the connection's cached results
var rows = await db.SqlReadAsync($"SELECT Id, Name FROM products", r => (r.GetInt64(0), r.GetString(1)));

// Escape hatch: the raw driver connection (the transaction's, inside a transaction view).
var version = await db.WithConnectionAsync((connection, transaction, ct) => ValueTask.FromResult(connection.ServerVersion));
```

`SqlText.Raw(...)` is for trusted, pre-built text only.

## Compiled queries

A compiled query is planned once per database, and later calls only bind arguments: no expression trees, no translation,
no rendering.

```csharp
static readonly CompiledQuery<Product, decimal> Above =
    CompiledQuery.For<Product, decimal>((q, min) => q.Where(p => p.Price > min).OrderBy(p => p.Id).Take(100));

var hits = await Above.ToListAsync(db, 25m);
```

## Auditing, concurrency, tenancy, filters

- **Audit columns:** `[CreatedAt]`/`[UpdatedAt]` take UTC time from `DatabaseOptions.Time`. `[CreatedBy]`/`[UpdatedBy]` come from `DatabaseOptions.UserAccessor`. All four are stamped on inserts, updates, upserts, set-based updates and bulk copy.
- **Concurrency tokens:** a `[ConcurrencyToken]` integer is incremented on every update (SQL Server can use `rowversion`). A lost update returns `data.concurrency`.
- **Tenancy:** `[TenantKey]` entities are filtered and stamped automatically.
  - The tenant comes from `DatabaseOptions.TenantAccessor`, or from a view: `db.ForTenant(tenantId)`.
  - A missing tenant returns `data.tenant_missing`, for reads and for every write: insert, upsert, bulk copy, update, delete and counters. A tenant value set on the entity is not enough; write for a specific tenant through `db.ForTenant(id)`.
- **Global filters:** `DatabaseOptions.Filters` holds standing conditions, for example `DataFilter.For<Product>(p => p.Active)`. Opt out per query with `IgnoreFilters()`. Soft delete is one of these filters.
- **Interceptors:** `IDataInterceptor` runs around every command (`OnExecutingAsync`, `OnExecutedAsync`) and around every entity save (`OnSavingAsync`, `OnSavedAsync`).

## Bulk copy

```csharp
var receipt = (await db.Bulk<Product>().InsertAsync(rows)).ValueOrThrow();             // IEnumerable or IAsyncEnumerable
Console.WriteLine($"{receipt.Rows} rows in {receipt.Duration} (native: {receipt.Native})");
await db.Bulk<Product>().InsertAsync(StreamRowsAsync(), new BulkOptions { ChunkSize = 5_000 });
```

**What bulk copy does:**
- Values are stamped and converted exactly as an insert does, and a copy joins a transaction view.
- It does not read back server-generated identities, and it doesn't run entity save hooks or interceptors.
- See [Providers](providers.md) for each database's native path.
