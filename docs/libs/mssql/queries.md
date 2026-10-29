# CL.MSSQL — Query Builder

> Typed LINQ-shaped expressions translated to real SQL — filters, subqueries, ordering, paging, joins, projections, aggregates, bulk writes, raw SQL, and transactions.

See the [overview](index.md) for loading, repositories, configuration, and events.

`mssql.Query<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to SQL on the server side — there is no client-side filtering — and materializes rows with a compiled, reflection-free mapper.

```csharp
var mssql = Libraries.Get<MSSQLLibrary>();

Result<List<Order>> orders = await mssql.Query<Order>()
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
mssql.Query<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && o.Total < 1000)
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
```

Supported expression shapes include comparisons, `&&` / `||`, `!`, `string` methods (`Contains` / `StartsWith` / `EndsWith` → `LIKE`), `Contains` over a collection (→ `IN (...)`; an empty collection becomes `1 = 0`), and null checks (→ `IS NULL` / `IS NOT NULL`). Captured local variables and `DateTime.UtcNow`-relative expressions are parameterized.

## Subquery filters — `EXISTS` / `IN`

Four WHERE-family methods compile to real SQL subqueries against a different entity. They compose with ordinary `.Where(...)`.

```csharp
// Correlated EXISTS — correlate via the two-parameter predicate
mssql.Query<Order>()
    .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent");

mssql.Query<Order>()
    .WhereNotExists<Shipment>((o, s) => s.OrderId == o.Id);

// IN (subquery) — outer column matched against an inner column, with an optional inner filter
mssql.Query<Order>()
    .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip);

mssql.Query<Order>()
    .WhereNotIn<Customer, long>(o => o.CustomerId, c => c.Id);
```

- `WhereExists<TInner>` / `WhereNotExists<TInner>` → `[NOT] EXISTS (SELECT 1 FROM inner WHERE …)`.
- `WhereIn<TInner, TKey>` / `WhereNotIn<TInner, TKey>` → `col [NOT] IN (SELECT innerCol FROM inner [WHERE innerFilter])`.

> **Subquery-filtered queries are not cacheable** and cannot be turned into a typed `.Join` — the result cache stamps each entry with a single table's version counter, so it cannot invalidate on the inner table's mutations. `.WithCache` / `.SmartCache` are bypassed on these (with a logged warning), and the refusal carries through `.Select(...)` and `.GroupBy(...)` to the resulting `ProjectedQuery`, so `.WhereExists(...).Select(...).WithCache(ttl)` also executes uncached. `WhereExists` against the outer query's own table is rejected (unqualified inner columns would be ambiguous).

## Ordering & paging

```csharp
mssql.Query<Order>()
    .OrderBy(o => o.CreatedUtc)
    .OrderByDescending(o => o.Total)
    .Skip(40)         // alias: Offset(40)
    .Take(20);        // alias: Limit(20)
```

`OrderBy` / `OrderByDescending` take a key selector. `Take`/`Limit` and `Skip`/`Offset` are aliases for `TOP` and `OFFSET … FETCH`. For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<Order>> page = await mssql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);

PagedResult<Order> p = page.Value!;
// p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages, p.HasPreviousPage, p.HasNextPage
```

For large or frequently changing result sets, use forward-only cursor paging. It performs
keyset seeks and fetches one lookahead row, so it needs neither `OFFSET` nor `COUNT(*)`:

```csharp
Result<CursorPagedResult<Order>> first = await mssql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToCursorPagedListAsync(pageSize: 25);

Result<CursorPagedResult<Order>> next = await mssql.Query<Order>()
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
Result<List<OrderView>> views = await mssql.Query<Order>()
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
mssql.Query<Order>()
    .Join("customers c", "c.id = t0.customer_id", JoinType.Left);
```

## Projections — `Select`

`Select<TResult>` emits a real `SELECT col1, col2, …` column list (projection pushdown) and materializes into `TResult` — anonymous types or DTOs. It returns a `ProjectedQuery<TSource, TResult>`.

```csharp
Result<List<OrderSummary>> rows = await mssql.Query<Order>()
    .Where(o => o.Status == "open")
    .Select(o => new OrderSummary { Id = o.Id, Total = o.Total })
    .WithCache(TimeSpan.FromSeconds(30))     // projections of a single table are cacheable
    .ToListAsync();
```

`ProjectedQuery` exposes `WithCache(ttl)`, `WithCache()` (the configured `DefaultTtlSeconds`), `SmartCache(pool)`, `ToListAsync`, and `FirstOrDefaultAsync`.

### Leaving columns out — `Exclude`

When a reader needs the entity but not a few wide columns, `Exclude` reads every other mapped column instead of `*` (`OUTPUT INSERTED.<columns>` for `UpdateAndGetAsync`). The excluded properties keep their default value, and a column added to the entity later is still read.

```csharp
var rows = await mssql.Query<Document>().Where(d => d.OwnerId == id).Exclude(d => d.Body).ToListAsync();
```

It applies to `ToListAsync`, `FirstOrDefaultAsync`, `ToPagedListAsync`, `ToCursorPagedListAsync` and `UpdateAndGetAsync`. Excluding an unknown member or every column throws. **Don't write such an entity back** with `Repository.UpdateAsync(entity)`; the excluded columns would be overwritten with defaults.

## Aggregates — `GroupBy`

`GroupBy<TKey>` returns a `GroupedQuery<TKey, TSource>`; its `Select` projects the grouping into a `ProjectedQuery` and translates to a real `GROUP BY` with aggregate functions — no client-side materialization.

```csharp
Result<List<DailyTotal>> daily = await mssql.Query<Order>()
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

Inside the projection use `g.Key`, `g.Sum(x => …)`, `g.Average(...)`, `g.Min(...)`, `g.Max(...)`, `g.Count()`, and `g.Any()`.

`SqlFn` exposes server-side functions for use inside a **grouped** query's key or projection:
`Year`, `Month`, `Day`, `Hour`, `Minute`, `DayOfWeek`, `Date`, `BucketUtc`, `Coalesce`,
`IfNull`, `Lower`, `Upper`, `Concat`, `Like`, `Round`, `Floor`, `Ceiling`. They are not
translated in an ungrouped `Select`, which supports plain column access only — that throws
`NotSupportedException` when the query is built. Calling one outside a query expression
throws `InvalidOperationException`; they are markers for the translator, not real methods.

```csharp
var perDay = await mssql.Query<Order>()
    .Where(o => o.CreatedUtc >= since)
    .GroupBy(o => SqlFn.Date(o.CreatedUtc))
    .Select(g => new { Day = g.Key, Count = g.Count(), Revenue = g.Sum(o => o.Total) })
    .ToListAsync();
```

The translations target T-SQL: `Year`/`Month`/`Day`/`Hour`/`Minute` become
`DATEPART(part, x)`, `Date(x)` becomes `CONVERT(date, x)`, `IfNull(a, b)` becomes
`COALESCE(a, b)`, and `BucketUtc(x, n)` floors a UNIX timestamp to an `n`-second window
with `DATEDIFF_BIG`/`DATEADD`.

`DayOfWeek` counts days from a known Sunday rather than using `DATEPART(weekday, …)`,
whose result would otherwise shift with the session's `SET DATEFIRST`. It always returns
0–6 from Sunday, matching .NET's `DayOfWeek`.

`Like` returns a `bit`: T-SQL has no boolean expression type, so the predicate is wrapped
in `CAST(CASE WHEN … THEN 1 ELSE 0 END AS bit)` to be legal in a key or projection.

## Terminal operations

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT TOP (1) …` |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)` |
| `ToCursorPagedListAsync(pageSize, ct)` | `Result<CursorPagedResult<T>>` | keyset page + one lookahead row |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)` (soft-delete filtered unless `IncludeDeleted()`) |
| `AnyAsync(ct)` / `AnyAsync(predicate, ct)` | `Result<bool>` | `SELECT TOP (1) 1 …` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)` |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)` |
| `AverageAsync<TResult>(selector, ct)` | `Result<double>` | `SELECT AVG(col)` |

```csharp
Result<long>    open  = await mssql.Query<Order>().Where(o => o.Status == "open").CountAsync();
Result<decimal> top   = await mssql.Query<Order>().MaxAsync(o => o.Total);
Result<decimal> total = await mssql.Query<Order>().Where(o => o.Day == today).SumAsync(o => o.Total);
Result<double>  avg   = await mssql.Query<Order>().AverageAsync(o => o.Total);

// Nullable selectors return null for SQL NULL (e.g. no matching rows) instead of default(T)
Result<DateTime?> last  = await mssql.Query<Order>().Where(o => o.UserId == id).MaxAsync(o => (DateTime?)o.CreatedUtc);
```

With a non-nullable selector, `MaxAsync` / `MinAsync` / `SumAsync` return `default(TResult)` (for example `DateTime.MinValue` or `0`) when the result is SQL NULL.

## Bulk update & delete

The builder runs set-based mutations server-side without materializing rows.

```csharp
// Bulk update via a LINQ set expression
Result<int> repriced = await mssql.Query<Order>()
    .Where(o => o.Status == "draft")
    .UpdateAsync(o => new Order { Status = "open", UpdatedUtc = DateTime.UtcNow });

// Bulk update via an explicit column map
Result<int> flagged = await mssql.Query<Order>()
    .Where(o => o.Total > 10000)
    .UpdateAsync(new Dictionary<string, object?> { ["needs_review"] = true });

// Bulk delete (hard delete regardless of [SoftDelete])
Result<int> purged = await mssql.Query<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();
```

A setter value can read the row it updates, including through `Math.Min` / `Math.Max`, so
clamps and counters stay in SQL:

```csharp
await mssql.Query<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });
```

Captured values (`cap`, `refill`, strings, dates, enums) are always sent as parameters, also
inside such column expressions; only primitive numbers are written into the SQL text. SQL
Server evaluates every `SET` assignment against the row as it was before the update, so a
value that reads a column assigned in the same setter sees its old value, whatever the order.
`LEAST` / `GREATEST` need SQL Server 2022, so `Math.Min` / `Math.Max` are written as
`CASE WHEN a <= b THEN a ELSE b END`; unlike the .NET methods, a NULL first argument yields the
second.

### Update and return the rows — `UpdateAndGetAsync`

`UpdateAndGetAsync` runs the same setter and returns the updated rows as they are after the
update, with no other writer in between:

```csharp
Result<List<Bucket>> r = await mssql.Query<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAndGetAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });

// Claim one job from a queue; concurrent workers skip rows another worker holds.
Result<List<Job>> claimed = await mssql.Query<Job>()
    .Where(j => j.Status == "queued")
    .OrderBy(j => j.Id).Take(1)
    .ForUpdate(LockWait.SkipLocked)
    .UpdateAndGetAsync(j => new Job { Status = "running", StartedUtc = now });
```

It is a single statement, `UPDATE … SET … OUTPUT INSERTED.* WHERE …`, so it is atomic by
itself and runs on the query's `TransactionScope` when there is one. With `Take` it updates
through a CTE: `WITH [__target] AS (SELECT TOP (n) * FROM … WITH (UPDLOCK, ROWLOCK) WHERE …
ORDER BY …) UPDATE [__target] SET … OUTPUT INSERTED.*`. `OUTPUT` carries no order, so the
returned rows are sorted in memory by the `OrderBy` keys. No primary key is needed; `Where`,
`OrderBy`, `Take` and a `ForUpdate` wait mode (`NOWAIT` / `READPAST`) are honoured, and the
lock is always exclusive, even after `ForShare()`.

> SQL Server rejects `OUTPUT` without `INTO` on a table that has an enabled trigger, so
> `UpdateAndGetAsync` fails on such tables.

### Insert or update from the current row — expression `UpsertAsync`

`Repository.UpsertAsync(entity, onConflict)` inserts the entity, or — when it collides with an
existing row on the primary key or a UNIQUE column / index — updates that row with a setter
that receives the existing row (`current`) and the row that failed to insert (`incoming`):

```csharp
Result<int> r = await mssql.GetRepository<LoginThrottle>().UpsertAsync(
    new LoginThrottle { Ip = ip, Failures = 1, LastUtc = now },
    (current, incoming) => new LoginThrottle
    {
        Failures = current.Failures + 1,
        LastUtc  = incoming.LastUtc,
    });
```

It runs the same locked batch as `UpsertAsync(entity)` (a table-variable source matched with
`WITH (UPDLOCK, HOLDLOCK)` under `SERIALIZABLE`, no `MERGE`), with `current.Col` written as the
target row's column and `incoming.Col` as the source row's. Columns the setter does not assign
keep their current value; captured values are parameters. `incoming` only carries inserted
columns, so `incoming.Id` on an IDENTITY (or `rowversion`) column throws `ArgumentException` —
use `current.Id`. As with bulk updates, every assignment sees the row as it was before the
update. The result is 1 for an insert and 1 for an update.

> The query builder's bulk `UpdateAsync` / `DeleteAsync` stay raw — they do **not** apply soft-delete auto-filtering, so you can target or restore deleted rows. `QueryBuilder.DeleteAsync` is always a hard delete. Soft-delete behaviour applies only to single-table reads and `Repository.DeleteAsync`; see [Schema & Migrations](schema-migrations.md).

## Raw SQL escape hatches

When the builder can't express something, drop to parameterized raw SQL on the library. All three use named parameters, flow through observability, and inherit the transient-retry policy.

```csharp
// Materialize rows into T with the same compiled mapper as the builder
Result<List<UserRecord>> rows = await mssql.SqlQueryAsync<UserRecord>(
    "SELECT * FROM users WHERE country = @c AND created_utc >= @since",
    new Dictionary<string, object?> { ["@c"] = "DK", ["@since"] = since });

// Non-query — returns affected rows
Result<int> n = await mssql.ExecuteSqlAsync(
    "UPDATE users SET active = 0 WHERE last_seen < @cutoff",
    new Dictionary<string, object?> { ["@cutoff"] = cutoff });

// Single scalar value
Result<long?> max = await mssql.SqlScalarAsync<long>(
    "SELECT MAX(id) FROM users");
```

## Transactions

`BeginTransactionAsync` returns a `TransactionScope` (an `IAsyncDisposable`). Commit explicitly; if the scope is disposed without a commit it rolls back automatically.

Pass the scope to `GetRepository<T>` or `Query<T>` to enlist typed work in it; without it,
a repository or builder runs on its own connection and is **not** part of the transaction.

```csharp
await using TransactionScope tx = await mssql.BeginTransactionAsync();

await mssql.GetRepository<Account>(tx).AdjustAsync(1L, a => a.Balance, -100m);
await mssql.Query<Audit>(tx).Where(a => a.Stale).DeleteAsync();

await tx.CommitAsync();      // without this, disposal rolls back
```

> **Raw SQL cannot join a transaction scope.** `SqlQueryAsync` / `ExecuteSqlAsync` /
> `SqlScalarAsync` take a `connectionId`, not a `TransactionScope`, and always open their own
> connection — so calling them while a scope is open runs them *outside* that transaction
> (and they may block on the locks it holds). Keep transactional work on the repository and
> the query builder, both of which accept the scope. A raw statement that must be
> transactional belongs in an `IMigration`, whose `IMigrationContext` exposes the runner's
> `Connection` and `Transaction` directly.

### Row locking — `ForUpdate` / `ForShare`

Inside a transaction, `ForUpdate()` locks the rows a read returns until the transaction ends,
so a read-modify-write needs no retry loop. `ForShare()` takes a shared lock: others can still
read the rows but not change them. SQL Server has no `FOR UPDATE` clause on a `SELECT`; the
lock is a table hint placed right after the table.

```csharp
await using TransactionScope tx = await mssql.BeginTransactionAsync();

var account = (await mssql.Query<Account>(tx)
    .Where(a => a.Id == id)
    .ForUpdate()
    .FirstOrDefaultAsync()).Value;

account!.Balance -= 100m;
await mssql.GetRepository<Account>(tx).UpdateAsync(account);
await tx.CommitAsync();
```

| Call | Table hint |
|------|-----|
| `ForUpdate()` | `WITH (UPDLOCK, ROWLOCK)` |
| `ForUpdate(LockWait.NoWait)` | `WITH (UPDLOCK, ROWLOCK, NOWAIT)` — fail at once (error 1222) instead of waiting |
| `ForUpdate(LockWait.SkipLocked)` | `WITH (UPDLOCK, ROWLOCK, READPAST)` — leave locked rows out (job queues) |
| `ForShare()` | `WITH (HOLDLOCK, ROWLOCK)` — serializable: key ranges are locked too, so no new matching row can appear |
| `ForShare(LockWait.NoWait)` | `WITH (HOLDLOCK, ROWLOCK, NOWAIT)` |
| `ForShare(LockWait.SkipLocked)` | `WITH (REPEATABLEREAD, ROWLOCK, READPAST)` — `READPAST` is not allowed with `HOLDLOCK`, so only the rows read stay locked, not the ranges between them |

A locking read only makes sense inside a transaction, so it fails when the query was not
created from a `TransactionScope`. It is honoured by `ToListAsync`, `FirstOrDefaultAsync`,
`AnyAsync` and `Select`; the other terminals (counts, aggregates, paging, `GroupBy`, typed
joins, bulk `UpdateAsync` / `DeleteAsync`) refuse it. `UpdateAndGetAsync` is a single
statement, so it takes the wait mode with or without a transaction.

> Statements inside an explicit transaction scope are **never** transient-retried — the whole transaction is the caller's to retry. The result cache and smart-cache pools are also disabled inside a transaction. See [Performance & Caching](performance.md).

## Choosing a connection

Every entry point accepts a `connectionId` selecting a named database from `config.mssql.json`; it defaults to `"Default"`. On the builder, `.WithConnection("Reporting")` does the same fluently.

```csharp
var reports = mssql.Query<Sale>().WithConnection("Reporting");
var repo    = mssql.GetRepository<Sale>("Reporting");
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MSSQL)
