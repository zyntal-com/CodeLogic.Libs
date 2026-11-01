# Changelog

## 5.0 (unreleased)

### Added

- New package: the SQL Server and Azure SQL provider for CodeLogic.Data (CL.Data), built on Microsoft.Data.SqlClient. Part
  of the CL.Data database framework that replaces CL.MSSQL in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.

### Changed

- `Encrypt` and `TrustServerCertificate` written in a connection string are no longer overridden by the `SqlServer` option defaults. An explicit `sslMode` still applies.

### Fixed

- `SqlFn.BucketUtc` works for dates after January 2038. Before, SQL Server failed with an arithmetic overflow (error 8115), because `DATEADD` takes an `int` number of seconds.
- `UpdateStatisticsAsync` runs on the primary as a side effect (`SqlIntent.SideEffect`). Before, it went through the read path and could be sent to a replica.
- The schema introspector always reads the primary.
