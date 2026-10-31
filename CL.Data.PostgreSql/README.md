# CodeLogic.Data.PostgreSql

> **Status: 5.0 preview.**

The PostgreSQL provider for [CodeLogic.Data](../CL.Data/README.md), built on Npgsql.

```bash
dotnet add package CodeLogic.Data.PostgreSql
```

- **With the CodeLogic runtime:** load `PostgreSqlDataLibrary` next to `DataLibrary`, and set `"provider": "postgresql"` on a connection in `config.data.json`.
- **Without it:** pass `PostgreSqlDataProvider.Instance` to `Database.Create`.

Bulk copy uses binary `COPY` with each column's real type; jsonb, uuid, timestamptz and bytea map natively; `postgreSql.defaultSchema` qualifies tables; replica lag comes from the replay LSN and timestamp; `failoverHosts` uses Npgsql multi-host. Extras: `db.Postgres.ListenAsync`, `NotifyAsync`, `VacuumAnalyzeAsync<T>()`.

Documentation: [Providers](../docs/libs/data/providers.md).
