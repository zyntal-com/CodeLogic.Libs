# Providers

Every provider implements the same API and passes the same conformance suite. This page covers what still differs:
connection settings, provider options, storage choices and the few provider-only operations.

| | MySQL / MariaDB | PostgreSQL | SQL Server / Azure SQL | SQLite |
|---|---|---|---|---|
| Package | `CodeLogic.Data.MySql` | `CodeLogic.Data.PostgreSql` | `CodeLogic.Data.SqlServer` | `CodeLogic.Data.Sqlite` |
| Library | `MySqlDataLibrary` | `PostgreSqlDataLibrary` | `SqlServerDataLibrary` | `SqliteDataLibrary` |
| `"provider"` | `mysql` | `postgresql` | `sqlserver` | `sqlite` |
| Driver | MySqlConnector | Npgsql | Microsoft.Data.SqlClient | Microsoft.Data.Sqlite |
| Upsert | `INSERT ... ON DUPLICATE KEY UPDATE` | `ON CONFLICT` | serializable UPDATE + INSERT | `ON CONFLICT` |
| Generated values | last insert id | `RETURNING` | `OUTPUT INSERTED` | `RETURNING` |
| Native bulk copy | `MySqlBulkCopy` (needs local infile) | `COPY ... (FORMAT BINARY)` | `SqlBulkCopy` | prepared inserts in one transaction |
| Slow-query plan | `EXPLAIN FORMAT=TREE` | `EXPLAIN` | `SET SHOWPLAN_TEXT` | `EXPLAIN QUERY PLAN` |
| Replica lag probe | `SHOW REPLICA STATUS` | replay LSN / timestamp | `sys.dm_hadr_database_replica_states` | none (no replicas) |
| Schema lock | `GET_LOCK` | `pg_advisory_lock` | `sp_getapplock` | lock table |
| Parameter limit | 65,535 | 65,535 | 2,098 | 32,766 |

A statement over its provider's parameter limit fails with `data.parameter_limit` before anything is sent. Batched inserts
size themselves to stay under the limit.

## MySQL

```json
{ "provider": "mysql", "host": "db1", "database": "app", "username": "app", "password": "secret",
  "failoverHosts": ["db1b"],
  "mySql": { "allowLoadLocalInfile": true, "characterSet": "utf8mb4", "collation": "utf8mb4_0900_ai_ci", "defaultEngine": "InnoDB" } }
```

- **Failover:** `failoverHosts` turns on MySqlConnector's failover load balancing for the primary.
- **Storage:** GUIDs are stored as `BINARY(16)`, and dates are UTC. `[MySqlTable]` and `[MySqlColumn]` set the engine, charset and collation per table or column.
- **Bulk copy** needs `allowLoadLocalInfile` and the server's `local_infile`. When either is off, CL.Data batches multi-row inserts instead, and it remembers when the server refuses.
- **Lag probe:** it needs the `REPLICATION CLIENT` privilege. Without it, the lag is unknown and the replica counts as usable.
- **MySQL-only operations:**

```csharp
using CL.Data.MySql;
var messages = (await db.MySql.OptimizeTableAsync<Product>()).ValueOrThrow();   // OPTIMIZE TABLE, per-table messages
await db.MySql.AnalyzeTableAsync<Product>();
```

## PostgreSQL

```json
{ "provider": "postgresql", "host": "pg1", "database": "app", "username": "app", "password": "secret",
  "failoverHosts": ["pg2"],
  "postgreSql": { "defaultSchema": "public", "maxAutoPrepare": 50, "multiplexing": false } }
```

- **Failover:** `failoverHosts` maps to Npgsql multi-host with `TargetSessionAttributes=primary`.
- **Tables** are qualified with `defaultSchema` unless `[Table(Schema = ...)]` says otherwise.
- **Types:** jsonb, uuid, timestamptz and bytea map natively. Other types (inet, interval, arrays, ranges) work through `[PostgresColumn(Type = ...)]`, with values bound as-is. LINQ has no array or range operators yet.
- **PostgreSQL-only operations:**

```csharp
using CL.Data.PostgreSql;
await using var listener = (await db.Postgres.ListenAsync("orders", n => HandleAsync(n.Payload))).ValueOrThrow();
await db.Postgres.NotifyAsync("orders", "42");
await db.Postgres.VacuumAnalyzeAsync<Product>();   // outside transactions only
```

`ListenAsync` holds a dedicated connection until you dispose the listener.

## SQL Server

```json
{ "provider": "sqlserver", "host": "sql1", "database": "app", "username": "app", "password": "secret",
  "sqlServer": { "authentication": "SqlPassword", "encrypt": true, "trustServerCertificate": false,
                 "multiSubnetFailover": true, "failoverPartner": null, "applicationName": "my-app" } }
```

- **Authentication:** `SqlPassword`, `Integrated`, `EntraDefault`, `EntraManagedIdentity` or `EntraServicePrincipal`.
- **TLS:** `Encrypt` and `TrustServerCertificate` written in a connection string win. Otherwise the `sqlServer` options apply (encrypted by default). An explicit `sslMode` overrides both.
- **Replicas** are reached with `ApplicationIntent=ReadOnly` (an availability group listener). The lag probe needs `VIEW SERVER STATE`.
- **Storage:** JSON columns are `nvarchar(max)` with `CHECK (ISJSON(col) = 1)`. `rowversion` concurrency tokens are native. Explicit identity values are inserted with `IDENTITY_INSERT`.
- **SQL Server-only operations:**

```csharp
using CL.Data.SqlServer;
await db.SqlServer.UpdateStatisticsAsync<Product>(fullScan: true);
```

## SQLite

```json
{ "provider": "sqlite", "database": "data/app.db",
  "sqlite": { "journalMode": "Wal", "synchronous": "Normal", "busyTimeoutMs": 5000, "foreignKeys": true, "createFile": true } }
```

- **Connections:** PRAGMAs are applied to every pooled connection. `:memory:` databases use a shared cache, so every connection sees the same data.
- **Storage:** decimals have NUMERIC affinity, dates are UTC ISO-8601 text, and GUIDs are 16-byte BLOBs.
- **Emulated behaviour:**
  - Row locks are emulated with `BEGIN IMMEDIATE`.
  - Schema changes SQLite can't `ALTER` rebuild the table, with foreign keys checked afterwards.
  - `db.Backup` can make a consistent copy with `VACUUM INTO`.
- **No replicas:** a configuration with replicas is rejected.

## Writing a provider

A provider derives from `CL.Data.Providers.DataProvider` and supplies:
- **Required:** a dialect (`SqlDialect`), a type mapper, an error classifier, a connection-string builder and a data source.
- **Optional:** schema introspection, DDL generation, advisory locks, bulk copy (`BulkCopier`), explain (`QueryExplainer`), a replica probe (`ReplicaProbe`) and database admin.

To make it available, register it with `DataProviderRegistry.Register(...)`, or ship a small `ILibrary` that registers it during Configure and declares `Dependencies = [LibraryDependency.Required("CL.Data")]`. Then run the conformance suites (`tests/Data.Conformance`) against it.
