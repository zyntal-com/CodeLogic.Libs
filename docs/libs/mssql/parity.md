# CL.MSSQL capability parity

`CL.MSSQL` preserves the workflow and fluent-service shape of `CL.MySQL2`, while exposing SQL Server-native configuration, provider types, mappings, and SQL semantics.

| Capability | CL.MSSQL implementation |
|---|---|
| CRUD and identity keys | Repositories with `OUTPUT INSERTED` |
| Insert ignoring duplicates | `InsertIgnoreAsync` / `InsertManyIgnoreAsync` via savepoints (no `INSERT IGNORE` in T-SQL): batch first, row-by-row fallback for a batch that hits 2627/2601, atomic on any other error |
| Batch insert and update | Dynamically capped below SQL Server's 2,100-parameter limit |
| Upsert and incrementing upsert | Serializable `UPDLOCK`/`HOLDLOCK` source-table algorithm; no `MERGE` |
| Expression upsert | `UpsertAsync(entity, (current, incoming) => …)` in the same serializable batch; `current` is the target row, `incoming` the source row |
| LINQ predicates and functions | SQL Server `bit`, `LIKE`, `DATEPART`, `CONVERT`, `DATEADD`/`DATEDIFF_BIG`, `COALESCE`, and `CONCAT` SQL |
| Joins, subqueries, projections, grouping | Typed and raw joins, projection pushdown, aggregates, and compiled materializers |
| Update-and-return, existence check | `UpdateAndGetAsync` as one `UPDATE … OUTPUT INSERTED.*` (CTE + `TOP` for `Take`, no primary key needed, fails on tables with triggers); `AnyAsync` as `SELECT TOP (1) 1`; `Math.Min`/`Math.Max` as `CASE` |
| Row locking | `ForUpdate`/`ForShare` as table hints: `UPDLOCK`/`HOLDLOCK` + `ROWLOCK`, `NOWAIT`, `READPAST` (`REPEATABLEREAD` for skip-locked shared reads) |
| Offset and cursor paging | `TOP` or `OFFSET … FETCH`, stable PK tie-breaking, and null-safe cursor equality |
| Transactions and raw SQL | `SqlConnection`, `SqlCommand`, and `SqlTransaction`; repositories and the query builder enlist in a `TransactionScope`, raw SQL does not |
| Soft delete and retention | Automatic read filters (reads, `CountAsync` and paging alike) and repeated `DELETE TOP (@batch)` from a worker with a live entity list |
| Cache and invalidation | In-process/custom stores, smart pools, table versions, and events |
| Schema synchronization | `sys.*` catalogs; schemas, identities, defaults, checks, keys, indexes with `INCLUDE`, FKs, collations, and comments; a drifted index is warned about at every level and rebuilt only where drops are allowed (`DriftPending` under Production) |
| Schema modes and CRC | Production, Developer, and Migration reconciliation with schema state in `[dbo].[__schema_state]` |
| Migrations and rollback | Ordered/checksummed migrations in `[dbo].[__migrations]` using `SYSUTCDATETIME()` |
| Cross-node lock | Dedicated session using `sys.sp_getapplock` |
| Schema backup and restore | Catalog-generated DDL for library-managed objects; advanced external objects are excluded |
| Resilience and observability | Deadlock, lock-timeout, and Azure transient retries, a one-second rolling-window N+1 detector, and best-effort cached ShowPlan XML via `sys.dm_exec_query_plan` |
| Health and named connections | Health events/checks and multiple connection IDs |
| Database and table admin | `DatabaseAdmin` on an unpooled `master` connection: create (collation, no charset), drop (`SINGLE_USER WITH ROLLBACK IMMEDIATE`), exists; schema-aware table create/drop/truncate/exists; `CreateDatabaseIfMissing` |
| Live config changes | `IConfigReloadable`: a changed connection string reconnects only that connection ID (`SqlConnection.ClearPool`); other settings update in place |

SQL Server differences remain visible for unique-null behavior, affected-row counts, collations, identity allocation, and ambiguous matches across multiple unique keys. Snapshot fidelity excludes triggers, temporal history, partitioning, replication, and other externally managed objects.
