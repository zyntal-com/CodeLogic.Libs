# Changelog

## 5.0 (unreleased)

### Breaking

- `MySqlOptions.ConnectionReset` is a `ConnectionResetMode` (was `MySqlConnectionReset`); configuration values are unchanged.

### Changed

- **No reset round trip on every open (behaviour change):** the pool runs with MySqlConnector's `ConnectionReset=false`, and CL.Data resets a connection (`COM_RESET_CONNECTION`, then `SET NAMES utf8mb4` and the database restored) before it returns to the pool only when it may carry session state. Generated queries and writes skip the reset, which halves point-lookup latency against a remote server (get by key against MySQL in Docker Desktop: 1,166 µs → 622 µs, and about 2 KB less allocated per call). Statements that user code prepared through `WithConnectionAsync` keep working: the driver then runs its full reset at the next open.

### Added

- `MySqlOptions.ConnectionReset` (`WhenDirty` default, `Always`, `Never`; overrides a `ConnectionReset` key in the connection string) and `MySqlOptions.ConnectionIdleTimeoutSeconds` (default 180).
- `MySqlDataProvider.MayChangeSession`: the raw-SQL rules (neutral: one `SELECT`/`WITH`/`VALUES`/`TABLE`/`SHOW`/`EXPLAIN`/`DESCRIBE`/`INSERT`/`REPLACE`/`UPDATE`/`DELETE` or DDL statement without user variables, `GET_LOCK`, `TEMPORARY`, `LAST_INSERT_ID`, `SQL_LOG_BIN` or executable comments).

- New package: the MySQL and MariaDB provider for CodeLogic.Data (CL.Data), built on MySqlConnector. Part of the CL.Data
  database framework that replaces CL.MySQL2 in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- `MySqlDatabaseAdmin` (information_schema.SCHEMATA; CREATE DATABASE with the connection charset/collation); batched deletes with DELETE ... LIMIT.

### Fixed

- The package depends on CodeLogic `[5.0.35-preview, 6.0.0)`; the old `[5.0.0-preview, 6.0.0)` floor let NuGet pick the unsigned 5.0.28-preview (CS0012).
- `OptimizeTableAsync` and `AnalyzeTableAsync` run on the primary as a side effect (`SqlIntent.SideEffect`). Before, they went through the read path and could be sent to a replica or retried.
- The schema introspector always reads the primary.
