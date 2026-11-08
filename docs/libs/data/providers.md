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
| Schema lock | `GET_LOCK` | `pg_advisory_lock` | `sp_getapplock` | none (run sync and migrations from one process) |
| Parameter limit | 65,535 | 65,535 | 2,098 | 32,766 |

A statement over its provider's parameter limit fails with `data.parameter_limit` before anything is sent. Batched inserts
size themselves to stay under the limit.

## MySQL

```json
{ "provider": "mysql", "host": "db1", "database": "app", "username": "app", "password": "secret",
  "failoverHosts": ["db1b"],
  "mySql": { "allowLoadLocalInfile": true, "characterSet": "utf8mb4", "collation": "utf8mb4_0900_ai_ci", "defaultEngine": "InnoDB",
             "connectionReset": "WhenDirty", "connectionIdleTimeoutSeconds": 180 } }
```

- **Failover:** `failoverHosts` turns on MySqlConnector's failover load balancing for the primary.
- **Connection reset:** MySqlConnector normally resets every pooled connection when it is opened, which costs one full
  round trip on every command (it roughly doubles the latency of a lookup by key). With `connectionReset: "WhenDirty"`
  (the default), the pool skips that reset and CL.Data resets a connection itself, before it returns to the pool, only
  when the connection may carry session state:
  - raw SQL (`SqlQueryAsync`, `ExecuteSqlAsync`, `DataBatch.Sql`, migrations) that is not one plain `SELECT`, `WITH`,
    `SHOW`, `INSERT`, `REPLACE`, `UPDATE`, `DELETE` or DDL statement: `SET`, `CALL`, `DO`, `START TRANSACTION`, `LOCK TABLES`,
    `USE`, `PREPARE`, user variables (`@v`), `GET_LOCK`, temporary tables, executable comments (`/*! */`) or several
    statements;
  - every connection handed to `WithConnectionAsync`;
  - an advisory lock that was not released cleanly, and SQL changed by an interceptor (checked by the same rules);
  - a command, stream, bulk copy or commit interrupted by anything but a plain statement error (a duplicate key or
    constraint violation does not count).

  The reset (`COM_RESET_CONNECTION`) drops user variables, session settings, temporary tables, locks, prepared statements
  and open transactions at once, so nothing lingers while the connection idles. CL.Data then restores the character set
  and the database. `connectionReset: "Always"` keeps the driver's reset on every open (the behaviour of earlier 5.0 previews), and
  `"Never"` turns resets off completely; only use it when nothing ever changes session state. The option overrides a
  `ConnectionReset` key in `connectionString`. The `cl.data.session.resets` metric counts CL.Data's resets.
  - **Limit:** a stored function that changes session state from inside a plain `SELECT` can't be seen. Use `Always` if
    you have such functions.
  - **Dead connections:** without the reset on open, nothing checks a pooled connection before it is reused. A connection
    that a proxy, load balancer or server dropped while idle fails on its first command. Reads are retried
    (`retryCount`); a write reports `data.unavailable`. Keep `connectionIdleTimeoutSeconds` (default 180) below the idle
    timeout of anything between the application and the server, or use `Always` behind a proxy that kills idle
    connections early.
- **Storage:** GUIDs are stored as `BINARY(16)`, and dates are UTC. `[MySqlTable]` sets the engine, charset, collation and row format per table; `[MySqlColumn]` sets the collation (which implies the character set) per column. `[MySqlColumn(Charset)]` is not applied yet.
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
  "postgreSql": { "defaultSchema": "public", "maxAutoPrepare": 50, "multiplexing": false, "connectionReset": "WhenDirty" } }
```

- **Connection reset:** Npgsql normally resets a pooled connection on every close (`DISCARD ALL` or its equivalent, sent
  with the next command), which is extra server work on every command. With `connectionReset: "WhenDirty"` (the default)
  that reset is skipped and CL.Data resets a connection only when it may carry session state: raw SQL that is not one plain
  query, DML or DDL statement (`SET`, `RESET`, `BEGIN`, `LOCK`, `LISTEN`, `PREPARE`, `DO`, `CALL`, `set_config`, advisory
  locks, temporary tables, several statements), connections handed to `WithConnectionAsync`, locks not released cleanly
  and interrupted commands. The reset rolls back an open transaction and runs the statements Npgsql uses when it has
  prepared statements, so auto-prepared statements stay valid. `"Always"` keeps Npgsql's reset; `"Never"` turns resets off.
  The option overrides `No Reset On Close` in `connectionString` and is ignored with `multiplexing`. A lookup by key took
  594 µs with the reset and 543 µs without it, against PostgreSQL 18 in Docker Desktop.

- **Failover:** `failoverHosts` maps to Npgsql multi-host with `TargetSessionAttributes=primary`.
- **Tables** are qualified with `defaultSchema` unless `[Table(Schema = ...)]` says otherwise.
- **Table hints:** `[PostgresTable(Unlogged = true, FillFactor = 70, Tablespace = "fast")]` shape `CREATE TABLE` (`UNLOGGED`, `WITH (fillfactor = 70)`, `TABLESPACE`). They apply when sync creates the table; existing tables are not altered.
- **Types:** jsonb, uuid, timestamptz and bytea map natively. Other types (inet, interval, arrays, ranges) work through `[PostgresColumn(Type = ...)]`, with values bound as-is. LINQ has no array or range operators yet.
- **PostgreSQL-only operations:**

```csharp
using CL.Data.PostgreSql;
await using var listener = (await db.Postgres.ListenAsync("orders", n => HandleAsync(n.Payload))).ValueOrThrow();
await db.Postgres.NotifyAsync("orders", "42");
await db.Postgres.VacuumAnalyzeAsync<Product>();   // outside transactions only
```

`ListenAsync` holds a dedicated connection until you dispose the listener. `NotifyAsync` and `VacuumAnalyzeAsync` always run on
the primary and are never retried once sent (as are the MySQL and SQL Server maintenance operations).

## SQL Server

```json
{ "provider": "sqlserver", "host": "sql1", "database": "app", "username": "app", "password": "secret",
  "sqlServer": { "authentication": "SqlPassword", "encrypt": true, "trustServerCertificate": false,
                 "multiSubnetFailover": true, "failoverPartner": null, "applicationName": "my-app" } }
```

- **Authentication:** `SqlPassword`, `Integrated`, `EntraDefault`, `EntraManagedIdentity` or `EntraServicePrincipal`.
- **TLS:** `Encrypt` and `TrustServerCertificate` written in a connection string win. Otherwise the `sqlServer` options apply (encrypted by default). An explicit `sslMode` overrides both. `sslCaCertificatePath` is rejected: SqlClient trusts the machine's certificate store only, so install the CA there, or pin the server certificate with `Server Certificate=<path>` in the connection string.
- **Replicas** are reached with `ApplicationIntent=ReadOnly` (an availability group listener). The lag probe needs `VIEW SERVER STATE`.
- **Storage:** JSON columns are `nvarchar(max)` with `CHECK (ISJSON(col) = 1)`. `rowversion` concurrency tokens are native. Explicit identity values are inserted with `IDENTITY_INSERT`. `[SqlServerTable(FileGroup = "FG1", DataCompression = "PAGE")]` adds `ON [FG1] WITH (DATA_COMPRESSION = PAGE)` when sync creates the table. `[SqlServerColumn(Sparse)]` is not applied yet.
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
- **No advisory locks:** `AcquireLockAsync` returns `data.unsupported`, and schema sync, migrations and seeds run without a lock. Run them from one process at a time.

## Writing a provider

A provider derives from `CL.Data.Providers.DataProvider` and supplies:
- **Required:** a dialect (`SqlDialect`), a type mapper, an error classifier, a connection-string builder and a data source.
- **Optional:** schema introspection, DDL generation, advisory locks, bulk copy (`BulkCopier`), explain (`QueryExplainer`), a replica probe (`ReplicaProbe`) and database admin.

To make it available, register it with `DataProviderRegistry.Register(...)`, or ship a small `ILibrary` that registers it during Configure and declares `Dependencies = [LibraryDependency.Required("CL.Data")]`. Then run the conformance suites (`tests/Data.Conformance`) against it.
