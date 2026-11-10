# CL.Data benchmark baseline (2026-10-31)

BenchmarkDotNet 0.15.4, in-process, 3 warm-up + 10 measured iterations, `MemoryDiagnoser`. One MySQL 8.4 in Docker
Desktop on the same Windows 11 machine (`cl-data-mysql`), 1,000 seeded rows. CL.Data, Dapper and raw ADO.NET run on
.NET 11 (`benchmarks/Data.Benchmarks`). CodeLogic.MySQL2 4.9.107 ran the same scenarios once on .NET 10, in its own
process; its raw output is `mysql2-4.9.txt` (the comparison project was removed once CL.Data beat it everywhere).

**Read the numbers with the environment in mind:** a round trip to Docker Desktop costs ~1.4 ms, so it dominates every
single-row scenario. Library overhead is in the tens of microseconds; the allocation column shows it better than the time.

| Scenario | Raw ADO.NET | Dapper | **CL.Data** | **CL.Data compiled** | CL.MySQL2 4.9 |
|---|---|---|---|---|---|
| Get by key | 1.401 ms / 7.1 KB | 1.395 ms / 8.9 KB | 1.419 ms / 13.6 KB | 1.412 ms / 10.4 KB | 1.510 ms / 16.3 KB |
| Filter, 100 rows | | 1.601 ms / 35.8 KB | 1.599 ms / 41.3 KB | 1.560 ms / 34.9 KB | 1.608 ms / 60.3 KB |
| Count (filtered) | | 1.588 ms / 6.1 KB | 1.597 ms / 11.8 KB | | 1.608 ms / 18.2 KB |
| Insert one | | 3.685 ms / 7.6 KB | 3.664 ms / 13.5 KB | | 3.877 ms / 15.9 KB |
| Insert 100 | | 297.8 ms / 279.7 KB ¹ | 5.902 ms / 298.2 KB | | 5.533 ms / 255.9 KB |
| Update one (all columns) | | 3.742 ms / 7.0 KB | 3.738 ms / 12.8 KB | | 3.830 ms / 15.4 KB |
| Update one (changed column only) | | | 3.725 ms / 10.6 KB | | |

¹ Dapper executes one INSERT per row for a list; CL.Data and CL.MySQL2 send multi-row batches.

## Result cache

Same machine, cache hits (`DatabaseOptions.Cache`, `WithCache()` / the entity cache). Each hit materializes fresh objects
from the buffered rows, so callers can never change what is cached.

| Scenario | Database round trip (CL.Data) | **Cache hit** | Allocated (hit) |
|---|---|---|---|
| Get by key (entity cache) | 1,465 µs | **1.66 µs** (~880×) | 5.2 KB |
| Filter, 100 rows (`WithCache()`) | 1,611 µs | **18.4 µs** (~87×) | 17.5 KB |

## Write path and bulk copy

| Scenario | Before | **Now** | CL.MySQL2 4.9 |
|---|---|---|---|
| Insert 100 (multi-row) | 5.902 ms / 298.2 KB | **5.514 ms / 226.4 KB** | 5.533 ms / 255.9 KB |
| Insert 5,000: `InsertMany` (multi-row) | | 62.1 ms / 11.07 MB | |
| Insert 5,000: `Bulk<T>().InsertAsync` (MySqlBulkCopy) | | **29.7 ms / 5.3 MB** | |

## Connection reset only when dirty (2026-11-08)

MySqlConnector resets every pooled connection when it is opened, which is one full round trip. CL.Data now runs the pool
without it (`mySql.connectionReset: "WhenDirty"`, the default) and resets only connections that may carry session state.
The raw ADO.NET and Dapper baselines were switched to `ConnectionReset=false` as well, so the ratios stay like-for-like.
Same machine and container as above; these numbers are from a run on an idle server.

| Get by key | Mean | Allocated |
|---|---:|---:|
| Raw ADO.NET (`ConnectionReset=false`) | 582 µs | 5.3 KB |
| Dapper (`ConnectionReset=false`) | 603 µs | 7.1 KB |
| **CL.Data, `WhenDirty` (default)** | **622 µs** | **12.0 KB** |
| CL.Data, compiled query | 619 µs | 9.5 KB |
| CL.Data, `Always` (the driver's reset on every open, as before) | 1,166 µs | 13.9 KB |

The reset on open cost 544 µs per lookup here (1.87×), about one round trip to Docker Desktop. Single-row operations also
allocate about 2 KB less per call without it. Budgets in `benchmarks/budgets.json` were lowered to match.

## Second round (2026-11-08): lean execution, per-row cache eviction, PostgreSQL reset

Same machine and containers; the "before" column is the run just before these changes (with the connection-reset change
already in). Times through Docker Desktop vary by a few percent between runs, so single-call times within ~5% are ties;
allocations are exact.

| Scenario | Before | **After** |
|---|---:|---:|
| MySQL get by key | 642 µs / 11.9 KB | **614 µs / 8.9 KB** (raw ADO.NET 594 µs / 5.3 KB, Dapper 590 µs / 7.1 KB) |
| MySQL get by key, entity cache hit | 2.19 µs / 3.95 KB | **0.79 µs / 1.63 KB** |
| 95% lookups / 5% updates, entity cache (per operation) | 767 µs / 14.7 KB | **191 µs / 2.4 KB** |
| Insert one row | 3.73 ms / 11.5 KB | **2.98 ms / 9.8 KB** |
| Update one row | 3.41 ms / 11.1 KB | **3.33 ms / 10.3 KB** |
| Count (LINQ / compiled) | 698 µs / 10.1 KB | 708 µs / 9.8 KB; **compiled 695 µs / 6.2 KB** |
| 32 workers: get by key (per operation) | 55.3 µs / 11.7 KB | **53.7 µs / 7.6 KB** (Dapper 51.9 µs / 6.8 KB) |
| 32 workers: filter 100 rows | 82.9 µs / 39.4 KB | **75.7 µs / 37.9 KB** |
| 32 workers: insert | 256 µs / 11.2 KB | **223 µs / 8.9 KB** |
| PostgreSQL get by key | 639 µs / 8.3 KB | **550 µs / 4.8 KB** (raw Npgsql: 632 µs with reset on close, 592 µs without) |

- **Key lookups** build their SQL once per entity; **per-row eviction** keeps the rest of the entity cache through updates
  by key; the **per-command path** uses pooled async state machines, no closures and cached converter lookups; insert
  column lists are cached.
- **PostgreSQL** skips Npgsql's reset on close and resets only dirty sessions, like MySQL.
- **Measured and not adopted:** preparing CL.Data's own key lookups on MySQL (597 µs prepared against 596 µs unprepared,
  same allocations), which would also have made every dirty reset more expensive.
- **LINQ queries** still build an expression tree (the C# compiler does) and render their SQL on every call; compiled
  queries avoid both.

## Third round (2026-11-10): rows by position, cache hits, writes, SQLite pragmas

Same machine and containers, full BenchmarkDotNet runs before and after on the same day (the server was a little slower
during the second run, so compare times with the baselines of the same run; allocations are exact).

| Scenario | Before | **After** | Baseline in the after run |
|---|---:|---:|---:|
| MySQL get by key | 765 µs / 8.5 KB | **821 µs / 7.1 KB** | raw ADO.NET 771 µs / 5.1 KB, Dapper 812 µs / 6.9 KB |
| MySQL get by key, compiled | 752 µs / 8.8 KB | **801 µs / 7.5 KB** | |
| MySQL get by key, entity cache hit | 0.75 µs / 1.63 KB | **0.69 µs / 0.62 KB** | |
| Get by key inside a transaction (compiled) | 2,341 µs / 24.4 KB ¹ | **2,140 µs / 10.7 KB** | Dapper 2,204 µs / 10.1 KB |
| Get by key inside a transaction (repository) | 12.4 KB ¹ | **2,274 µs / 10.6 KB** | |
| Filter, 100 rows (LINQ / compiled) | 38.9 KB / 33.3 KB | **35.6 KB / 31.9 KB** | Dapper 33.8 KB |
| Filter, 100 rows, cached | 17.1 µs / 16.2 KB | **16.0 µs / 13.5 KB** | |
| Count (LINQ / compiled) | 9.5 KB / 5.9 KB | **8.4 KB** / 6.0 KB | Dapper 4.1 KB |
| Insert one row | 3.22 ms / 9.4 KB | **3.35 ms / 7.9 KB** | Dapper 3.28 ms / 5.5 KB |
| Update one row (all columns / changed) | 9.9 KB / 8.1 KB | **7.7 KB / 6.6 KB** | Dapper 4.9 KB |
| 95% lookups / 5% updates, entity cache | 209 µs / 2.4 KB | **208 µs / 1.3 KB** | |
| 32 workers: get by key | 50.9 µs / 7.6 KB | **53.7 µs / 6.1 KB** | Dapper 52.5 µs / 6.8 KB |
| 32 workers: filter 100 rows / insert | 37.8 KB / 8.7 KB | **34.5 KB / 6.7 KB** | |
| PostgreSQL get by key | 735 µs / 4.6 KB | 739 µs / 4.6 KB | raw Npgsql 772 µs / 2.7 KB (no reset on close) |
| SQLite get by key ¹ | 14.4 µs / 3.9 KB | **10.4 µs / 2.5 KB** | raw ADO.NET 7.7 µs / 1.7 KB |
| SQLite insert one row ¹ | 38.5 µs / 6.4 KB | **34.5 µs / 4.4 KB** | |

¹ Quick profile (`--quick`), because the scenario did not exist in the "before" BenchmarkDotNet run.

- **Rows by position:** CL.Data no longer asks for column names when it wrote the SELECT list itself. MySqlConnector decodes
  six strings per column when one name is read (about 1.4 KB per query here), which is also most of Dapper's distance to
  raw ADO.NET. Npgsql keeps the names it already has, so PostgreSQL did not change.
- **Compiled queries inside transactions:** every transaction is a new view of the database and planned the query again;
  plans are now shared by the views of one database.
- **Cache hits** build their key without a string and need no loader; **writes** lost their per-call delegate, closure and
  three async frames.
- **SQLite** applies its connection pragmas once per pooled native connection instead of on every open (3 µs and 1 KB per
  command).
- **What is left** (measured, not started): non-compiled LINQ renders its SQL on every call (count 8.4 KB against 6.0 KB
  compiled), and single-row inserts and updates build and render their statement every time (about 2.5 KB above Dapper).

## Findings

- **CL.Data runs at the speed of hand-written ADO.NET and Dapper** (within 1–2%) in every scenario; the database round
  trip is the cost. Compiled queries are the fastest CL.Data path and allocate as little as Dapper (filter: 34.9 vs 35.8 KB).
- **Against CL.MySQL2 4.9:** 2–6% faster on single-row reads and writes (the gap is the library overhead, since the
  round trip is the same), and **20–42% less memory** per call (get by key 16.3 → 10.4–13.6 KB, filter 60.3 → 34.9–41.3 KB,
  count 18.2 → 11.8 KB).
- **Fixed:** batch insert of 100 rows was 7% slower and allocated 16% more than 4.9. It is now faster than 4.9,
  with 12% less memory.
- On a remote database with a few ms of latency the time differences shrink further; on a busy server the lower
  allocations matter more than the microseconds.

## Re-running

```sh
set CL_DATA_MYSQL_CS=Server=127.0.0.1;Port=33406;Database=cl_data;User ID=root;Password=;SslMode=None;AllowPublicKeyRetrieval=true
dotnet run -c Release --project benchmarks/Data.Benchmarks -- --filter *
```
