# CodeLogic.SQLite

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite)](https://www.nuget.org/packages/CodeLogic.SQLite)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> An embedded SQLite data-access layer for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — connection pooling, WAL, attribute-driven table sync, a repository, and a fluent LINQ-shaped query builder.

Map a plain class with attributes and the library keeps the live table in shape, then read and write through a `Repository<T>` or a fluent `QueryBuilder<T>`. It builds on [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), pools connections per database, and enables Write-Ahead Logging by default. Every fallible operation returns a `Result` / `Result<T>` — no exceptions for the expected failure paths.

## Install

```bash
dotnet add package CodeLogic.SQLite
```

## Quick start

```csharp
using CL.SQLite;

await Libraries.LoadAsync<SQLiteLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var db = Libraries.Get<SQLiteLibrary>();

// 1. Reconcile the table from the entity (CREATE / ALTER to match the class)
await db.TableSync.SyncTableAsync<NoteRecord>();

// 2. CRUD via the repository — every call returns a Result
var repo = db.GetRepository<NoteRecord>();
var insert = await repo.InsertAsync(new NoteRecord { Title = "Hello", Body = "World" });
if (insert.IsSuccess)
    Console.WriteLine($"new rowid = {insert.Value}");

// 3. Fluent queries via the query builder
var notes = await db.GetQueryBuilder<NoteRecord>()
    .Where(n => n.Title.Contains("Hello"))
    .OrderByDescending(n => n.CreatedUtc)
    .Take(20)
    .ToListAsync();

if (notes.IsSuccess)
    foreach (var note in notes.Value!)
        Console.WriteLine(note.Title);
```

The entity is plain C# annotated with `[SQLiteTable]` / `[SQLiteColumn]`:

```csharp
using CL.SQLite.Models;

[SQLiteTable("notes")]
public sealed class NoteRecord
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "title", IsNotNull = true, IsIndexed = true)]
    public string Title { get; set; } = "";

    [SQLiteColumn(ColumnName = "body")]
    public string Body { get; set; } = "";

    [SQLiteColumn(ColumnName = "created_utc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

Only properties marked with `[SQLiteColumn]` are mapped. When `DataType` is left off, the column is declared `INTEGER` — the first `SQLiteDataType` value — rather than being inferred from the property type; SQLite's dynamic typing means text still round-trips through an `INTEGER`-affinity column, but set `DataType` explicitly when the declared affinity matters. The connection id defaults to `"Default"` on every entry point, so `GetRepository<NoteRecord>()` equals `GetRepository<NoteRecord>("Default")`.

## Features

- **Named connection pools** — a map of databases keyed by connection id, each with its own per-database pool. `MaxPoolSize` (default 10) caps both the pooled connections *and* the concurrently live ones, so a caller waits when the cap is reached; pooled connections are discarded after 5 minutes idle.
- **WAL by default** — `journal_mode=WAL` is set on every connection for better read/write concurrency.
- **Repository CRUD** — insert / upsert / update / delete, by-id and composite-key lookups, paging, LINQ `Find`, count, and raw SQL — all returning `Result`.
- **Fluent query builder** — `Where` / `OrderBy` / `ThenBy` / `Select` / `GroupBy`, aggregates, `AnyAsync`, paging, and bulk predicate update / delete translated to SQL. Typed `UpdateAsync` setters may read the row (`Tokens = Math.Min(cap, t.Tokens + refill)`), with captured values always sent as parameters, and `UpdateAndGetAsync` runs the same update and returns the rows as they are afterwards (`UPDATE … RETURNING *`).
- **Expression upsert** — `Repository.UpsertAsync(entity, (current, incoming) => new T { … })` inserts or updates the existing row from its current values with `ON CONFLICT … DO UPDATE`, instead of the delete-and-insert of `INSERT OR REPLACE`.
- **Database and table admin** — `db.Admin` creates, deletes and checks database files (`CreateDatabaseAsync`, `DropDatabaseAsync`, `DatabaseExistsAsync`, by path) and tables (`CreateTableAsync<T>`, `DropTableAsync<T>`, `TruncateTableAsync<T>`, `TableExistsAsync<T>`).
- **Attribute-driven schema sync** — `TableSync` creates tables, adds missing columns, and builds indexes to match the entity class; batch-sync by type set or namespace.
- **Migration ledger** — `MigrationTracker` records and inspects applied migration ids in a JSON history file.
- **Type conversion** — `bool`, `DateTime`, `DateTimeOffset`, `Guid`, and `enum` are converted automatically on read and write. A `DateTimeOffset` is stored as `yyyy-MM-dd HH:mm:ss.fffzzz` and read back with its offset intact.
- **Events** — `TableSyncedEvent` on every successful sync, and `SlowQueryEvent` for any query at or above `slowQueryThresholdMs`.

## Configuration

Auto-generated on first run as `config.sqlite.json` (section `sqlite`). The config is a `Databases` map — each key is a connection id you pass to the entry points; `Default` is created automatically.

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "databasePath": "database.db",
      "connectionTimeoutSeconds": 30,
      "commandTimeoutSeconds": 120,
      "skipTableSync": false,
      "cacheMode": "Default",
      "useWAL": true,
      "enableForeignKeys": true,
      "maxPoolSize": 10,
      "slowQueryThresholdMs": 500
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Disable a database without removing it. |
| `databasePath` | `database.db` | Absolute, or relative to the library data directory. |
| `connectionTimeoutSeconds` | `30` | Applied as the connection string's `Default Timeout`, which is also what commands inherit. 30 is the provider's own default. |
| `commandTimeoutSeconds` | `120` | **Not wired, and deliberately so** — SQLite has one timeout knob, not two: a command's `CommandTimeout` is inherited from the connection's `DefaultTimeout`, which `connectionTimeoutSeconds` already owns. Use that setting. |
| `skipTableSync` | `false` | When `true`, `SyncTableAsync` / `SyncTablesAsync` touch nothing for that database and report the sync as skipped. |
| `cacheMode` | `Default` | `Default` / `Private` / `Shared`. `Shared` and `Private` are requested explicitly on the connection string; `Default` leaves the provider's own choice alone. |
| `useWAL` | `true` | Write-Ahead Logging — better concurrency, recommended. |
| `enableForeignKeys` | `true` | Enforce foreign-key constraints. Both directions are sent as a `PRAGMA`, so `false` really does turn enforcement off. |
| `maxPoolSize` | `10` | Maximum pooled **and** concurrently live connections per database. |
| `slowQueryThresholdMs` | `500` | Queries at or above this duration are logged as a warning and published as `SlowQueryEvent`. |

A database with `enabled: false` is skipped at startup; if no database is enabled the library initializes disabled and the health check reports healthy-but-disabled.

### Changing config while running

With CodeLogic 4.9+, `config.sqlite.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher, and only the databases that changed are touched:

- **Connection-shaping settings changed** (`databasePath`, `cacheMode`, `useWAL`, `enableForeignKeys`, `connectionTimeoutSeconds`, `maxPoolSize`): the database gets a fresh pool and is opened once to check it. Idle connections from the old pool are closed; connections in use finish their work and are closed when released, never reused.
- **Other settings changed** (`slowQueryThresholdMs`, `skipTableSync`, ...): updated in place; pooled connections are kept.
- **Added / removed** (or enabled / disabled): registered / released.
- **Untouched databases**: nothing happens; their pooled connections stay open.

A database that cannot be opened leaves the change saved and reports `ConfigApplyResult.Applied = false`; the other databases are still applied. Enabling the first database when the library started with none requires a restart.

## Documentation

Full guide: **[CL.SQLite documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/sqlite/index.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- Microsoft.Data.Sqlite 9.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
