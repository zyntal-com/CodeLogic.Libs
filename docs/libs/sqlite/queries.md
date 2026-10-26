# CL.SQLite — Query Builder

> Typed LINQ-shaped expressions translated to SQL — filters, multi-key ordering, projections, `GroupBy` aggregates, paging, bulk update & delete, update-and-return, and raw SQL.

See the [overview](index.md) for loading, the repository, schema sync, configuration, and events.

`db.GetQueryBuilder<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to a single parameterized SQL statement, runs it against the embedded SQLite engine, and materializes rows into `T`.

```csharp
var db = Libraries.Get<SQLiteLibrary>();

Result<List<Order>> orders = await db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open" && o.Total > 100)
    .OrderByDescending(o => o.CreatedUtc)
    .Take(50)
    .ToListAsync();

if (orders.IsSuccess)
    foreach (var o in orders.Value!) { /* … */ }
```

Each entry point accepts a `connectionId` selecting a named database from `config.sqlite.json`; it defaults to `"Default"`. `db.GetQueryBuilder<Order>("Reporting")` queries the `Reporting` database.

## Filtering with `Where`

`Where` takes an `Expression<Func<T, bool>>` and translates it to a parameterized `WHERE` clause. Chained calls are AND-combined.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && o.Total < 1000)
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
```

Supported expression shapes:

| Shape | Translates to |
|-------|---------------|
| `==` `!=` `<` `<=` `>` `>=` | the matching SQL operator |
| `&&` `\|\|` `!` | `AND` / `OR` / `NOT (…)` |
| `x.Flag` on a `bool` member | `"Flag" = @p` bound to `true` |
| `x.Name.Contains/StartsWith/EndsWith(s)` | `LIKE '%s%'` / `'s%'` / `'%s'` |
| `== null` / `!= null` | `IS NULL` / `IS NOT NULL` |
| `collection.Contains(x.Id)` — array, `List<T>`, `HashSet<T>`, any `IEnumerable` | `"Id" IN (@p0, @p1, …)`, one bound parameter per element |

Captured local variables and expressions such as `DateTime.UtcNow.AddDays(-30)` are evaluated at build time and bound as parameters — values never land in the SQL string. An `IN` over an **empty** collection emits the literal `1=0`, matching nothing, rather than invalid SQL.

Anything else — another method call, arithmetic on a column, or comparing two columns to each other — raises `NotSupportedException`. Note where it surfaces: `QueryBuilder.Where` translates the expression **eagerly**, so an unsupported predicate throws out of the `Where` call itself rather than arriving as a failed `Result` from the terminal. (`Repository.FindAsync` translates inside its own `try`, so there the same predicate comes back as a failed `Result`.)

## Ordering

Order by one key with `OrderBy` / `OrderByDescending`, then add tie-breakers with `ThenBy` / `ThenByDescending`. Each takes a key selector.

```csharp
db.GetQueryBuilder<Order>()
    .OrderBy(o => o.Status)
    .ThenByDescending(o => o.CreatedUtc)
    .ThenBy(o => o.Id);
```

## Paging

`Limit` / `Take` and `Offset` / `Skip` are aliases for SQL `LIMIT` and `OFFSET`. An offset with no limit is legal — SQLite's grammar is `LIMIT expr [OFFSET expr]`, so it is emitted as `LIMIT -1 OFFSET n`, meaning "skip n, then everything":

```csharp
db.GetQueryBuilder<Order>()
    .OrderByDescending(o => o.CreatedUtc)
    .Skip(40)        // alias: Offset(40)
    .Take(20);       // alias: Limit(20)
```

For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<Order>> page = await db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);

if (page.IsSuccess)
{
    PagedResult<Order> p = page.Value!;
    // p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages
}
```

## Projections — `Select`

`Select` takes an `Expression<Func<T, object?>>` and restricts the emitted column list to just the members the projection touches, instead of the default `SELECT *`.

```csharp
db.GetQueryBuilder<Order>()
    .Select(o => o.Total);                  // SELECT "total"
```

Rows are still materialized into `T`; columns you did not select are simply left at their default values on the returned instances.

Anonymous-type projections work the same way: every member — single or anonymous — is resolved back to the **source entity's** mapped `ColumnName`, and each emitted name is double-quoted, so renamed columns and reserved words are both safe.

```csharp
db.GetQueryBuilder<Order>()
    .Select(o => new { o.Id, o.CreatedUtc });   // SELECT "id", "created_utc"
```

## Aggregates — `GroupBy`

`GroupBy<TKey>` adds a `GROUP BY` clause to the **row-returning** terminals — `ToListAsync` and `ToPagedListAsync`.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.Day);
```

`GroupBy` also reaches the two counting terminals, where it changes what is being counted:

- `CountAsync` on a grouped builder returns the **number of groups**, not the number of rows — the grouped query is wrapped in `SELECT COUNT(*) FROM (…)`.
- `ToPagedListAsync` reports the same number as `TotalItems`, so paging through grouped rows pages correctly.

> **The typed aggregates refuse a grouped builder.** `SumAsync`, `MinAsync` and `MaxAsync` throw `NotSupportedException` when the builder carries a `GroupBy`, because a per-group aggregate has no single scalar answer and silently dropping the grouping would answer a different question. Read the grouped rows with `ToListAsync`, or compute per-group aggregates with `RawQueryAsync`.

The aggregate terminals evaluate in the database over the current `WHERE`:

```csharp
Result<decimal> revenue = await db.GetQueryBuilder<Order>()
    .Where(o => o.Day == today)
    .SumAsync(o => o.Total);

Result<decimal> top = await db.GetQueryBuilder<Order>().MaxAsync(o => o.Total);
Result<decimal> low = await db.GetQueryBuilder<Order>().MinAsync(o => o.Total);
```

## Terminal operations

Nothing runs until a terminal is called; each returns a `Result<…>`. There is no `AverageAsync` or `SingleAsync` on this builder — `SUM`, `MAX` and `MIN` are the only aggregate terminals.

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT … LIMIT 1`; the limit is local to the call, so the builder can be reused |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)`; fails validation if `page` or `pageSize` < 1 |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)`; with `GroupBy`, counts groups |
| `AnyAsync(ct)` / `AnyAsync(predicate, ct)` | `Result<bool>` | `SELECT 1 … LIMIT 1` — stops at the first match |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)`; throws `NotSupportedException` with `GroupBy` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)`; throws `NotSupportedException` with `GroupBy` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)`; throws `NotSupportedException` with `GroupBy` |
| `DeleteAsync(ct)` | `Result<int>` | `DELETE … WHERE …` |
| `UpdateAsync(setter, ct)` / `UpdateAsync(updates, ct)` | `Result<int>` | `UPDATE … SET … WHERE …` |
| `UpdateAndGetAsync(setter, ct)` | `Result<List<T>>` | `UPDATE … SET … WHERE … RETURNING *` |

```csharp
Result<long>  open  = await db.GetQueryBuilder<Order>().Where(o => o.Status == "open").CountAsync();
Result<Order?> first = await db.GetQueryBuilder<Order>().Where(o => o.Id == 1).FirstOrDefaultAsync();
Result<bool>  any   = await db.GetQueryBuilder<Order>().AnyAsync(o => o.Status == "open");
```

## Bulk update & delete

`DeleteAsync` and `UpdateAsync` issue a single set-based statement without materializing rows. Both return the number of affected rows. `UpdateAsync` comes in two forms: a typed setter, and an explicit column map.

The typed setter is a member-init expression; only the properties it assigns are written. A value that reads no column (a constant or captured variable) binds as one parameter, converted the way the repository writes that type. A value that reads the row is translated to a SQL expression, including `Math.Min` / `Math.Max` (SQLite's scalar `min` / `max`), so counters and clamps stay in SQL:

```csharp
// Bulk update via a typed setter
Result<int> opened = await db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "draft")
    .UpdateAsync(o => new Order { Status = "open", UpdatedUtc = DateTime.UtcNow });

// A setter value may read the row it updates
await db.GetQueryBuilder<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });
```

Captured values (`cap`, `refill`, strings, dates, enums) are always sent as parameters, also inside such column expressions; only primitive numbers are written into the SQL text. SQLite evaluates every `SET` expression against the row as it was before the update, so a value that reads a column assigned in the same setter sees its old value, whatever the order. `min` / `max` return NULL when either argument is NULL. Assigning an unmapped property or an auto-increment primary key is rejected; a setter that assigns nothing returns 0 without running a statement.

The column-map form takes keys that are **column names** as they exist in the table (not C# property names); they are quoted and emitted into the `SET` list, with values bound as positional parameters (`@p0`, `@p1`, …) so that a column name which is not a legal parameter token still works. The names themselves are still emitted into the statement, so pass literal column names you control, never user input.

```csharp
// Bulk delete by predicate
Result<int> purged = await db.GetQueryBuilder<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();

// Bulk update by predicate, via an explicit column map
Result<int> renamed = await db.GetQueryBuilder<Note>()
    .Where(n => n.Title == "")
    .UpdateAsync(new Dictionary<string, object?> { ["title"] = "(untitled)" });
```

> A `DeleteAsync` or `UpdateAsync` with no `Where` affects every row in the table. Always scope bulk mutations with a predicate unless that is genuinely intended.

### Update and return the rows — `UpdateAndGetAsync`

`UpdateAndGetAsync` runs the same typed setter and returns the updated rows as they are after the update, in one atomic `UPDATE … RETURNING *` statement — no other writer can change them in between:

```csharp
Result<List<Bucket>> r = await db.GetQueryBuilder<Bucket>()
    .Where(b => b.Key == key)
    .UpdateAndGetAsync(b => new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });

// Claim one job from a queue
Result<List<Job>> claimed = await db.GetQueryBuilder<Job>()
    .Where(j => j.Status == "queued")
    .OrderBy(j => j.Id).Take(1)
    .UpdateAndGetAsync(j => new Job { Status = "running", StartedUtc = now });
```

`RETURNING` needs SQLite 3.35 or later; the bundled engine (SQLite 3.50.4, via `SQLitePCLRaw.bundle_e_sqlite3`) has it. `OrderBy`, `Take` and `Skip` choose which rows are updated through `WHERE rowid IN (SELECT rowid … ORDER BY … LIMIT n OFFSET m)`, because `UPDATE … ORDER BY / LIMIT` only exists in specially compiled SQLite builds. SQLite does not define the order of `RETURNING` rows, so the returned list is **not** guaranteed to follow the `OrderBy`. It is not available with `GroupBy`.

### Row locking — `ForUpdate` / `ForShare`

`ForUpdate` and `ForShare` exist for API parity with CL.MySQL2, CL.MSSQL and CL.PostgreSQL, but always throw `NotSupportedException`: SQLite has no row-level locks (a writer locks the whole database), and this library has no transactions to hold a lock in. For an atomic read-modify-write use `UpdateAndGetAsync`, which updates and returns the rows in one statement, as in the job-queue example above.

### Insert or update from the current row — expression `UpsertAsync`

`Repository.UpsertAsync(entity, onConflict, conflictTarget)` inserts the entity, or — when it collides on the conflict target — updates the existing row with a setter that receives that row (`current`) and the row that failed to insert (`incoming`):

```csharp
Result<int> r = await db.GetRepository<LoginThrottle>().UpsertAsync(
    new LoginThrottle { Ip = ip, Failures = 1, LastUtc = now },
    (current, incoming) => new LoginThrottle
    {
        Failures = current.Failures + 1,
        LastUtc  = incoming.LastUtc,
    });
```

It emits `INSERT INTO "t" (…) VALUES (…) ON CONFLICT (target) DO UPDATE SET col = expr`, with `current.Col` written as the existing row's column and `incoming.Col` as `excluded."col"`. Columns the setter does not assign keep their current value, captured values are parameters, and every assignment sees the existing row as it was before the update. The result is 1 for an insert or an update.

SQLite only accepts a conflict target that matches a primary key or unique index exactly. When `conflictTarget` is omitted it is inferred from the primary key (unless it is auto-increment, which an incoming row never collides on), a `[SQLiteColumn(IsUnique = true)]` column, or a unique `[SQLiteIndex]` — and there must be exactly one such candidate; otherwise the call returns a failed `Result` naming the candidates, and you pass e.g. `conflictTarget: [nameof(LoginThrottle.Ip)]`. Unlike the plain `UpsertAsync(entity)` (`INSERT OR REPLACE`), this updates the row in place instead of deleting and re-inserting it.

## Raw SQL escape hatches

When the builder can't express something, drop to parameterized raw SQL on the repository. Both take named parameters as a `Dictionary<string, object?>`.

```csharp
var repo = db.GetRepository<UserRecord>();

// Materialize rows into T with the same mapper as the builder
Result<List<UserRecord>> rows = await repo.RawQueryAsync(
    "SELECT * FROM users WHERE country = @c AND created_utc >= @since",
    new() { ["@c"] = "DK", ["@since"] = since });

// Non-query — returns affected rows
Result<int> n = await repo.RawExecuteAsync(
    "UPDATE users SET active = 0 WHERE last_seen < @cutoff",
    new() { ["@cutoff"] = cutoff });
```

Always bind values via named parameters — never interpolate user input into the SQL string.

## See also

- [Overview](index.md) — loading, the repository, schema sync, configuration, and events.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.SQLite)
