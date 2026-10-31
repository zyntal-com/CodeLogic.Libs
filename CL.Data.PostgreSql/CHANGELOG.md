# Changelog

## 5.0 (unreleased)

### Added

- New package, part of the CL.Data database framework that replaces CL.MySQL2, CL.MSSQL, CL.PostgreSQL and
  CL.SQLite in the CodeLogic 5 line. Targets .NET 11 and CodeLogic 5.
- **PostgreSQL SQL dialect:** integer date parts, UTC dates for `timestamptz`, `jsonb_path_*` and `@>` JSON functions,
  full text through `websearch_to_tsquery`, and `ON CONFLICT` upserts with `RETURNING`.
- **PostgreSQL provider (Npgsql 10):**
  - **Connections:** connection strings come from the settings. Multi-host primaries use
    `TargetSessionAttributes=primary`; auto-prepare follows `PrepareStatements`; multiplexing and TLS are available.
  - **Type mapping:** native `boolean`/`uuid`/`jsonb`/`bytea`, `timestamptz` holding UTC, and `serial` identities.
  - **Parameters:** JSON parameters are typed `jsonb`; time values are bound as UTC (Npgsql rejects other offsets).
  - **Errors:** mapped by SQLSTATE. Unique, foreign key and check constraints; deadlocks and serialization failures
    are transient and retried; NOWAIT; read-only standby; connection loss.
  - **Registration:** `PostgreSqlDataLibrary` registers the provider. The default schema comes from
    `PostgreSql.DefaultSchema` (`public`).
  - **Tests:** the full conformance suite is green on PostgreSQL 18.
- `PostgreSqlDdlGenerator`: identity columns, ALTER COLUMN ... TYPE ... USING, native INCLUDE.
