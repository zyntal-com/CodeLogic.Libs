# Performance

CL.Data aims to run at the speed of hand-written ADO.NET. Every claim on this page is measured with BenchmarkDotNet
(`benchmarks/Data.Benchmarks`) against MySQL 8.4. The raw results are in `benchmarks/baseline/`.

## Numbers

All numbers are from one Windows 11 machine, with MySQL in Docker Desktop. A round trip there costs about 1.4 ms, so it dominates single-row timings; the allocation column shows library overhead best.

| Scenario | Raw ADO.NET | Dapper | CL.Data | CL.Data compiled | CL.MySQL2 4.9 |
|---|---|---|---|---|---|
| Get by key | 1.43 ms / 7.1 KB | 1.43 ms / 8.9 KB | 1.47 ms / 13.7 KB | 1.46 ms / 11.2 KB | 1.51 ms / 16.3 KB |
| Filter, 100 rows | | 1.55 ms / 35.9 KB | 1.59 ms / 41.3 KB | 1.62 ms / 35.6 KB | 1.61 ms / 60.3 KB |
| Insert 100 rows | | 311 ms / 280 KB ¹ | **5.51 ms / 226 KB** | | 5.53 ms / 256 KB |
| Insert 5,000 rows | | | `InsertMany` 62.1 ms / 11.1 MB, **`Bulk` 29.7 ms / 5.3 MB** | | |
| Get by key, cached | | | **1.7 µs / 4.5 KB** | | |
| Filter, 100 rows, cached | | | **18–26 µs / 17 KB** | | |

¹ Dapper runs one INSERT per row for a list.

The table is the first baseline (2026-10-31). Later rounds removed the reset round trip and most of the remaining
allocations; on the same machine a MySQL lookup by key now takes about 0.8 ms and 7.1 KB (raw ADO.NET 5.1 KB, Dapper 6.9 KB),
a cached one 0.7 µs and 0.6 KB, an insert 7.9 KB and an update 7.7 KB. `benchmarks/baseline/README.md` has every round.

**Summary:**
- CL.Data is within a few percent of hand-written code everywhere.
- Against the 4.x library it's 2–6% faster and uses 20–45% less memory per call.
- Batch inserts beat 4.9, and bulk copy halves the time again.
- Retries, routing and telemetry with no listener cost nothing measurable.

## What makes it fast

- **Source-generated mapping:** entities are read and bound by generated code. That means typed reads, no boxing and no reflection, and it's why Native AOT works. `CL_DATA_FORCE_REFLECTION=1` turns generated mapping off, for comparison.
- **Plan cache:** a query's shape is hashed (`XxHash128`, ignoring captured values) and its SQL and parameter slots are reused. Repeated queries skip translation completely.
- **Compiled queries:** `CompiledQuery.For<T, TArgs>(...)` plans a query once per database, and calls only bind arguments. This is the fastest path for LINQ queries, and it allocates as little as Dapper. Transactions (`tx.Db`), `ReadFrom(...)` and `ForTenant(...)` views share the plans of the database they come from.
- **Key lookups:** `GetByIdAsync` / `GetByKeyAsync` build their SQL once per entity, dialect and naming policy and only bind the key, so they run as fast as raw ADO.NET.
- **Rows read by position:** when CL.Data wrote the SELECT list itself (key lookups, queries and compiled queries of whole entities), rows are read by column position and no column name is asked for. Drivers decode column metadata lazily, and MySqlConnector decodes six strings per column when one name is read: about 1.4 KB per query for a six-column entity. Projections, `Exclude(...)`, raw SQL and statements an interceptor rewrote are matched by name, as before.
- **Lean execution:** the per-command steps (pipeline, retries, executor, reader) reuse pooled async state machines, rows are read without closures, and registered value converters are looked up once per type, not once per value.
- **Cheap SQL building:** parameter placeholder names are cached per dialect, and the SQL writer's buffers are reused per thread. Planning and rendering a 100-row insert takes 55 KB, down from 114 KB.
- **Fewer round trips:**
  - `db.Batch()` sends several queries at once.
  - Inserts and upserts go out as multi-row statements sized to the provider's parameter limit.
  - Bulk copy runs above `bulkThreshold`.
  - Changed-column updates send nothing when nothing changed.
- **No reset round trip on MySQL:** MySqlConnector's reset on every pooled open is skipped. CL.Data resets a connection only
  when it may carry session state (`mySql.connectionReset: "WhenDirty"`, the default; see [Providers](providers.md#mysql)),
  which halves the latency of small queries against a remote server.
- **No reset on close on PostgreSQL:** Npgsql's reset on every close is skipped in the same way
  (`postgreSql.connectionReset`), 9–14% off a lookup by key.
- **SQLite pragmas once per connection:** the connection PRAGMAs are applied when a pooled native connection is new or may have been changed, not on every open (see [Providers](providers.md#sqlite)).
- **Per-row cache eviction:** an update by key keeps the entity cache of every other row (see [Caching](caching.md)).
- **Cheap cache hits:** a hit builds its key in a reused buffer and needs no loader, so a cached key lookup allocates about 0.6 KB.
- **Warm pools:** `minPoolSize` connections are opened at start.
- **No `COUNT` you didn't ask for:** `ToPagedListAsync(withTotal: false)` is the default, and keyset paging (`ToCursorPageAsync`) never counts.
- **The result cache:** microsecond hits, with exact invalidation (see [Caching](caching.md)).

## Native AOT

`CodeLogic.Data` and the providers are `IsAotCompatible`. `samples/Data.AotSmoke` publishes with `PublishAot=true` and
performs CRUD on SQLite with **0 trim or AOT warnings** from CL.Data. Microsoft.Data.SqlClient brings warnings of its own (upstream).

The analyzers (CLD001–CLD011 for mapping, CLD101–CLD103 for untranslatable lambdas) catch at build time what would
otherwise fail, or fall back to reflection, at run time.

## Budgets in CI

`benchmarks/budgets.json` caps each benchmark:
- **Memory:** allocated bytes per operation, about 12% above today's numbers.
- **Time:** relative to Dapper or raw ADO.NET in the same run, which holds on any machine.

```bash
dotnet run -c Release --project benchmarks/Data.Benchmarks -- --check-budgets benchmarks/budgets.json
```

While optimizing, `-- --quick [name]` runs the same benchmark methods in a plain loop and prints bytes and microseconds per
call within seconds (set `CL_BENCH_TYPES=1` to also see which types are allocated most). Numbers for the record come from a
full BenchmarkDotNet run.

The CI job `data-perf` runs the check, and it exits 1 when a benchmark goes over budget. When an optimization lands, lower the budget. Raise one only with a reason.

## Tips

- **Hot queries:** use `WithCache` for read-mostly data, and compiled queries for hot LINQ queries (a compiled count
  allocates about a third less than the LINQ one). Key lookups are already precompiled.
- **Large columns:** use `Exclude(...)` when you don't need them.
- **Large lists:** stream with `AsAsyncEnumerable`, and page with cursors.
- **Writes:** for large inserts with known keys, or when you don't need identities back, use `db.Bulk<T>()`. To write only the columns you changed, use snapshots (`repo.Snapshot` plus `UpdateAsync(entity, snapshot)`).
- **Reporting reads:** send them to replicas (`ReadFrom(ReadPreference.ReplicaOnly)`), and keep the primary for writes.
