# CodeLogic.Data.Sqlite

> **Status: 5.0 preview.**

The SQLite provider for [CodeLogic.Data](../CL.Data/README.md), built on Microsoft.Data.Sqlite.

```bash
dotnet add package CodeLogic.Data.Sqlite
```

- **With the CodeLogic runtime:** load `SqliteDataLibrary` next to `DataLibrary`, and set `"provider": "sqlite"` on a connection in `config.data.json`.
- **Without it:** pass `SqliteDataProvider.Instance` to `Database.Create`.

WAL, busy timeout and foreign keys are set on every pooled connection; schema changes SQLite cannot ALTER rebuild the table; bulk copy runs prepared inserts in one transaction; `db.Backup.SnapshotAsync` copies the file with `VACUUM INTO`. No replicas.

Documentation: [Providers](../docs/libs/data/providers.md).
