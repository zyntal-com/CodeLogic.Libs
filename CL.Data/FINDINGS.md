# CL.Data: findings log

What building CL.Data (5.x) turned up: measurements, bugs found and fixed, behaviour worth knowing, decisions still open,
and work that was deferred. Newest milestones are at the end of each section. Feature parity with the 4.x libraries is
tracked separately in [PARITY.md](PARITY.md), and the changes per work package are in [CHANGELOG.md](CHANGELOG.md).

## Performance (measured)

All numbers come from BenchmarkDotNet against MySQL 8.4 in Docker Desktop on the same Windows 11 machine. The full tables
are in `benchmarks/baseline/README.md`. A round trip to Docker costs about 1.4 ms, so it dominates single-row timings,
and the allocation column shows library overhead better than the time column does.

| Area | Finding |
|---|---|
| Against hand-written code | CL.Data is within 1–4% of raw ADO.NET and Dapper in every scenario. Compiled queries allocate about as little as Dapper (filter of 100 rows: 34.9 KB against 35.8 KB). |
| Against CL.MySQL2 4.9 | 2–6% faster on single-row reads and writes, and 20–42% less memory per call (get by key: 16.3 KB → 10.4–13.6 KB). |
| Batch insert of 100 rows (fixed in WP-A1) | It was 7% slower and allocated 16% more than 4.9 (5.90 ms / 298 KB against 5.53 ms / 256 KB). Now **5.51 ms / 226 KB**: faster than 4.9, with 12% less memory. The fixes are cached parameter names per dialect, a reused SQL writer per thread, and one value collector per plan. Planning plus rendering 100 rows dropped from 114 KB to 55 KB, and `InsertAllocationBudget` guards that number. |
| Bulk copy | 5,000 rows on MySQL take **29.7 ms through bulk copy against 62.1 ms** with multi-row `InsertMany` (2.1× faster), using 5.3 MB against 11.1 MB. Bulk copy still creates one driver parameter per value to get the exact stored form, about 1 KB per row; there is room to trim that. |
| Result cache | A get by key costs **1.66 µs** from the entity cache against 1.47 ms from the database (~880×). A 100-row filter costs **18.4 µs** against 1.61 ms (~87×). Each hit builds fresh objects. |
| Retries and routing | Retries are on by default and cost no measurable time: GetById is still 1.02× raw ADO.NET. They add about 0.3 KB per command (the async step). |
| Telemetry | With no listener, telemetry adds nothing (GetById allocations unchanged: 14.42 → 14.43 KB, time equal to raw ADO.NET). Spans and metrics are only built when a listener is attached. Slow-query and N+1 checks are integer comparisons when they are off. |
| Plan cache | Hashing query shapes with `XxHash128` at first took about 700 ns per lookup. Hashing into a byte buffer in CL.Common `FastHash` brought it to about 70 ns. |

**Budgets (WP-A4):** `benchmarks/budgets.json` holds a ceiling per benchmark for allocations per operation (about 12% above
the current numbers) and for time relative to Dapper or raw ADO.NET in the same run.
- **Running it:** `dotnet run -c Release --project benchmarks/Data.Benchmarks -- --check-budgets benchmarks/budgets.json`. The CI job `data-perf` runs it but isn't required yet.
- **First local run:** every benchmark is within budget (for example GetById 13,980 B against 15,700 B, at 1.02× ADO.NET).

## Bugs found and fixed

| Where | Bug | Found by | Fix |
|---|---|---|---|
| CL.Data.SqlServer | Inserts and upserts carrying an explicit identity value failed with error 544 (`IDENTITY_INSERT` is OFF). | Cache conformance (upsert with a key) | The statements run between `SET IDENTITY_INSERT ON/OFF`. A failing insert switches it off again, so pooled sessions don't keep it on. A regression test runs on all four providers. |
| CL.Common `FastHash` | `XxHash128` over spans was about 10× slower than needed. | Plan cache benchmark | Hash into bytes. |
| Plan cache | The binder passed the item instead of the shaped root value. Lookups compared `PropertyInfo` slowly. Guards boxed values. | Plan cache tests | `Slot` takes the root value, lookups use reference and handle comparison, and guards store `long` facts. |
| Schema sync | Drift state wasn't forgotten after a drift. A SQLite table rebuild cascaded deletes through foreign keys. Indexes were duplicated after a rebuild. | Schema conformance | The state row is deleted on drift. Foreign keys are switched off during the rebuild, followed by `foreign_key_check`. `CREATE INDEX IF NOT EXISTS` is used. |
| Backup | A sequential-access reader read the same column twice. SQLite columns with mixed types didn't round-trip. | Backup conformance | Each column is read once, and values are written inline with a type tag. |
| CL.Data.SqlServer (explain) | `SET SHOWPLAN_TEXT ON` returns no plan for a parameterized RPC call (`sp_executesql`), so slow-query plans were empty. | Telemetry conformance | The explainer declares the parameters as variables of the same SQL types in a plain batch. |
| Configuration (audit during WP-B0/B1) | Five settings did nothing: `CacheEnabled` per connection, `CreateDatabaseIfMissing`, `data.cache.PublishEvents`, `MaxInClauseValues` and `data.cache.TimeQuantizeSeconds`. Also, nothing checked queries against the provider's parameter limit. | Checking every config property for a reader | The first three now work, with tests. `MaxInClauseValues` and `TimeQuantizeSeconds` are removed: splitting IN lists doesn't help with parameter limits, and quantizing would silently change query values. A query over the provider's parameter limit now fails with `data.parameter_limit` before it is sent. |
| **4.x (separate)** | The MySQL2, MSSQL and PostgreSQL translators reject the C# 14 three-argument `MemoryExtensions.Contains(span, value, null)` that the compiler emits for nullable and enum arrays. | Porting the translator | The fix is on branch `fix/4x-span-contains` in worktree `../CodeLogic.Libs-4x`, with tests and changelogs. **Not pushed:** it needs a PR into `support/4.x` once the owner approves. |

## Behaviour worth knowing

- **Retries are on by default** (`RetryCount = 3`, `CircuitBreakerThreshold = 5`).
  - Reads retry transient failures.
  - Writes retry only when the statement certainly didn't take effect: the connection couldn't be opened, or the server rolled it back (deadlock, lock timeout).
  - A transient failure after a write was sent, and a failed commit, are never retried, because the outcome is unknown.
  - Nothing retries inside a user transaction.
  - Callers always get the original `data.*` error, not a wrapper.
- **The circuit breaker counts node failures only** (unreachable, connection lost). Deadlocks, slow-query timeouts and constraint errors never open it.
- **Streaming (`AsAsyncEnumerable`) bypasses the pipeline.** It always reads from the primary, with no retries, routing or cache, because rows already handed to the caller cannot be replayed.
- **Reads-your-writes is per `Database`, not per user.** After any write through that database, every read stays on the primary for `StickyAfterWriteMs`.
- **Replicas with unknown lag count as usable.** A probe that can't run, for example because of a missing privilege, sets `LagMs = null` and the replica stays in rotation. The probes need these privileges:
  - MySQL: `REPLICATION CLIENT`.
  - SQL Server: `VIEW SERVER STATE`.
  - PostgreSQL: none.

  When replication is stopped, the probe reports `ReplicaProbe.Broken`, which always takes the replica out of rotation.
- **Cache tags include the connection name.** Nodes linked by a cache bus must use the same connection names.
  - A failed publish leaves other nodes on their TTL; the local write is unaffected.
  - `DatabaseCacheBus` polls, so other nodes see an invalidation up to one poll interval late (500 ms by default).
- **Reads inside a transaction never use the cache.** Invalidations from a transaction wait for commit, and a rollback drops nothing.
- **SQL Server connection strings:** `Encrypt` and `TrustServerCertificate` written in an explicit connection string win over the `SqlServer` option defaults, matching how `sslMode` works on the other providers. The owner decided this on 2026-10-31. An explicit `sslMode` (`Disable`, `VerifyCA`, `VerifyFull`) still applies.
- **Bulk copy does not read back server-generated identities.** `InsertManyAsync` therefore switches to bulk at `BulkThreshold` (5,000 by default) only when nothing has to be read back: every row already carries its identity and the table has no row version.
  - Bulk copy skips entity save hooks and the command pipeline (interceptors, retries). It joins a transaction view, and it drops the table's cached results.
  - MySQL needs `MySql.AllowLoadLocalInfile` and the server's `local_infile`. Without both, CL.Data batches multi-row inserts, and it remembers when the server refuses.
  - PostgreSQL reads the column types from `pg_attribute` once per chunk and writes binary COPY with them.
- **Telemetry granularity:** each span and duration measurement covers a whole logical command, retries included (`cl.data.attempts` on the span).
  - Slow-query, N+1 and `QueryExecutedEvent` checks run per attempt, so a retried command can report each slow attempt.
  - EXPLAIN runs in the background on its own connection, for reads outside transactions only, and never executes the statement. The event arrives after the query has returned.
  - Pool metrics are left to the drivers' own meters.
- **N+1 detection** keys on the exact parameterized SQL text, in a one-second window, and reports once per window. Its memory is bounded: the window table is cleared above 10,000 shapes.
- **Hosting (`DataLibrary`):**
  - A connection that can't be created (missing provider, invalid settings) doesn't stop startup. It's logged, health reports Degraded, and `Db(name)` returns that error.
  - Health is Unhealthy only when a primary can't be reached.
  - A `data` change touches only the connections that were added, changed or removed. Removed pools close after `min(CommandTimeoutSeconds, 30)` seconds, so in-flight commands can finish.
  - A `data.cache` change rebuilds the cache and every database.
  - Provider libraries declare `Required("CL.Data")`. CodeLogic runs every Configure before any Initialize, which the hosting tests confirm, so providers register in time.
  - The CodeLogic event bus dispatches on an event's static type, so `DataLibrary` forwards each CL.Data event under its concrete type through an exhaustive switch over the closed `DataEvent` hierarchy.
- **Native AOT:** CL.Data and the SQLite, MySQL and PostgreSQL providers publish with 0 trim or AOT warnings. Microsoft.Data.SqlClient brings 57 warnings of its own (upstream).

## Deferred, with reasons

| Item | Reason | Planned |
|---|---|---|
| `ICacheStore` (L2 distributed cache interface) | It would ship with nothing implementing it. | With the Redis adapter (5.1). |
| Cache bus over CodeLogic events | `InProcessCacheBus` already covers in-process linking. | Add if hosts ask for it. |
| ~~`data.cache` config section, wiring the bus and probes on start~~ | Done in WP-A2: `DataLibrary` wires the cache, bus, probes, warm-query refresh and pool warm-up. | |
| SQL Server live replica test | An availability group can't run in a local container. | Probe parsing is covered by canned results; a real AG test only if CI infrastructure allows. |
| `benchmarks/MySql2.Baseline` | It's the 4.9 comparison, needed until bulk copy beats the 100-row insert. | Decide after WP-A1. |

## Comprehensive audit, CL.Data items (2026-10-31)

| # | Claim | Verdict | Fix |
|---|---|---|---|
| 3.1 | Unscoped insert, upsert or bulk copy can write another tenant's row through a preset tenant column | True | `Stamp` requires a tenant for every write. This is breaking: writing for a specific tenant goes through `db.ForTenant(id)`. |
| 3.2 | `Retention.PurgeAsync` ignores the tenant | True for tenant views | A tenant view purges only its tenant. The unscoped purge stays system-wide by design. |
| 4.1 | SQL Server `BucketUtc`: `DATEADD` with a `bigint` (claimed Msg 8116) | Partly | The renderer passed `bigint`, which SQL Server converts to `int`. It works today, then fails with **8115 overflow after 2038-01-19**. The suggested `CAST AS int` does not fix that. The fix adds days, then seconds. |
| 4.6 | Mixed-identity `UpsertMany` drops the identity column for every row | True | Explicit-identity rows and generated-identity rows are planned as separate statements. |
| 5.5 | `SqlTables.Of` allocates on every cached call | True | Tags are computed only on a miss, inside the loader. |
| 6.2 | `AsAsyncEnumerable` bypasses the pipeline and replicas | True | The stream opens through the pipeline: interceptors, telemetry, routing, and a retry of the open. |

**Reported separately: MySQL unchanged saves.** `UseAffectedRows = true` (needed by insert-ignore, which is rendered as a no-op `ON DUPLICATE KEY UPDATE`) combined with the update's exactly-one-row check made saving an unchanged entity fail with `data.not_found`. The snapshot path hid it, because it skips no-op saves. Fixed with a match check when 0 rows are reported. Still different on MySQL, documented in PARITY: set-based `UpdateAsync` returns rows *changed*, not rows matched, and `AdjustAsync(..., 0)` returns false.

**Found while testing:** a `SqlFn` call with only captured arguments, such as `SqlFn.BucketUtc(at, 60)` in a `Select`, was evaluated on the client and threw "SqlFn functions can only be used inside CL.Data query expressions". `SqlFn` calls are now always translated. There was no conformance test running `BucketUtc` against a real server, which is why 4.1 was missed; one exists now, for dates before and after 2038.

## Strong-name signing (2026-10-31)

All CodeLogic and CodeLogic.Libs assemblies are strong-named with one key pair, token `53a89305eedc5d98` (see AGENTS.md).
Ordinary builds public-sign; releases are fully signed from the `CL_STRONG_NAME_KEY` secret, and the release fails without it.
`build/verify-strong-name.ps1` blocks a push when any packed assembly isn't fully signed.

**Findings while setting it up:**
- **Key type:** a key generated as a key-exchange key (`CALG_RSA_KEYX`) gave public-signed and fully signed builds different identities. Strong-name keys must be signature keys (`CALG_RSA_SIGN`), the way `sn -k` makes them. The generator script uses `KeyNumber.Signature`.
- **Incremental builds:** an incremental build doesn't notice a changed `CLStrongNameKeyFile`, so packing over an old build ships public-signed DLLs. Release runners always build clean, and the verification step catches it regardless.
- **Unsigned dependencies:** referencing unsigned assemblies produced no CS8002 warnings on .NET 11. The 15 libraries, their tests, the CodeLogic samples and the generator all build signed with 0 warnings.

## Local-only state (nothing pushed)

- **Libs:** `feature/cl-data` with every WP branch (`cl-data/<wp>`) merged in locally.
- **CodeLogic:** `feature/result-throw-if-failure` adds `Result.ThrowIfFailure()` and `ResultFailedException`, and moves the README and docs to 5.x. The libraries build against this local source (`CodeLogicFromNuGet=false`). At the end: publish CodeLogic and switch the libraries back to NuGet.
- **4.x fix:** see the table above. The branch `fix/4x-span-contains` builds on top of `origin/support/4.x`, which already has 485cb9a, the two-argument span `Contains` fix. It adds only the three-argument form (enum and nullable arrays), so the two complement each other. The local `support/4.x` label is stale; compare against `origin/support/4.x`.
- **Old database libraries:** no code, project, CI or release references remain (WP-B2). What's left is history: the parity checklist, a release-workflow comment about 4.8.86, and the 4.9 benchmark baseline project.
- **Unblocked on 2026-10-31:** CodeLogic **5.0.30-preview** is published (PR #18), with `ThrowIfFailure` and strong-name signing, and `CodeLogicPackageVersion` is bumped to it. The original note follows.
- **CI cannot pass until CodeLogic 5 is published.** CI builds with `-p:CodeLogicFromNuGet=true`, but the libraries and tests use `Result.ThrowIfFailure()` and `ResultFailedException`. Those exist only on the local CodeLogic branch, not in `CodeLogic 5.0.29-preview` on nuget.org. Publish CodeLogic and bump `CodeLogicPackageVersion` before any CI run means anything.

## Test environment notes

- **Containers:** single-server tests use `cl-data-mysql` (33406), `cl-data-pg` (15532) and `cl-data-mssql` (14430). Replica pairs come from `tests/replicas/docker-compose.yml` (project `cl-data-replicas`: MySQL 33407/33408, PostgreSQL 15533/15534) and run with `CL_DATA_REPLICAS=1`.
- **MySQL replica:** `--super-read-only` can't be a startup flag, because it blocks the image's own init steps. The init script enables it with `SET PERSIST` instead.
- **Replica scripts:** they must keep LF line endings, which `.gitattributes` enforces.
- **Reflection mapping:** `CL_DATA_FORCE_REFLECTION=1` runs the suites without generated mapping. Run both modes before a release.
