# CodeLogic.Data

> **Status: 5.0 preview.**

One database framework for MySQL, PostgreSQL, SQL Server and SQLite. It's the CodeLogic 5 replacement for CL.MySQL2,
CL.MSSQL, CL.PostgreSQL and CL.SQLite: one core package plus one small package per database, with the same API and the
same behaviour everywhere.

| Package | Database | Driver |
|---|---|---|
| `CodeLogic.Data` | core (no driver) | — |
| `CodeLogic.Data.MySql` | MySQL / MariaDB | MySqlConnector |
| `CodeLogic.Data.PostgreSql` | PostgreSQL | Npgsql |
| `CodeLogic.Data.SqlServer` | SQL Server / Azure SQL | Microsoft.Data.SqlClient |
| `CodeLogic.Data.Sqlite` | SQLite | Microsoft.Data.Sqlite |

```bash
dotnet add package CodeLogic.Data.PostgreSql   # brings CodeLogic.Data
```

```csharp
await using var db = Database.Create("main", PostgreSqlDataProvider.Instance, new DataConnectionConfig
{
    Provider = "postgresql", Host = "localhost", Database = "app", Username = "app", Password = "secret",
}).ValueOrThrow();

await db.Schema.SyncAsync<Product>();
await db.Repository<Product>().InsertAsync(new Product { Sku = "A-1", Price = 9.95m });
var cheap = await db.Query<Product>().WithCache().Where(p => p.Price < 20m).OrderBy(p => p.Price).ToListAsync();
```

**Features:**
- **Queries:** typed LINQ (strictly translated, never evaluated on the client), projections, joins over 2–5 tables, grouping with HAVING, cursor paging and streaming, and compiled queries.
- **Writes:** repositories with upserts, counters and changed-column updates; set-based updates and deletes; transactions, savepoints, row locks, advisory locks and batches.
- **Schema:** table sync with safe levels and drift detection, migrations, seeding, admin, backup and retention.
- **Caching:** query and entity caching with exact tag invalidation, warm queries and cross-node invalidation.
- **Replicas and resilience:** read replicas with lag probes, sticky reads and failover; retries and per-node circuit breakers.
- **Entity behaviour:** auditing, optimistic concurrency, multi-tenancy, global filters and interceptors.
- **Telemetry:** OpenTelemetry spans and metrics, slow-query plans and N+1 detection.
- **Bulk copy:** native on every provider.
- **Mapping:** a source generator (shipped in this package) writes it at build time, with no reflection, and CL.Data is Native AOT compatible.

**Measured:** within a few percent of raw ADO.NET and Dapper. Against CL.MySQL2 4.9 it uses 20–45% less memory, and cached reads take microseconds.

**Documentation:** [docs/libs/data](../docs/libs/data/index.md).
- [Parity with the 4.x libraries](PARITY.md)
- [Findings log](FINDINGS.md)
- [Changelog](CHANGELOG.md)
