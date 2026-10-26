# CL.MySQL2 — Query Builder

> Typed LINQ-shaped expressions translated to real SQL — filters, subqueries, ordering, paging, joins, projections, aggregates, bulk writes, raw SQL, and transactions.

See the [overview](index.md) for loading, repositories, configuration, and events.

`mysql.Query<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to SQL on the server side — there is no client-side filtering — and materializes rows with a compiled, reflection-free mapper.

```csharp
var mysql = Libraries.Get<MySQL2Library>();

Result<List<Order>> orders = await mysql.Query<Order>()
    .Where(o => o.Status == "open" && o.Total > 100)
    .OrderByDescending(o => o.CreatedUtc)
    .Take(50)
    .ToListAsync();

if (orders.IsSuccess)
    foreach (var o in orders.Value!) { /* … */ }
```

## Filtering with `Where`

`Where` takes an `Expression<Func<T, bool>>` and translates it to a parameterized `WHERE` clause. Chained calls are AND-combined.

```csharp
mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && o.Total < 1000)
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
```

Supported expression shapes include comparisons, `&&` / `||`, `!`, `string` methods (`Contains` / `StartsWith` / `EndsWith` → `LIKE`), `Contains` over a collection (→ `IN (...)`; an empty collection becomes `1 = 0`), and null checks (→ `IS NULL` / `IS NOT NULL`). Captured local variables and `DateTime.UtcNow`-relative expressions are parameterized.

## Subquery filters — `EXISTS` / `IN`

Four WHERE-family methods compile to real SQL subqueries against a different entity. They compose with ordinary `.Where(...)`.

```csharp
// Correlated EXISTS — correlate via the two-parameter predicate
mysql.Query<Order>()
    .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent");

mysql.Query<Order>()
    .WhereNotExists<Shipment>((o, s) => s.OrderId == o.Id);

// IN (subquery) — outer column matched against an inner column, with an optional inner filter
mysql.Query<Order>()
    .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip);

mysql.Query<Order>()
    .WhereNotIn<Customer, long>(o => o.CustomerId, c => c.Id);
```

- `WhereExists<TInner>` / `WhereNotExists<TInner>` → `[NOT] EXISTS (SELECT 1 FROM inner WHERE …)`.
- `WhereIn<TInner, TKey>` / `WhereNotIn<TInner, TKey>` → `col [NOT] IN (SELECT innerCol FROM inner [WHERE innerFilter])`.

> **Subquery-filtered queries are not cacheable** and cannot be turned into a typed `.Join` — the result cache stamps each entry with a single table's version counter, so it cannot invalidate on the inner table's mutations. `.WithCache` / `.SmartCache` are silently bypassed on these, including on a `.Select(...)` projection taken from such a query (the projection inherits the refusal, so `.WithCache` applied after `.Select` is ignored too). `WhereExists` against the outer query's own table is rejected (unqualified inner columns would be ambiguous).

## Ordering & paging

```csharp
mysql.Query<Order>()
    .OrderBy(o => o.CreatedUtc)
    .OrderByDescending(o => o.Total)
    .Skip(40)         // alias: Offset(40)
    .Take(20);        // alias: Limit(20)
```

`OrderBy` / `OrderByDescending` take a key selector. `Take`/`Limit` and `Skip`/`Offset` are aliases for `LIMIT` and `OFFSET`. For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<Order>> page = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);

PagedResult<Order> p = page.Value!;
// p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages, p.HasPreviousPage, p.HasNextPage
```

For large or frequently changing result sets, use forward-only cursor paging. It performs
keyset seeks and fetches one lookahead row, so it needs neither `OFFSET` nor `COUNT(*)`:

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

`CursorPagedResult<T>` carries `Items`, `PageSize`, `NextCursor`, and `HasNextPage`.
Cursor queries require an explicit ordering and a mapped primary key; the primary key is
automatically appended as a stable tie-breaker. Multiple and nullable ordering columns are
supported. Do not combine cursor paging with `Take`/`Skip`, joins, projections, or grouping.

Continuation tokens are versioned Base64URL-encoded JSON bound to the entity/table and exact
ordering. Treat them as opaque paging state, not as secrets: they are not encrypted or signed,
and they are not bound to the query's filters. Tokens longer than 4,096 encoded characters are
rejected before Base64 decoding or JSON deserialization.

## Joins

### Typed joins

`Join<TRight, TKey, TResult>` translates a strongly-typed equi-join to SQL with table aliases and a compiled projection into `TResult` — only the columns the selector touches are transferred. It returns a `JoinedQuery<TLeft, TRight, TResult>`.

```csharp
Result<List<OrderView>> views = await mysql.Query<Order>()
    .Where(o => o.Total > 100)                  // carried filters re-qualified to the left table
    .Join<Customer, long, OrderView>(
        o => o.CustomerId,                       // left key
        c => c.Id,                               // right key
        (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name },
        JoinType.Inner)
    .Where((o, c) => c.IsVip)                    // two-parameter filters on the joined shape
    .OrderByDescending((o, c) => o.Total)
    .Take(20)
    .ToListAsync();
```

- **`JoinType`**: `Inner` (default), `Left`, `Right`, `Cross`. A keyed join implies an equi-join, so `Cross` is rejected there.
- **Composite keys**: `o => new { o.A, o.B }` matched positionally with `c => new { c.X, c.Y }`.
- **`TRight` must be specified explicitly** — it cannot be inferred from a lambda parameter type.
- **Fluent surface on the join**: `.Where((l, r) => …)`, `.OrderBy` / `.OrderByDescending((l, r) => …)`, `.Take` / `.Skip` / `.Limit` / `.Offset`, and the `ToListAsync` / `FirstOrDefaultAsync` / `CountAsync` terminals.

> **Joined queries are not cacheable.** `.WithCache` / `.SmartCache` are intentionally absent on `JoinedQuery` rather than risk serving stale joins — the single-table version stamp cannot detect mutations on the other side.

### Raw-string joins

For ad-hoc joins outside the typed model, the string overload appends a literal join clause:

```csharp
mysql.Query<Order>()
    .Join("customers c", "c.id = orders.customer_id", JoinType.Left);
```

The base table is *not* aliased on a raw-string join — it appears under its own mapped name
(`orders` above), so qualify left-side columns with the table name. The `t0` / `t1` aliases
exist only inside a typed `Join<TRight, TKey, TResult>`.

## Projections — `Select`

`Select<TResult>` emits a real `SELECT col1, col2, …` column list (projection pushdown) and materializes into `TResult` — anonymous types or DTOs. It returns a `ProjectedQuery<TSource, TResult>`.

```csharp
Result<List<OrderSummary>> rows = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .Select(o => new OrderSummary { Id = o.Id, Total = o.Total })
    .WithCache(TimeSpan.FromSeconds(30))     // projections of a single table are cacheable
    .ToListAsync();
```

`ProjectedQuery` exposes `WithCache(ttl)`, `WithCache()` (the cache configuration's `DefaultTtlSeconds`), `SmartCache(pool)`, `ToListAsync`, and `FirstOrDefaultAsync`.

## Aggregates — `GroupBy`

`GroupBy<TKey>` returns a `GroupedQuery<TKey, TSource>`; its `Select` projects the grouping into a `ProjectedQuery` and translates to a real `GROUP BY` with aggregate functions — no client-side materialization.

```csharp
Result<List<DailyTotal>> daily = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.Day)
    .Select(g => new DailyTotal
    {
        Day      = g.Key,
        Count    = g.Count(),
        Revenue  = g.Sum(x => x.Total),
        AvgTotal = g.Average(x => x.Total),
        MaxTotal = g.Max(x => x.Total),
        MinTotal = g.Min(x => x.Total),
    })
    .ToListAsync();
```

Inside the projection use `g.Key`, `g.Sum(x => …)`, `g.Average(...)`, `g.Min(...)`, `g.Max(...)`, `g.Count()`, and `g.Any()`. The predicate overloads `g.Count(x => …)` and `g.Any(x => …)` are also translated, each as a `SUM(CASE WHEN … THEN 1 ELSE 0 END)` expression.

`SqlFn` exposes server-side functions for use inside a **grouped** query's key or projection:
`Year`, `Month`, `Day`, `Hour`, `Minute`, `DayOfWeek`, `Date`, `BucketUtc`, `Coalesce`,
`IfNull`, `Lower`, `Upper`, `Concat`, `Like`, `Round`, `Floor`, `Ceiling`. They are not
translated in an ungrouped `Select`, which supports plain column access only — that throws
`NotSupportedException` when the query is built. Calling one outside a query expression
throws `InvalidOperationException`; they are markers for the translator, not real methods.

```csharp
var perDay = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc >= since)
    .GroupBy(o => SqlFn.Date(o.CreatedUtc))
    .Select(g => new { Day = g.Key, Count = g.Count(), Revenue = g.Sum(o => o.Total) })
    .ToListAsync();
```

The translations target MySQL: `Year`/`Month`/`Day`/`Hour`/`Minute` become the matching
`YEAR(x)`-style calls, `Date(x)` becomes `DATE(x)`, `IfNull(a, b)` becomes `IFNULL(a, b)`,
and `BucketUtc(x, n)` floors a UNIX timestamp to an `n`-second window.

`DayOfWeek` is adjusted to match .NET: MySQL's `DAYOFWEEK` is 1–7 from Sunday, so the
translation subtracts one to give 0–6.

## Terminal operations

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT … LIMIT 1` |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)` |
| `ToCursorPagedListAsync(pageSize, ct)` | `Result<CursorPagedResult<T>>` | keyset page + one lookahead row |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)` |
| `AnyAsync(ct)` / `AnyAsync(predicate, ct)` | `Result<bool>` | `SELECT 1 … LIMIT 1` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)` |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)` |
| `AverageAsync<TResult>(selector, ct)` | `Result<double>` | `SELECT AVG(col)` |

```csharp
Result<long>    open  = await mysql.Query<Order>().Where(o => o.Status == "open").CountAsync();
Result<decimal> top   = await mysql.Query<Order>().MaxAsync(o => o.Total);
Result<decimal> total = await mysql.Query<Order>().Where(o => o.Day == today).SumAsync(o => o.Total);
Result<double>  avg   = await mysql.Query<Order>().AverageAsync(o => o.Total);

// Nullable selectors return null for SQL NULL (e.g. no matching rows) instead of default(T)
Result<DateTime?> last  = await mysql.Query<Order>().Where(o => o.UserId == id).MaxAsync(o => (DateTime?)o.CreatedUtc);
```

With a non-nullable selector, `MaxAsync` / `MinAsync` / `SumAsync` return `default(TResult)` (for example `DateTime.MinValue` or `0`) when the result is SQL NULL. Use a nullable cast in the selector when "no rows" must be distinguishable from a real value.

## Bulk update & delete

The builder runs set-based mutations server-side without materializing rows.

```csharp
// Bulk update via a LINQ set expression
Result<int> repriced = await mysql.Query<Order>()
    .Where(o => o.Status == "draft")
    .UpdateAsync(o => new Order { Status = "open", UpdatedUtc = DateTime.UtcNow });

// Bulk update via an explicit column map
Result<int> flagged = await mysql.Query<Order>()
    .Where(o => o.Total > 10000)
    .UpdateAsync(new Dictionary<string, object?> { ["needs_review"] = true });

// Bulk delete (hard delete regardless of [SoftDelete])
Result<int> purged = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();
```

A setter value can read the row it updates, including through `Math.Min` / `Math.Max`
(`LEAST` / `GREATEST`), so clamps and counters stay in SQL:

```csharp
await mysql.Query<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });
```

Captured values (`cap`, `refill`, strings, dates, enums) are always sent as parameters, also
inside such column expressions; only primitive numbers are written into the SQL text. MySQL
applies `SET` assignments left to right, so a value that reads a column assigned earlier in
the same setter sees its new value. `LEAST` / `GREATEST` return NULL when either argument is
NULL.

### Update and return the rows — `UpdateAndGetAsync`

`UpdateAndGetAsync` runs the same setter and returns the updated rows as they are after the
update, with no other writer in between:

```csharp
Result<List<Bucket>> r = await mysql.Query<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAndGetAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });

// Claim one job from a queue; concurrent workers skip rows another worker holds.
Result<List<Job>> claimed = await mysql.Query<Job>()
    .Where(j => j.Status == "queued")
    .OrderBy(j => j.Id).Take(1)
    .ForUpdate(LockWait.SkipLocked)
    .UpdateAndGetAsync(j => new Job { Status = "running", StartedUtc = now });
```

MySQL has no `UPDATE … RETURNING`, so it runs as one transaction: `SELECT <pk> … FOR UPDATE`,
then the `UPDATE` and a re-read by key in one batch. It uses the query's `TransactionScope`
when there is one and otherwise opens a short transaction of its own. The entity needs a
primary key; `Where`, `OrderBy`, `Take` and a `ForUpdate` wait mode are honoured.

> The query builder's bulk `UpdateAsync` / `DeleteAsync` stay raw — they do **not** apply soft-delete auto-filtering, so you can target or restore deleted rows. `QueryBuilder.DeleteAsync` is always a hard delete. Soft-delete behaviour applies only to single-table reads and `Repository.DeleteAsync`; see [Schema & Migrations](schema-migrations.md).

## Raw SQL escape hatches

When the builder can't express something, drop to parameterized raw SQL on the library. All three use named parameters, flow through observability, and inherit the transient-retry policy.

```csharp
// Materialize rows into T with the same compiled mapper as the builder
Result<List<UserRecord>> rows = await mysql.SqlQueryAsync<UserRecord>(
    "SELECT * FROM users WHERE country = @c AND created_utc >= @since",
    new Dictionary<string, object?> { ["@c"] = "DK", ["@since"] = since });

// Non-query — returns affected rows
Result<int> n = await mysql.ExecuteSqlAsync(
    "UPDATE users SET active = 0 WHERE last_seen < @cutoff",
    new Dictionary<string, object?> { ["@cutoff"] = cutoff });

// Single scalar value — T? on an unconstrained T is just T for a value type,
// so SqlScalarAsync<long> yields Result<long> (0 when there are no rows).
Result<long> max = await mysql.SqlScalarAsync<long>(
    "SELECT MAX(id) FROM users");
```

## Transactions

`BeginTransactionAsync` returns a `TransactionScope` (an `IAsyncDisposable`). Commit explicitly; if the scope is disposed without a commit it rolls back automatically.

Pass the scope to `GetRepository<T>` or `Query<T>` to enlist typed work in it; without it,
a repository or builder runs on its own connection and is **not** part of the transaction.

```csharp
await using TransactionScope tx = await mysql.BeginTransactionAsync();

await mysql.GetRepository<Account>(tx).AdjustAsync(1L, a => a.Balance, -100m);
await mysql.Query<Audit>(tx).Where(a => a.Stale).DeleteAsync();

await tx.CommitAsync();      // without this, disposal rolls back
```

> **The library-level raw SQL helpers do not enlist in a transaction scope.**
> `SqlQueryAsync` / `ExecuteSqlAsync` / `SqlScalarAsync` take a `connectionId`, not a
> `TransactionScope`, and each opens its own pooled connection. Calling one inside an
> `await using TransactionScope` block runs it *outside* the transaction, so a later
> `RollbackAsync` will not undo it.

The transactional surface is the typed one: `GetRepository<T>(tx)` and `Query<T>(tx)`,
including the query builder's bulk `UpdateAsync` / `DeleteAsync`. The scope's underlying
`MySqlConnection` and `MySqlTransaction` are internal, so there is no supported way to issue
a hand-written statement on them from application code. Where raw SQL genuinely has to be
transactional, write it as an `IMigration`: `IMigrationContext` exposes the live `Connection`
and `Transaction` along with `ExecuteAsync` / `QueryAsync` / `ScalarAsync` helpers that
already run on them.

### Row locking — `ForUpdate` / `ForShare`

Inside a transaction, `ForUpdate()` locks the rows a read returns until the transaction ends,
so a read-modify-write needs no retry loop. `ForShare()` takes a shared lock: others can still
read the rows but not change them.

```csharp
await using var tx = await mysql.BeginTransactionAsync();

var account = (await mysql.Query<Account>(tx)
    .Where(a => a.Id == id)
    .ForUpdate()
    .FirstOrDefaultAsync()).Value;

account!.Balance -= 100m;
await mysql.GetRepository<Account>(tx).UpdateAsync(account);
await tx.CommitAsync();
```

| Call | SQL |
|------|-----|
| `ForUpdate()` | `FOR UPDATE` |
| `ForUpdate(LockWait.NoWait)` | `FOR UPDATE NOWAIT` — fail at once instead of waiting |
| `ForUpdate(LockWait.SkipLocked)` | `FOR UPDATE SKIP LOCKED` — leave locked rows out (job queues) |
| `ForShare()` | `LOCK IN SHARE MODE` (MySQL and MariaDB) |
| `ForShare(LockWait.NoWait / SkipLocked)` | `FOR SHARE NOWAIT / SKIP LOCKED` (MySQL 8+) |

`SKIP LOCKED` and `NOWAIT` need MySQL 8.0+ or MariaDB 10.6+. A locking read only makes sense
inside a transaction, so it fails when the query was not created from a `TransactionScope`.
It is honoured by `ToListAsync`, `FirstOrDefaultAsync`, `AnyAsync`, `Select` and
`UpdateAndGetAsync`; the other terminals (counts, aggregates, paging, `GroupBy`, typed joins)
refuse it.

> Statements inside an explicit transaction scope are **never** transient-retried — the whole transaction is the caller's to retry. The result cache and smart-cache pools are also disabled inside a transaction. See [Performance & Caching](performance.md).

## Choosing a connection

Every entry point accepts a `connectionId` selecting a named database from `config.mysql.json`; it defaults to `"Default"`. On the builder, `.WithConnection("Reporting")` does the same fluently. (`GetRepository<T>` and `Query<T>` also have a `TransactionScope` overload, which takes its connection from the scope.)

```csharp
var reports = mysql.Query<Sale>().WithConnection("Reporting");
var repo    = mysql.GetRepository<Sale>("Reporting");
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
