# CL.Data benchmark baseline (2026-10-31)

BenchmarkDotNet 0.15.4, in-process, 3 warm-up + 10 measured iterations, `MemoryDiagnoser`. One MySQL 8.4 in Docker
Desktop on the same Windows 11 machine (`cl-data-mysql`), 1,000 seeded rows. CL.Data, Dapper and raw ADO.NET run on
.NET 11 (`benchmarks/Data.Benchmarks`); CodeLogic.MySQL2 4.9.107 runs the same scenarios on .NET 10 in its own process
(`benchmarks/MySql2.Baseline`, it needs CodeLogic 4).

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

## Result cache (WP-80–82)

Same machine, cache hits (`DatabaseOptions.Cache`, `WithCache()` / the entity cache). Each hit materializes fresh objects
from the buffered rows, so callers can never change what is cached.

| Scenario | Database round trip (CL.Data) | **Cache hit** | Allocated (hit) |
|---|---|---|---|
| Get by key (entity cache) | 1,465 µs | **1.66 µs** (~880×) | 5.2 KB |
| Filter, 100 rows (`WithCache()`) | 1,611 µs | **18.4 µs** (~87×) | 17.5 KB |

## Findings

- **CL.Data runs at the speed of hand-written ADO.NET and Dapper** (within 1–2%) in every scenario; the database round
  trip is the cost. Compiled queries are the fastest CL.Data path and allocate as little as Dapper (filter: 34.9 vs 35.8 KB).
- **Against CL.MySQL2 4.9:** 2–6% faster on single-row reads and writes (the gap is the library overhead, since the
  round trip is the same), and **20–42% less memory** per call (get by key 16.3 → 10.4–13.6 KB, filter 60.3 → 34.9–41.3 KB,
  count 18.2 → 11.8 KB).
- **To improve:** batch insert of 100 rows is 7% slower and allocates 16% more than 4.9. WP-A1 (bulk copy, and the
  automatic switch to it for large `InsertMany`) is where to fix it; this row is the regression budget to beat.
- On a remote database with a few ms of latency the time differences shrink further; on a busy server the lower
  allocations matter more than the microseconds.

## Re-running

```sh
set CL_DATA_MYSQL_CS=Server=127.0.0.1;Port=33406;Database=cl_data;User ID=root;Password=;SslMode=None;AllowPublicKeyRetrieval=true
dotnet run -c Release --project benchmarks/MySql2.Baseline -- --filter *
dotnet run -c Release --project benchmarks/Data.Benchmarks -- --filter *
```
