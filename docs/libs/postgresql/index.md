# CL.PostgreSQL

> A typed data-access layer for PostgreSQL — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

`CL.PostgreSQL` maps a plain class with attributes, keeps the live table in shape, generates reflection-free row mappers, translates LINQ-shaped expressions to real SQL, and caches results with version-stamped invalidation. It builds on [Npgsql](https://www.nuget.org/packages/Npgsql). Every fallible operation returns a framework `Result<T>` — no exceptions for the expected failure paths.

It shares its architecture with [`CL.MySQL2`](../mysql2/index.md) and [`CL.MSSQL`](../mssql/index.md), so the API is nearly identical across the three. Where PostgreSQL genuinely differs — `ON CONFLICT` arbitration, schemas, case-sensitive identifiers — the difference is called out rather than papered over. See [Dialect notes](#dialect-notes) below.

| | |
|---|---|
| **Package** | [`CodeLogic.PostgreSQL`](https://www.nuget.org/packages/CodeLogic.PostgreSQL) |
| **Library class** | `CL.PostgreSQL.PostgreSQLLibrary` |
| **Config files** | `config.postgresql.json` · `config.postgresql.cache.json` |
| **Dependencies** | Npgsql 9.x |
| **Engines** | PostgreSQL 12+ |

This overview covers loading, the entry points, and configuration. The deep material lives on three sub-pages:

- **[Query Builder](queries.md)** — `Where` / subquery filters / ordering / paging / cursor paging / joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Migrations](schema-migrations.md)** — entity attributes, `SyncMode` & `SchemaSyncLevel`, `SyncTableAsync` / `SyncSchemaAsync`, the CRC sentinel, soft delete, retention, imperative migrations, backups & restore.
- **[Performance & Caching](performance.md)** — the result cache, time quantization, table-version invalidation, `SmartCachePool`, multi-node coordination, transient retry, the N+1 detector, slow-query / `EXPLAIN`, compiled materializers, projection pushdown.

## Install & load

```bash
dotnet add package CodeLogic.PostgreSQL
```

```csharp
using CL.PostgreSQL;

await Libraries.LoadAsync<PostgreSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var pg = Libraries.Get<PostgreSQLLibrary>()!;
```

Set your connection in `config.postgresql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.PostgreSQL.Models`. The full attribute set is documented on the [Schema & Migrations](schema-migrations.md) page.

```csharp
using CL.PostgreSQL.Models;

[Table(Name = "users", Schema = "public")]
public sealed class User
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "email", Size = 160, Unique = true, NotNull = true)]
    public string Email { get; set; } = "";

    // No DataType: inferred from the CLR type. Guid -> uuid, DateTime -> timestamptz.
    [Column(Name = "external_id")]
    public Guid ExternalId { get; set; }

    [Column(Name = "created_utc", DefaultValue = "now()", Index = true)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Column(Name = "settings", DataType = DataType.Jsonb)]
    public string? Settings { get; set; }
}
```

Leaving `DataType` off is the common case: the CLR property type decides, and it picks the
native PostgreSQL type rather than a lowest-common-denominator one. Set it explicitly when
you want something the CLR type does not imply, such as `Jsonb` for a `string`.

## Entry points

| Member | Purpose |
|---|---|
| `SyncTableAsync<T>()` | Reconcile one table to the entity definition. |
| `SyncSchemaAsync(params Type[])` | Reconcile several entities in one pass. |
| `GetRepository<T>()` | Typed CRUD: insert, batched bulk insert, upsert, update, delete, paging, increment. |
| `Query<T>()` | Fluent query builder: filters, ordering, joins, projections, aggregates, cursor paging. |
| `SqlQueryAsync<T>()` / `SqlScalarAsync<T>()` / `ExecuteSqlAsync()` | Parameterised raw SQL. |
| `BeginTransactionAsync()` | An `await using` scope that rolls back unless committed. |
| `Migrations` | The migration runner: `MigrateAsync`, `RollbackAsync`, `GetPendingAsync`. |
| `ConnectionManager.RegisterConfiguration(config, id)` | Add a connection at runtime. |
| `HealthCheckAsync()` | Per-connection health for the framework's health endpoint. |
| `GetCacheStats()` / `GetCachePoolStats()` | Cache counters. |

```csharp
await pg.SyncTableAsync<User>();

var repo = pg.GetRepository<User>();
var created = await repo.InsertAsync(new User { Email = "ada@example.com" });

// Idempotent insert: a duplicate unique/primary key is a normal outcome, not an error
// (INSERT ... ON CONFLICT DO NOTHING). Any other error rolls back everything the call wrote
// (own transaction, or a savepoint inside an explicit transaction scope), so a failure Result
// means nothing was written and the caller's transaction stays usable.
Result<InsertOutcome> outcome = await repo.InsertIgnoreAsync(evt);      // Inserted | Duplicate
Result<int>           added   = await repo.InsertManyIgnoreAsync(batch); // rows actually inserted

var recent = await pg.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(20)
    .ToListAsync();
```

## Multi-database

`Databases` is a named map. Every entry point takes an optional `connectionId` that defaults
to `"Default"`.

```csharp
var reporting = pg.GetRepository<User>("Reporting");
var rows = await pg.Query<User>("Reporting").Where(u => u.Email != null).ToListAsync();
```

Register one at runtime instead of in config:

```csharp
pg.ConnectionManager.RegisterConfiguration(new PostgreSqlDatabaseConfig
{
    Host = "tenant42.db.internal",
    Database = "tenant42",
    Username = "app",
    Password = secret,
    SslMode = PostgreSqlSslMode.VerifyFull,
}, "Tenant42");
```

## Configuration

`config.postgresql.json`, section `postgresql`:

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "localhost",
      "port": 5432,
      "database": "app",
      "username": "postgres",
      "password": "",
      "sslMode": "Prefer",
      "defaultSchema": "public",
      "minPoolSize": 1,
      "maxPoolSize": 100,
      "syncMode": "production",
      "slowQueryThresholdMs": 1000
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Per-database switch; disabled databases are skipped at startup. |
| `host` / `port` | `localhost` / `5432` | Server endpoint. |
| `database` / `username` / `password` | `""` | Connection credentials. |
| `sslMode` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. |
| `sslCertificatePath` / `sslKeyPath` / `sslRootCertificatePath` | `null` | Client certificate, its key, and the CA bundle for `VerifyCA` / `VerifyFull`. |
| `defaultSchema` | `public` | The schema unqualified entities live in, and the connection's `search_path`. A `[Table]` without a `Schema` is created in — and every statement for it qualified with — this schema; `[Table(Schema = "…")]` still wins. The schema is created (`CREATE SCHEMA IF NOT EXISTS`) on first sync if missing. **Changing it moves where your tables are read and written** — see the migration note in the changelog. |
| `applicationName` | `null` | Reported to the server; visible in `pg_stat_activity`. |
| `minPoolSize` / `maxPoolSize` | `1` / `100` | Connection-pool bounds. |
| `connectionLifetime` | `300` | Seconds a pooled connection may sit idle before being closed. |
| `connectionTimeout` / `commandTimeout` | `30` / `30` | Seconds to wait opening a connection / running a command. |
| `syncMode` | `production` | See [Schema & Migrations](schema-migrations.md). |
| `queryTimeoutMs` | `30000` | Command timeout applied to every command the library creates, rounded up to whole seconds. `0` = no timeout. Matches Npgsql's own 30-second default. |
| `maxBatchInsertSize` | `500` | Rows per batched insert / upsert, capped further by PostgreSQL's 65535-parameter limit. |
| `distinguishDuplicateKeyError` | `false` | When true, a unique violation (SQLSTATE `23505`) in `InsertAsync` / `InsertManyAsync` returns error code `postgresql.duplicate_key` and logs a Warning instead of `postgresql.insert_failed` / `postgresql.bulk_insert_failed` at Error. Also settable per repository via `Repository<T>.DistinguishDuplicateKeyError`. |
| `maxInClauseValues` | `1000` | Warn (at most once per query build, naming the entity and the count) when a generated `IN` list is wider than this. The list is still emitted whole — nothing is chunked and nothing throws. |
| `slowQueryThresholdMs` | `1000` | Queries at or above this duration raise a `SlowQueryEvent`. |
| `captureExplainOnSlowQuery` | `false` | Run `EXPLAIN (FORMAT JSON)` for a slow query and attach the plan to `SlowQueryEvent.ExplainJson`. Best-effort: fetched off the query path, never inside the caller's transaction, and a failure leaves the payload null. |
| `n1DetectorThreshold` | `0` | Publish `N1QueryDetectedEvent` when one normalized query template runs this many times on the connection inside a one-second window. `0` disables the detector. |
| `transientRetryCount` / `transientRetryBaseDelayMs` | `3` / `50` | Retry policy for SQLSTATE `40001`, `40P01` and `55P03`. |
| `defaultStringSize` | `255` | `varchar` length used for a string column with no explicit `[Column(Size = …)]`. |
| `cacheEnabledOverride` | `null` | Per-database override of `postgresql.cache.enabled`. `null` inherits the global switch. |
| `backupDirectory` | `null` | Where schema backups for this connection are written. `null` keeps `DataDirectory/backups`. |
| `preparedStatementCacheSize` | `256` | **Obsolete** — statement caching is Npgsql's, configured on the connection string (`Max Auto Prepare`, `Auto Prepare Min Usages`). Not read. |

### TLS

`Prefer`, the default, encrypts when the server offers it but **does not verify the
certificate**, so it does not protect against an active attacker. Production deployments
should use `VerifyFull`, which checks both the chain and the hostname:

```json
{ "sslMode": "VerifyFull", "sslRootCertificatePath": "/etc/ssl/certs/rds-ca.pem" }
```

### Changing config while running

With CodeLogic 4.9+, both sections apply live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher — and only the databases that changed are touched:

- **Connection settings changed** (endpoint, credentials, TLS, pooling, timeouts): the idle pool is cleared, the connection tested, and the schema-state tables ensured. In-flight queries finish on their old connections.
- **Other settings changed** (default schema, query timeout, cache override, N+1 threshold, …): updated in place, no reconnect.
- **Added / removed** (or enabled / disabled): connected / disconnected.
- **Untouched databases**: nothing happens; their connections stay open.

An unreachable database leaves the change saved and reports `ConfigApplyResult.Applied = false`; the other databases are still applied. A runtime `SetSyncMode` survives unrelated changes. Enabling the first database when the library started with none requires a restart.

## Dialect notes

Points where PostgreSQL behaves differently from the MySQL and SQL Server libraries:

- **Upserts need a conflict target.** `ON CONFLICT` arbitrates on one named unique key, not
  "whichever unique key collides". `UpsertAsync` infers it when the entity has exactly one
  candidate key and throws — naming the candidates — when it has several. Pass
  `conflictTarget` to choose:

  ```csharp
  await repo.UpsertAsync(user, conflictTarget: [nameof(User.Email)]);
  ```

- **Identifiers are case-sensitive.** Everything is emitted double-quoted, so
  `[Column(Name = "userId")]` is a different column from `userid`. Prefer `snake_case`.
- **Schemas are namespaces inside a database.** The connection picks the database and, via
  `defaultSchema`, the schema for entities that do not name one; `[Table(Schema = "…")]`
  overrides it per entity. Every generated statement is schema-qualified, and resolution is
  per connection — two named connections may map the same entity types into different
  schemas.
- **`DateTime` maps to `timestamptz`.** Values with `DateTimeKind.Unspecified` are treated
  as UTC on the way in, since that is what the rest of the stack produces.
- **`OnUpdateCurrentTimestamp` becomes a trigger.** PostgreSQL has no such column clause,
  so schema sync creates a `BEFORE UPDATE` row trigger named `trg_{table}_{column}_touch`.
- **Retention batches by `ctid`.** `DELETE … LIMIT` is not valid PostgreSQL, so the worker
  selects a batch by row pointer with `FOR UPDATE SKIP LOCKED`.

## Health & events

```csharp
var health = await pg.HealthCheckAsync();
```

Returns `Healthy` when every configured connection responds, `Degraded` when some do, and
`Unhealthy` when none do; the payload carries connection counts.

Events published on the framework bus (`CL.PostgreSQL.Events`):

| Event | Raised when |
|---|---|
| `DatabaseConnectedEvent` / `DatabaseDisconnectedEvent` | A connection opens or closes. |
| `TableSyncedEvent` | A table is created or altered; carries the schema, table and statements. |
| `QueryExecutedEvent` | After every query — SQL, elapsed ms, row count, cache-hit flag. |
| `SlowQueryEvent` | A query crosses `slowQueryThresholdMs`. `ExplainJson` carries the plan when `captureExplainOnSlowQuery` is on and the capture succeeded, else null. |
| `CacheHitEvent` / `CacheMissEvent` | A cached read is served or falls through. |
| `N1QueryDetectedEvent` | One query template repeats `n1DetectorThreshold` times on a connection within a second. Fires once per window per template; never when the threshold is `0` (the default). |
| `HealthChangedEvent` | Health state transitions. |
