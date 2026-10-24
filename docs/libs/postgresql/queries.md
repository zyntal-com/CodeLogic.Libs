# CL.PostgreSQL — Query Builder

`pg.Query<T>()` returns a `QueryBuilder<T>`: a chainable, immutable-in-spirit builder that
translates LINQ-shaped expressions into parameterised PostgreSQL. Values are always bound as
parameters; identifiers always come from entity metadata, never from caller strings.

Every terminal returns `Result<T>`.

## Filtering

```csharp
var rows = await pg.Query<User>()
    .Where(u => u.IsActive && u.CreatedUtc >= DateTime.UtcNow.AddDays(-30))
    .Where(u => u.Email != null)          // additional Where calls AND together
    .ToListAsync();
```

Supported in a predicate:

| C# | SQL |
|---|---|
| `==`, `!=`, `<`, `<=`, `>`, `>=` | the same operators |
| `&&`, `\|\|`, `!` | `AND`, `OR`, `NOT` |
| `x.Prop == null` | `IS NULL` (either operand order) |
| `x.Name.Contains("a")` | `LIKE` with the term's metacharacters escaped |
| `x.Name.StartsWith` / `EndsWith` | anchored `LIKE` |
| `string.IsNullOrEmpty(x.Name)` | `(col IS NULL OR col = '')` |
| `ids.Contains(x.Id)` | `IN (…)`, one bound parameter per value (no chunking) |

An empty collection in `Contains` produces `FALSE` rather than the invalid `IN ()`.

### Subquery filters

```csharp
var withOrders = await pg.Query<User>()
    .WhereExists<Order>((u, o) => o.UserId == u.Id)
    .ToListAsync();

var inRegion = await pg.Query<User>()
    .WhereIn<Region, long>(u => u.RegionId, r => r.Id, r => r.IsActive)
    .ToListAsync();
```

`WhereNotExists` and `WhereNotIn` are the negations.

A query carrying a subquery filter is **not cacheable** and cannot be turned into a typed
`.Join`: the result cache stamps an entry with a single table's version counter, so a
mutation on the inner table could not invalidate it. The refusal travels with the query, so
`.WhereExists(…).Select(…).WithCache(…)` and `.WhereExists(…).GroupBy(…)` are uncached too —
the `.WithCache` is ignored (and logged) rather than serving a stale cross-table result.

## Ordering, paging, projection

```csharp
var page = await pg.Query<User>()
    .Where(u => u.IsActive)
    .OrderByDescending(u => u.CreatedUtc)
    .Skip(40).Take(20)                    // aliases for Offset / Limit
    .ToListAsync();

var summary = await pg.Query<User>()
    .Select(u => new { u.Id, u.Email })   // only these columns are read
    .ToListAsync();
```

`Select` is a real projection: the generated `SELECT` lists just those columns, and the
result is materialized by a compiled mapper, not reflection.

### Offset paging

```csharp
var paged = await pg.Query<User>().OrderBy(u => u.Id).ToPagedListAsync(page: 3, pageSize: 25);
// paged.Value.Items / TotalItems / PageNumber / PageSize / TotalPages
```

The repository has its own `GetPagedAsync(page, pageSize)` for the unfiltered case.

### Cursor paging

Offset paging drifts when rows are inserted between requests, and gets slower the deeper you
go. Cursor paging seeks instead:

```csharp
var first = await pg.Query<User>()
    .OrderByDescending(u => u.CreatedUtc)
    .ToCursorPagedListAsync(pageSize: 50);

var next = await pg.Query<User>()
    .OrderByDescending(u => u.CreatedUtc)
    .After(first.Value.NextCursor)
    .ToCursorPagedListAsync(pageSize: 50);
```

The ordering must be declared, and the primary key is appended automatically to make it
total. The cursor is a base64url token carrying the typed ordering values; decoding validates
it against the entity, table and the exact ordering of the query issuing it, so a token from
one query cannot be replayed against another. It is **not** signed — treat a cursor as a
position, not as an authorisation.

The seek predicate uses `IS NOT DISTINCT FROM` for the equality legs, so nullable ordering
columns page correctly.

## Joins

```csharp
var rows = await pg.Query<User>()
    .Join<Order, long, UserOrder>(
        u => u.Id,                      // left key
        o => o.UserId,                  // right key
        (u, o) => new UserOrder { Email = u.Email, Total = o.Total })
    .Where((u, o) => o.Total > 100)
    .OrderByDescending((u, o) => o.Total)
    .Take(50)
    .ToListAsync();
```

Both sides are schema-qualified from their `[Table]` attributes. A raw `Join(table, condition)`
overload exists for shapes the typed form cannot express; its table name is quoted (and may be
`schema.table`) but the condition is passed through verbatim, so do not build it from user input.

## Grouping & aggregates

```csharp
var perDay = await pg.Query<Order>()
    .Where(o => o.CreatedUtc >= since)
    .GroupBy(o => SqlFn.Date(o.CreatedUtc))
    .Select(g => new { Day = g.Key, Count = g.Count(), Revenue = g.Sum(o => o.Total) })
    .ToListAsync();
```

Scalar terminals on the query builder: `CountAsync`, `SumAsync`, `MinAsync`, `MaxAsync`,
`AverageAsync`. (`Count` / `Any` are available *inside* a grouped projection as `g.Count()`
and `g.Any()`; there is no `AnyAsync` terminal.)

```csharp
// Nullable selectors return null for SQL NULL (e.g. no matching rows) instead of default(T)
Result<DateTime?> last = await pg.Query<Order>().Where(o => o.UserId == id).MaxAsync(o => (DateTime?)o.CreatedUtc);
```

With a non-nullable selector, `MaxAsync` / `MinAsync` / `SumAsync` return `default(TResult)` (for example `DateTime.MinValue` or `0`) when the result is SQL NULL.

`SqlFn` exposes server-side functions for use inside a **grouped** query's key or projection.
They are not translated in an ungrouped `Select`, which supports plain column access only —
that throws `NotSupportedException` when the query is built.

The translations target PostgreSQL, not MySQL. The date parts are also normalised to UTC
first, so they do not swing with the server's session `TimeZone`:
`Year`/`Month`/`Day`/`Hour`/`Minute`/`DayOfWeek` become
`EXTRACT(… FROM (x) AT TIME ZONE 'UTC')::int` and `Date(x)` becomes
`((x) AT TIME ZONE 'UTC')::date`. `IfNull(a, b)` becomes `COALESCE(a, b)`, `BucketUtc(x, n)`
becomes `to_timestamp(floor(EXTRACT(EPOCH FROM x) / n) * n)`, and `Round(v, d)` becomes
`ROUND(v::numeric, d)::double precision` — PostgreSQL's two-argument `round` is numeric-only.

`DayOfWeek` needs no adjustment here: PostgreSQL's `DOW` is already 0–6 from Sunday, matching
.NET's `DayOfWeek`, where MySQL's `DAYOFWEEK` is 1–7 and the MySQL library subtracts one.

## Bulk writes

```csharp
// Typed setter form — `Counter = u.Counter + 1` stays server-side.
await pg.Query<User>()
    .Where(u => u.IsActive)
    .UpdateAsync(u => new User { LoginCount = u.LoginCount + 1, LastSeen = DateTime.UtcNow });

// Dictionary form — keys resolve through the entity's column allow-list.
await pg.Query<User>().Where(u => u.Id == id).UpdateAsync(new() { ["email"] = newEmail });

await pg.Query<User>().Where(u => u.CreatedUtc < cutoff).DeleteAsync();
```

Both overloads reject database-generated columns rather than emitting SQL the server refuses.
Bulk update deliberately bypasses the soft-delete read filter so it can target or restore
deleted rows.

## Caching

```csharp
var rows = await pg.Query<User>()
    .Where(u => u.IsActive)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();

var hot = await pg.Query<User>().SmartCache("active-users").ToListAsync();

// No argument: uses postgresql.cache.defaultTtlSeconds (60 by default).
var brief = await pg.Query<User>().WithCache().ToListAsync();
```

Cache keys mix the connection id, table, table version, SQL text and sorted parameters. Any
write through the library bumps the table version, so prior entries become unreachable without
an explicit eviction pass. A per-database `cacheEnabledOverride` can switch caching off for
one connection while leaving it on globally. See [Performance & Caching](performance.md).

## Raw SQL

```csharp
var rows = await pg.SqlQueryAsync<User>(
    "SELECT * FROM \"public\".\"users\" WHERE \"email\" = @email",
    new() { ["@email"] = email });

var count = await pg.SqlScalarAsync<long>("SELECT count(*) FROM \"public\".\"users\"");

await pg.ExecuteSqlAsync("REFRESH MATERIALIZED VIEW \"public\".\"user_stats\"");
```

Pass values as parameters. Never interpolate them into the SQL string — the builder cannot
protect a statement you assembled yourself.

> **Raw SQL does not join a transaction.** `SqlQueryAsync`, `SqlScalarAsync` and
> `ExecuteSqlAsync` have no `TransactionScope` overload and always take their own pooled
> connection, so a call made inside an `await using` scope commits on its own and is *not*
> rolled back with the scope. For transactional work use `GetRepository<T>(tx)` and
> `Query<T>(tx)`, or `IMigrationContext.ExecuteAsync` inside a migration — those do run on
> the scope's connection.

## Transactions

```csharp
await using var tx = await pg.BeginTransactionAsync();

var repo = pg.GetRepository<User>(tx);
await repo.InsertAsync(user);
await pg.Query<Audit>(tx).Where(a => a.Stale).DeleteAsync();

await tx.CommitAsync();      // without this, disposal rolls back
```

Only `GetRepository<T>(tx)` and `Query<T>(tx)` enlist on the scope. The raw-SQL entry points
do not — see the note above.

The scope rolls back if it is disposed without a commit, so an early `return` or a thrown
exception cannot silently leave a half-applied transaction.
