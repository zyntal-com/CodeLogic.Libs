# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **SQL Server SQL dialect:**
  - Booleans are `1`/`0`, and conditions become `CASE` in value positions.
  - Paging uses `TOP`, or `OFFSET ... FETCH` with an `ORDER BY`. Locks are table hints (`UPDLOCK`, `READPAST`, `NOWAIT`).
  - Writes return values with `OUTPUT INSERTED`; limited deletes use `DELETE TOP`.
  - `LEAST`/`GREATEST` are emulated for SQL Server 2019.
  - Upserts are a two-statement `UPDLOCK, SERIALIZABLE` batch that runs in a transaction.
- **SQL Server provider (Microsoft.Data.SqlClient 7):**
  - **Connections:** SQL login, Windows integrated or Microsoft Entra ID authentication (default credential, managed
    identity, service principal); encryption, multi-subnet failover and failover partner; replicas use
    `ApplicationIntent=ReadOnly`.
  - **Type mapping:** `bit`, `datetime2`, `datetimeoffset` holding UTC, `uniqueidentifier`, and `nvarchar(max)` for
    JSON and text.
  - **Parameters:** `datetime2` (not the 3.33 ms `datetime`), and strings sized in 4000/max buckets so the server
    reuses query plans.
  - **Errors:** duplicate key (2627/2601), foreign key and check constraints (547 is split by message), deadlock and
    snapshot conflicts (transient), lock timeout, login and permission, and Azure SQL transient errors.
  - **Generated keys:** `OUTPUT` does not promise row order, so inserts with generated keys go one row per statement.
  - **Registration:** `SqlServerDataLibrary` registers the provider.
  - **Tests:** the full conformance suite is green on SQL Server 2019. Turning on `READ_COMMITTED_SNAPSHOT` is
    recommended so readers do not block on writers.
  - **Breaking:** Entra ID password authentication is not offered; Microsoft has deprecated it.
