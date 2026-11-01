# Changelog

## 5.0 (unreleased)

### Added

- New package: the PostgreSQL provider for CodeLogic.Data (CL.Data), built on Npgsql. Part of the CL.Data database
  framework that replaces CL.PostgreSQL in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- Restores move identity sequences past the restored values (setval).

### Fixed

- `NotifyAsync` (`pg_notify`) and `VacuumAnalyzeAsync` run on the primary as a side effect (`SqlIntent.SideEffect`). Before, they went through the read path: a notification could be sent on a replica (where it fails) or sent twice after a transient error.
- The schema introspector always reads the primary.
- `[PostgresTable]` hints are applied when a table is created: `Unlogged` gives `CREATE UNLOGGED TABLE`, `FillFactor` gives `WITH (fillfactor = n)`, `Tablespace` gives `TABLESPACE`. Before, they were ignored.
