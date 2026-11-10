# Changelog

## 5.0 (unreleased)

### Changed

- **No reset on every close (behaviour change):** the pool runs with Npgsql's `No Reset On Close`, and CL.Data resets a connection before it returns to the pool only when it may carry session state: a `ROLLBACK`, then the statements Npgsql uses when it has prepared statements (`CLOSE ALL`, `SET SESSION AUTHORIZATION DEFAULT`, `RESET ALL`, `UNLISTEN *`, `pg_advisory_unlock_all()`, `DISCARD SEQUENCES`, `DISCARD TEMP`), so auto-prepared statements stay valid. A CL.Data lookup by key against PostgreSQL 18 in Docker Desktop: 639 µs → 550 µs (8.3 KB → 4.8 KB with the lean execution changes).
- `ListenAsync`'s listener runs `UNLISTEN *` before its connection goes back to the pool.

### Added

- `PostgreSqlOptions.ConnectionReset` (`WhenDirty` default, `Always`, `Never`; overrides `No Reset On Close` in the connection string; ignored with multiplexing) and `PostgreSqlDataProvider.MayChangeSession` (PostgreSQL lexing; `@` is an operator, not a variable).

- New package: the PostgreSQL provider for CodeLogic.Data (CL.Data), built on Npgsql. Part of the CL.Data database
  framework that replaces CL.PostgreSQL in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- Restores move identity sequences past the restored values (setval).

### Fixed

- **Objects in `pg_temp` count as session state:** `CREATE TABLE pg_temp.x (...)` creates a temporary table without the `TEMP` keyword; with `connectionReset: "WhenDirty"` its connection went back to the pool without a reset.

- `NotifyAsync` (`pg_notify`) and `VacuumAnalyzeAsync` run on the primary as a side effect (`SqlIntent.SideEffect`). Before, they went through the read path: a notification could be sent on a replica (where it fails) or sent twice after a transient error.
- The schema introspector always reads the primary.
- `[PostgresTable]` hints are applied when a table is created: `Unlogged` gives `CREATE UNLOGGED TABLE`, `FillFactor` gives `WITH (fillfactor = n)`, `Tablespace` gives `TABLESPACE`. Before, they were ignored.
