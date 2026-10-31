# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **MySQL SQL dialect:**
  - Backtick identifiers, and `CONCAT` instead of `||`.
  - Row-alias upserts (`AS excluded ON DUPLICATE KEY UPDATE`). Insert-ignore is a no-op key assignment, so only
    duplicates are ignored.
  - Native `DELETE ... LIMIT` and MySQL date functions.
  - JSON through `JSON_EXTRACT`, `JSON_CONTAINS_PATH` and `JSON_CONTAINS`; full text through `MATCH ... AGAINST`.
  - `!` as the LIKE escape, so `NO_BACKSLASH_ESCAPES` cannot change its meaning.
- **MySQL provider (MySqlConnector 2):**
  - **Connections:** connection strings come from the settings, with driver failover across `FailoverHosts`.
  - **Driver options CL.Data sets:**
    - affected rows, so insert-ignore detects duplicates;
    - `BINARY(16)` GUIDs in RFC byte order, so version-7 GUIDs sort by time;
    - date/times read as UTC.
  - **Type mapping:** `TINYINT(1)` booleans, `DATETIME(6)`, native `JSON`, and UNSIGNED through `[MySqlColumn]`.
  - **Errors:** duplicate key 1062, foreign keys, deadlock 1213 (transient), lock wait timeout 1205 (transient), NOWAIT
    3572, access denied, connection loss and read-only failover.
  - **Identity:** the last-insert-id fills generated identities (consecutive for multi-row inserts).
  - **Registration:** `MySqlDataLibrary` registers the provider.
  - **Tests:** the full conformance suite is green on MySQL 8.4.
- `MySqlDdlGenerator`: AUTO_INCREMENT, ENGINE/CHARSET/COLLATE from `[MySqlTable]` or the connection, MODIFY COLUMN, DROP INDEX ... ON, DROP FOREIGN KEY.
