# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **SQLite provider:**
  - Pooled connections with per-connection PRAGMAs (foreign keys, synchronous, busy timeout). WAL is set once per
    file. `:memory:` databases are shared across pooled connections.
  - Type mapping: decimals are NUMERIC so they sort numerically, GUIDs are 16-byte BLOBs, and time values are UTC
    ISO-8601 text so text order is time order.
  - Errors are mapped from SQLite result codes: busy/locked → `data.lock_timeout` (transient), unique/primary key →
    `data.duplicate_key`, foreign key → `data.foreign_key_violation`, read-only/auth → `data.access_denied`.
  - `SqliteDataLibrary` registers the provider with CodeLogic.
- **SQLite SQL dialect:**
  - `strftime` date parts over UTC ISO-8601 text.
  - Floor and ceiling built from casts, since math functions are not always compiled in. Multi-argument `MIN`/`MAX`.
  - JSON through `json_extract`, `json_type` and `json_each`.
  - Row locks render nothing; transactions take the write lock instead.
  - Full-text search is not a capability, because it needs an FTS5 table.
- `SqliteDdlGenerator`: INTEGER PRIMARY KEY AUTOINCREMENT, inline foreign keys, WITHOUT ROWID/STRICT; type, column-drop and foreign-key changes rebuild the table.
