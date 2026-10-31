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
