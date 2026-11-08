# Changelog

## 5.0 (unreleased)

### Added

- New package: the SQLite provider for CodeLogic.Data (CL.Data), built on Microsoft.Data.Sqlite. Part of the CL.Data
  database framework that replaces CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- File snapshots through VACUUM INTO.

### Fixed

- The schema introspector always reads the primary.

### Documentation

- The README says when the pragmas apply: busy timeout, foreign keys and synchronous mode on every pooled connection, WAL once per database file.
