# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **PostgreSQL SQL dialect:** integer date parts, UTC dates for `timestamptz`, `jsonb_path_*` and `@>` JSON functions,
  full text through `websearch_to_tsquery`, and `ON CONFLICT` upserts with `RETURNING`.
