# Changelog

## 5.0 (unreleased)

### Added

- New package: the MySQL and MariaDB provider for CodeLogic.Data (CL.Data), built on MySqlConnector. Part of the CL.Data
  database framework that replaces CL.MySQL2 in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- `MySqlDatabaseAdmin` (information_schema.SCHEMATA; CREATE DATABASE with the connection charset/collation); batched deletes with DELETE ... LIMIT.

### Fixed

- `OptimizeTableAsync` and `AnalyzeTableAsync` run on the primary as a side effect (`SqlIntent.SideEffect`). Before, they went through the read path and could be sent to a replica or retried.
- The schema introspector always reads the primary.
