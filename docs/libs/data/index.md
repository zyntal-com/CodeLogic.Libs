# CL.Data

> One database framework for MySQL, PostgreSQL, SQL Server and SQLite: typed LINQ queries, repositories, upserts, joins,
> grouping, paging, transactions, schema sync and migrations, unified caching, read replicas, resilience, OpenTelemetry
> and native bulk copy, with the same API and behaviour on every database.

| | |
|---|---|
| **Packages** | [`CodeLogic.Data`](https://www.nuget.org/packages/CodeLogic.Data) plus one provider: `CodeLogic.Data.MySql`, `CodeLogic.Data.PostgreSql`, `CodeLogic.Data.SqlServer`, `CodeLogic.Data.Sqlite` |
| **Library class** | `CL.Data.Hosting.DataLibrary`, plus each provider's library (`CL.Data.MySql.MySqlDataLibrary`, ...) |
| **Config files** | `config.data.json` (connections), `config.data.cache.json` (result cache) |
| **Target** | .NET 11 / CodeLogic 5, Native AOT compatible |
| **Result model** | `Result` and `Result<T>` for every expected failure (`data.*` error codes); exceptions only for bugs |

CL.Data replaces the 4.x libraries CL.MySQL2, CL.MSSQL, CL.PostgreSQL and CL.SQLite. Feature-by-feature mapping:
[PARITY.md](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/CL.Data/PARITY.md).

**More pages:**
- **[Providers](providers.md):** connection settings and what is specific to each database.
- **[Queries & writes](queries.md):** repositories, LINQ queries, joins, grouping, paging, transactions, raw SQL, compiled queries, auditing, tenancy and bulk copy.
- **[Caching](caching.md):** the result cache, the entity cache, warm queries and cross-node invalidation.
- **[Replicas & resilience](replicas.md):** read routing, lag probes, retries and circuit breakers.
- **[Schema & migrations](schema-migrations.md):** table sync, migrations, seeding, admin, backup and retention.
- **[Telemetry](telemetry.md):** OpenTelemetry spans and metrics, slow-query plans, N+1 detection, events and health.
- **[Performance](performance.md):** measured numbers, compiled queries, the source generator, Native AOT and budgets.

## Install and start

```bash
dotnet add package CodeLogic.Data.MySql      # brings CodeLogic.Data
```

**With the CodeLogic runtime:**

```csharp
using CL.Data.Hosting;
using CL.Data.MySql;

await Libraries.LoadAsync<DataLibrary>();
await Libraries.LoadAsync<MySqlDataLibrary>();   // registers the provider during Configure
await CodeLogic.CodeLogic.ConfigureAsync();
await CodeLogic.CodeLogic.StartAsync();

var data = Libraries.Get<DataLibrary>();
var db = data.Db().ValueOrThrow();               // the DefaultConnection; data.Db("reports") for another
```

**Without the runtime** (tests, tools, other hosts):

```csharp
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.MySql;

await using var db = Database.Create("main", MySqlDataProvider.Instance, new DataConnectionConfig
{
    Provider = "mysql", Host = "localhost", Database = "app", Username = "app", Password = "secret",
}).ValueOrThrow();
```

## Configuration

`config.data.json` holds the connections. Every setting has a default; only the provider and the target are required.

```json
{
  "enabled": true,
  "defaultConnection": "Main",
  "connections": {
    "Main": {
      "provider": "mysql",
      "host": "db1", "database": "app", "username": "app", "password": "secret",
      "syncMode": "Production",
      "replicas": [ { "name": "r1", "host": "db2" } ],
      "readPreference": "PreferReplica",
      "slowQueryThresholdMs": 500
    },
    "Local": { "provider": "sqlite", "database": "data/local.db" }
  }
}
```

**Settings worth knowing:**

| Group | Settings |
|---|---|
| Target | `provider`, `connectionString` or `host`/`port`/`database`/`username`/`password`, `failoverHosts`, `sslMode` |
| Pool & timeouts | `minPoolSize` (opened at start), `maxPoolSize`, `connectTimeoutSeconds`, `commandTimeoutSeconds` |
| Schema | `syncMode` (`Developer`, `Production`, `Migration`), `schemaSyncLevel` override, `createDatabaseIfMissing`, `namingPolicy` |
| Performance | `maxBatchSize`, `bulkThreshold`, `prepareStatements`, `cacheEnabled` (per-connection cache override) |
| Diagnostics | `slowQueryThresholdMs`, `captureExplainOnSlowQuery`, `nPlusOneThreshold`, `publishQueryEvents` |
| Resilience | `retryCount`, `retryBaseDelayMs`, `circuitBreakerThreshold`, `circuitBreakSeconds` |
| Replicas | `replicas[]`, `readPreference`, `stickyAfterWriteMs`, `maxReplicaLagMs`, `replicaProbeIntervalSeconds` |
| Provider options | `mySql`, `postgreSql`, `sqlServer`, `sqlite` (see [Providers](providers.md)) |

**Live changes:** changes made while running (admin UI, file watcher) apply without a restart. Only the connections that were added, changed or removed are touched, and the others keep their pools.

## Entities

Mark a class with `[Table]`. The source generator in `CodeLogic.Data` writes its mapping at build time, so no reflection is used.

```csharp
using CL.Data;

[Table(Name = "products")]
public sealed class Product
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 64, Unique = true)] public string Sku { get; set; } = "";
    [Column(Precision = 18, Scale = 2)] public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    [CreatedAt] public DateTimeOffset CreatedAt { get; set; }
    [UpdatedAt] public DateTimeOffset UpdatedAt { get; set; }
    [ConcurrencyToken] public int Version { get; set; }
    [SoftDelete] public DateTimeOffset? DeletedAt { get; set; }
}
```

**Attributes:**

| Purpose | Attributes |
|---|---|
| Tables and columns | `[Table]`, `[Column]` (`Primary`, `AutoIncrement`, `KeyOrder` for composite keys, `Size`, `Precision`/`Scale`, `DataType`, `Converter`, `PreviousName` for renames), `[Ignore]` |
| Indexes and keys | `[Index]`, `[CompositeIndex]`, `[ForeignKey]` |
| Behaviour | `[SoftDelete]`, `[ConcurrencyToken]`, `[CreatedAt]`, `[UpdatedAt]`, `[CreatedBy]`, `[UpdatedBy]`, `[TenantKey]`, `[RetainDays]` |
| Provider hints | `[MySqlTable]`/`[MySqlColumn]`, `[PostgresTable]`/`[PostgresColumn]`, `[SqlServerTable]`/`[SqlServerColumn]`, `[SqliteTable]`/`[SqliteColumn]` |
| Read-only rows | `[DataRow]` (query results that are not tables) |

**Analyzers:** mapping mistakes are build warnings, CLD001–CLD011 (for example no key, an unsupported type, or a composite key without `KeyOrder`). Lambdas CL.Data can't translate are CLD101–CLD103.

## Everyday use

```csharp
var products = db.Repository<Product>();
var created = (await products.InsertAsync(new Product { Sku = "A-1", Price = 9.95m })).ValueOrThrow();

var cheap = await db.Query<Product>()
    .Where(p => p.Price < 20m && p.Status == ProductStatus.Active)
    .OrderBy(p => p.Price)
    .Take(50)
    .ToListAsync();                                  // Result<List<Product>>

if (cheap.IsFailure) logger.Warning(cheap.Error!.Message);
```

**Errors and values:**
- Every operation returns `Result` or `Result<T>`. Check `IsFailure`, or call `ValueOrThrow()` / `ThrowIfFailure()` where a failure is a bug.
- Error codes are stable (`data.duplicate_key`, `data.concurrency`, `data.not_found`, `data.timeout`, ...).
- Captured values always become parameters, never SQL text.
