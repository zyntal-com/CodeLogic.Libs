# CodeLogic.Data.MySql

The MySQL and MariaDB provider for [CodeLogic.Data](../CL.Data/README.md), built on MySqlConnector.

```bash
dotnet add package CodeLogic.Data.MySql
```

- **With the CodeLogic runtime:** load `MySqlDataLibrary` next to `DataLibrary`, and set `"provider": "mysql"` on a connection in `config.data.json`.
- **Without it:** pass `MySqlDataProvider.Instance` to `Database.Create`.

Bulk copy uses `MySqlBulkCopy` (set `mySql.allowLoadLocalInfile` and the server's `local_infile`); slow-query plans use `EXPLAIN FORMAT=TREE`; replica lag comes from `SHOW REPLICA STATUS`; `failoverHosts` enables driver failover. Extras: `db.MySql.OptimizeTableAsync<T>()`, `AnalyzeTableAsync<T>()`.

Pooled connections skip MySqlConnector's reset on every open (one round trip per command). CL.Data resets a connection
before it goes back to the pool only when it may carry session state: raw `SET`/`CALL`/transaction control/user
variables/locks/temporary tables, `WithConnectionAsync`, interrupted commands. `mySql.connectionReset` (`WhenDirty`,
`Always`, `Never`) chooses; keep `mySql.connectionIdleTimeoutSeconds` below any proxy's idle timeout.

Documentation: [Providers](../docs/libs/data/providers.md).
