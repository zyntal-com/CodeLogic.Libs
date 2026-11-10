# Changelog

## 5.0 (2026-11-10)

### Added

- New package: the SQLite provider for CodeLogic.Data (CL.Data), built on Microsoft.Data.Sqlite. Part of the CL.Data
  database framework that replaces CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- File snapshots through VACUUM INTO.

### Changed

- `Microsoft.Data.Sqlite` 11.0.0 (the released version; the previews used RC3).

- **Connection pragmas once per pooled connection (behaviour change):** `foreign_keys`, `synchronous` and `busy_timeout` are applied when a pooled native connection is new, not on every open, which cost about 3 µs and 1 KB per command. A key lookup takes 10.4 µs and 2.5 KB instead of 14.4 µs and 3.9 KB (raw ADO.NET: 7.7 µs, 1.7 KB). A connection that may have changed them gets them again at its next open: after raw SQL that is not one plain query, DML or DDL statement (a `PRAGMA`, `ATTACH`, several statements), after `WithConnectionAsync`, after a schema sync and after an interrupted command. Two databases on one file keep their own settings. With `pooling: false` every open applies them. The `cl.data.session.resets` metric counts the marked connections.

### Fixed

- The schema introspector always reads the primary.

### Documentation

- The README says when the pragmas apply: busy timeout, foreign keys and synchronous mode once per pooled native connection (and again after anything that may have changed them), WAL once per database file.
