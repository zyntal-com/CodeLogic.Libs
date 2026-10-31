# CodeLogic.Data.SqlServer

> **Status: 5.0 preview.**

The SQL Server and Azure SQL provider for [CodeLogic.Data](../CL.Data/README.md), built on Microsoft.Data.SqlClient.

```bash
dotnet add package CodeLogic.Data.SqlServer
```

- **With the CodeLogic runtime:** load `SqlServerDataLibrary` next to `DataLibrary`, and set `"provider": "sqlserver"` on a connection in `config.data.json`.
- **Without it:** pass `SqlServerDataProvider.Instance` to `Database.Create`.

SQL, integrated and Entra ID authentication; bulk copy uses `SqlBulkCopy`; JSON columns get an `ISJSON` check; replicas use `ApplicationIntent=ReadOnly` and report lag from `sys.dm_hadr_database_replica_states`; `multiSubnetFailover` and `failoverPartner` for failover. Extras: `db.SqlServer.UpdateStatisticsAsync<T>()`.

Documentation: [Providers](../docs/libs/data/providers.md).
