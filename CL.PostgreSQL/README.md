# CodeLogic.PostgreSQL

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL)](https://www.nuget.org/packages/CodeLogic.PostgreSQL)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> A typed PostgreSQL data-access layer for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — multi-database connections, an attribute-driven repository, a fluent LINQ query builder, joins and projections, cursor paging, upserts, caching, migrations, and declarative schema sync.

Map a plain class with attributes and the library reconciles the live table to match, then exposes a typed `Repository<T>` and a chainable `QueryBuilder<T>` over it. It builds on [Npgsql](https://www.nuget.org/packages/Npgsql) and connects to one or many PostgreSQL instances from a single config. Every fallible operation returns a framework `Result<T>` — no exceptions on the expected failure paths.

## Install

```bash
dotnet add package CodeLogic.PostgreSQL
```

## Quick start

```csharp
using CL.PostgreSQL;
using CL.PostgreSQL.Models;

[Table(Name = "users", Schema = "public")]
public sealed class User
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "email", Size = 160, Unique = true, NotNull = true)]
    public string Email { get; set; } = "";

    // No DataType: inferred from the CLR type — Guid maps to uuid, DateTime to timestamptz.
    [Column(Name = "external_id")]
    public Guid ExternalId { get; set; }

    [Column(Name = "created_utc", DefaultValue = "now()")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

await Libraries.LoadAsync<PostgreSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var pg = Libraries.Get<PostgreSQLLibrary>()!;

// Reconcile the table to match the entity (creates it, or adds missing columns/indexes)
await pg.SyncTableAsync<User>();

// Typed repository CRUD
var repo = pg.GetRepository<User>();
var created = await repo.InsertAsync(new User { Email = "ada@example.com" });

// Idempotent insert (re-sent batches keyed by a unique id): duplicates are a normal outcome
var outcome = await repo.InsertIgnoreAsync(new User { Email = "ada@example.com" });

// Fluent query builder
var recent = await pg.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(20)
    .ToListAsync();
```

## Features

- **Multi-database** — manage connections to several PostgreSQL instances from one config; pick the target per call with a `connectionId` (default `"Default"`), or add one at runtime with `pg.ConnectionManager.RegisterConfiguration(config, id)`.
- **Repository** — full CRUD plus batched bulk insert, `ON CONFLICT` upserts, paging, find, raw SQL, soft delete, and atomic increment/decrement.
- **Fluent query builder** — `Where`, `OrderBy`, `Limit`/`Offset` (aliases `Take`/`Skip`), `Join`, typed `Select` projections, `GroupBy`, aggregates, `WhereIn`/`WhereExists`, bulk update/delete.
- **Read-modify-write in SQL** — bulk `UpdateAsync` setters may read the row (`Tokens = Math.Min(cap, t.Tokens + refill)`), with captured values always sent as parameters; `UpdateAndGetAsync` runs the same update and returns the rows as they are afterwards (`UPDATE … RETURNING *`); `AnyAsync` checks for a match with `SELECT 1 … LIMIT 1`; `Repository.UpsertAsync(entity, (current, incoming) => new T { … })` inserts or updates from the current row in one `ON CONFLICT … DO UPDATE`.
- **Row locking** — inside a transaction, `ForUpdate()` / `ForShare()` add `FOR UPDATE` / `FOR SHARE` to a read (`LockWait.NoWait` / `LockWait.SkipLocked` for job queues).
- **Database and table admin** — `pg.Admin` creates, drops and checks databases and tables (`CreateDatabaseAsync`, `DropDatabaseAsync`, `DatabaseExistsAsync`, `CreateTableAsync<T>`, `DropTableAsync<T>`, `TruncateTableAsync<T>`, `TableExistsAsync<T>`); `createDatabaseIfMissing` creates the database at startup.
- **Cursor paging** — `After(cursor).ToCursorPagedListAsync()` for stable keyset pagination that does not drift as rows are inserted. Tokens are validated against the issuing query's entity and ordering, but are not signed — treat a cursor as a position, not as an authorisation.
- **Caching** — per-query `WithCache(ttl)`, named smart-cache pools with background refresh, table-version invalidation, and a pluggable store/coordinator for multi-node setups.
- **Migrations** — `IMigration` classes with up/down, ordered by app version, tracked in a database table (not a local file) and applied under an advisory lock so only one node runs them.
- **Schema sync** — create or alter tables to match entities (single, set, or whole namespace), CRC-gated so unchanged models cost nothing, with `SyncMode` controlling how destructive a reconcile may be.
- **PostgreSQL-native** — `uuid`, `timestamptz`, `jsonb`, arrays, ranges, `inet`; identity columns; `INCLUDE` covering indexes; `pg_advisory_lock`; `ctid`-batched retention.
- **Observability** — query-executed, slow-query, and cache hit/miss events on the framework bus.
- **Transactions** — `BeginTransactionAsync()` returns an `await using` scope that auto-rolls-back if it is never committed.

## Configuration

Auto-generated on first run as `config.postgresql.json` (section `postgresql`). `Databases` is a named map keyed by connection id; `Default` is created automatically.

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
| `database` / `username` / `password` | `""` | Connection credentials. The database is not the schema — see `defaultSchema`. |
| `sslMode` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. **`Prefer` encrypts but does not verify the certificate; use `VerifyFull` in production.** |
| `sslCertificatePath` / `sslKeyPath` / `sslRootCertificatePath` | `null` | Client certificate, its key, and the CA bundle used by `VerifyCA`/`VerifyFull`. |
| `defaultSchema` | `public` | The schema unqualified entities live in, and the connection's `search_path`. A `[Table]` without a `Schema` is created in — and every statement for it qualified with — this schema; `[Table(Schema = "…")]` still wins. Created if missing. **Changing it moves where your tables are read and written.** |
| `maintenanceDatabase` | `postgres` | Database the library connects to (same host and credentials) for `CREATE` / `DROP DATABASE` and existence checks, since PostgreSQL cannot create or drop the database a session is connected to. |
| `createDatabaseIfMissing` | `false` | Create `database` (server default encoding and locale) during initialization when it does not exist. Needs the `CREATEDB` attribute. |
| `applicationName` | `null` | Reported to the server; shows up in `pg_stat_activity`. |
| `minPoolSize` / `maxPoolSize` | `1` / `100` | Connection-pool bounds. |
| `connectionLifetime` | `300` | Seconds a pooled connection may sit idle before being closed. |
| `connectionTimeout` / `commandTimeout` | `30` / `30` | Seconds to wait when opening a connection / running a command. |
| `syncMode` | `production` | `developer` (drops freely), `production` (add and modify only), `migration` (one-shot destructive reconcile, backup first). |
| `allowDestructiveSync` | `false` | Legacy escape hatch; promotes `production` to a full reconcile. |
| `maxBatchInsertSize` | `500` | Rows per batched insert/upsert, capped so a statement stays under PostgreSQL's 65535-parameter limit. |
| `queryTimeoutMs` | `30000` | Command timeout applied to every command the library creates (rounded up to whole seconds). `0` = no timeout. |
| `maxInClauseValues` | `1000` | Warn (once per query build) when a generated `IN` list is wider than this. The list is still sent whole — nothing is chunked and nothing throws. |
| `defaultStringSize` | `255` | `varchar` length for a string column with no explicit `[Column(Size = …)]`. |
| `cacheEnabledOverride` | `null` | Per-database override of the global cache switch. `null` inherits. |
| `backupDirectory` | `null` | Where schema backups are written. `null` = `DataDirectory/backups`. |
| `n1DetectorThreshold` | `0` | Publish `N1QueryDetectedEvent` when one query template repeats this often within a one-second window. `0` disables. |
| `captureExplainOnSlowQuery` | `false` | Attach `EXPLAIN (FORMAT JSON)` to `SlowQueryEvent` for slow queries. Best-effort. |
| `slowQueryThresholdMs` | `1000` | Queries at or above this duration raise a `SlowQueryEvent`. |
| `transientRetryCount` | `3` | Retries for serialization failures, deadlocks and unavailable locks (SQLSTATE `40001`, `40P01`, `55P03`). |

### Changing config while running

With CodeLogic 4.9+, both sections apply live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher — and only the databases that changed are touched:

- **Connection settings changed** (endpoint, credentials, TLS, pooling, timeouts): the idle pool is cleared, the connection tested, and the schema-state tables ensured. In-flight queries finish on their old connections.
- **Other settings changed** (default schema, query timeout, cache override, N+1 threshold, …): updated in place, no reconnect.
- **Added / removed** (or enabled / disabled): connected / disconnected.
- **Untouched databases**: nothing happens; their connections stay open.

An unreachable database leaves the change saved and reports `ConfigApplyResult.Applied = false`; the other databases are still applied. A runtime `SetSyncMode` survives unrelated changes. Enabling the first database when the library started with none requires a restart.

## Notes for PostgreSQL

A few places where PostgreSQL genuinely differs from the MySQL and SQL Server siblings:

- **Upserts need a conflict target.** `ON CONFLICT` arbitrates on one named unique key, not "whichever key collides". The target is inferred when the entity has exactly one candidate; when it has several, pass `conflictTarget` explicitly rather than have one chosen for you.
- **Identifiers are case-sensitive.** Everything is emitted double-quoted, so `[Column(Name = "userId")]` is a different column from `userid`. Prefer `snake_case` names.
- **Schemas are namespaces.** A connection targets one database; `defaultSchema` picks the schema inside it for entities that do not name one, and `[Table(Schema = "…")]` overrides that per entity. Every generated statement is schema-qualified, and two named connections may target different schemas with the same entity types.
- **`OnUpdateCurrentTimestamp`** has no column-clause equivalent, so schema sync creates a `BEFORE UPDATE` trigger for it.
- **`DateTime` maps to `timestamptz`** and values with `Unspecified` kind are treated as UTC.

## Documentation

Full guide: **[CL.PostgreSQL documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/postgresql/index.html)**

- [Overview](https://zyntal-com.github.io/CodeLogic.Libs/libs/postgresql/index.html) — load, multi-database, repository CRUD, config, health, events.
- [Query Builder](https://zyntal-com.github.io/CodeLogic.Libs/libs/postgresql/queries.html) — fluent methods, terminals, aggregates, projections, cursor paging, bulk writes, transactions.
- [Schema & Migrations](https://zyntal-com.github.io/CodeLogic.Libs/libs/postgresql/schema-migrations.html) — entity attributes, sync modes, the CRC sentinel, soft delete, retention, migrations, backups.
- [Performance & Caching](https://zyntal-com.github.io/CodeLogic.Libs/libs/postgresql/performance.html) — result cache, smart pools, multi-node coordination, retry, N+1 detection, batch limits.

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- Npgsql 9.x · PostgreSQL 12+ (covering `INCLUDE` indexes need 11+; identity columns need 10+)

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
