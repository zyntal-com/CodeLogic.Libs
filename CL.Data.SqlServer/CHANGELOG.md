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
