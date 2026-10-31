# CodeLogic.Data

> **Status: in development (5.0 preview).** The API is not stable yet.

CodeLogic.Data is the database framework of the CodeLogic 5 libraries. It replaces CL.MySQL2, CL.MSSQL,
CL.PostgreSQL and CL.SQLite with one core package plus one small package per database:

| Package | Database | Driver |
|---|---|---|
| `CodeLogic.Data` | core (no driver) | — |
| `CodeLogic.Data.MySql` | MySQL / MariaDB | MySqlConnector |
| `CodeLogic.Data.PostgreSql` | PostgreSQL | Npgsql |
| `CodeLogic.Data.SqlServer` | SQL Server / Azure SQL | Microsoft.Data.SqlClient |
| `CodeLogic.Data.Sqlite` | SQLite | Microsoft.Data.Sqlite |

Every provider supports the same features:
- typed LINQ queries, joins, grouping with HAVING, and projections
- repositories with upserts
- cursor paging and streaming
- transactions and row locks
- schema sync and migrations, seeding, retention
- unified caching with tag invalidation
- read replicas
- optimistic concurrency, auditing, multi-tenancy
- OpenTelemetry tracing and metrics
- native bulk copy

Entity mapping is produced at compile time by a source generator that ships inside this package, so
mapping uses no reflection and works with Native AOT.
