# CL.MySQL2 — Schema & Migrations

> Your entity classes are the source of truth — declarative sync keeps the live schema in shape, imperative migrations handle the data transforms it can't express.

See the [overview](index.md) for loading, repositories, configuration, and events.

CL.MySQL2 reconciles tables in two layers. **Declarative sync** diffs each attribute-mapped class against the live table and emits the DDL to converge them. **Imperative migrations** (`IMigration`) run versioned, ordered transforms for seeds, data backfills, and semantic changes the diff can't see. Both run under a cross-node lock so only one application instance applies changes at a time.

## Entity attributes

All attributes live in `CL.MySQL2.Models`.

### `[Table]`

```csharp
[Table(Name = "users", Engine = TableEngine.InnoDB, Charset = Charset.Utf8mb4,
       Collation = "utf8mb4_unicode_ci", Comment = "Application users")]
public class User { /* … */ }
```

`Name` defaults to the class name. `Engine` defaults to `InnoDB`; `Charset` to `Utf8mb4`. `TableEngine`: `InnoDB`, `MyISAM`, `Memory`, `Archive`, `CSV`, `NDB`. `Charset`: `Utf8mb4`, `Utf8`, `Latin1`, `Ascii`, `Binary`.

### `[Column]`

```csharp
[Column(Name = "email_address", Size = 120, NotNull = true, Unique = true, Index = true)]
public string Email { get; set; } = "";

[Column(Primary = true, AutoIncrement = true)]
public long Id { get; set; }

[Column(DataType = DataType.Decimal, Precision = 12, Scale = 2)]
public decimal Total { get; set; }

[Column(DefaultValue = "CURRENT_TIMESTAMP", OnUpdateCurrentTimestamp = true)]
public DateTime UpdatedUtc { get; set; }
```

| Property | Default | Purpose |
|----------|---------|---------|
| `Name` | property name | Column name. |
| `PreviousName` | — | Rename in place via `CHANGE COLUMN`, preserving data. |
| `DataType` | inferred | Explicit MySQL type (see `DataType` below). |
| `Size` | `0` | Length for `VarChar`/`Char`/binary. `0` on an inferred string column falls back to the configured `DefaultStringSize` (255). |
| `Precision` / `Scale` | `10` / `2` | For `Decimal`. |
| `Primary` | `false` | Part of the primary key. |
| `AutoIncrement` | `false` | `AUTO_INCREMENT`. |
| `NotNull` | `false` | `NOT NULL`. |
| `Unique` | `false` | Unique constraint. |
| `Index` | `false` | Single-column index. |
| `DefaultValue` | — | Column default as raw SQL (`0`, `'active'`, `CURRENT_TIMESTAMP`). `""` means `DEFAULT ''` on CHAR/VARCHAR and is ignored on other types. |
| `Charset` | — | Per-column charset. |
| `Comment` | — | Column comment. |
| `Unsigned` | `false` | Unsigned integer. |
| `OnUpdateCurrentTimestamp` | `false` | `ON UPDATE CURRENT_TIMESTAMP`. |
| `StorageType` | `Default` | Physical storage override (see below). |

**`DataType`**: `TinyInt`, `SmallInt`, `MediumInt`, `Int`, `BigInt`, `Decimal`, `Float`, `Double`, `Bit`, `Char`, `VarChar`, `TinyText`, `Text`, `MediumText`, `LongText`, `Enum`, `Set`, `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`, `LongBlob`, `Date`, `Time`, `DateTime`, `Timestamp`, `Year`, `Json`, `Geometry`.

**`StorageType`** (`Default`, `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`, `LongBlob`) overrides physical storage with automatic binary conversion. A `Guid` with `StorageType.Binary` auto-sizes to `BINARY(16)`.

```csharp
[Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
public Guid Id { get; set; } = SequentialGuid.NewId();   // time-ordered UUIDv7
```

### Keys, foreign keys, and indexes

```csharp
[ForeignKey("customers", "id", OnDelete = ForeignKeyAction.Cascade)]
[Column] public long CustomerId { get; set; }

[Index(Name = "ix_status_created", Include = new[] { "total" })]
[Column] public string Status { get; set; } = "";

[Ignore]                       // not persisted
public string Transient { get; set; } = "";
```

```csharp
[CompositeIndex("ix_day_status", nameof(Day), nameof(Status), Unique = true)]
[Table]
public class DailyOrder { /* … */ }
```

- `[ForeignKey(referenceTable, referenceColumn)]` with `OnDelete` / `OnUpdate` (`ForeignKeyAction`: `Restrict`, `Cascade`, `SetNull`, `NoAction`, `SetDefault`) and an optional `ConstraintName`.
- `[Index]` (column-level, `AllowMultiple`) — `Name`, `Unique`, `Include` for covering columns.
- `[CompositeIndex(name, columns…)]` (class-level, `AllowMultiple`) — `Unique` optional. Indexes are matched by **name**: when an existing index keeps its name but its definition differs from the model (key columns or their order, `UNIQUE`; this covers `[CompositeIndex]`, `[Index]` and `Column.Index` / `Column.Unique`), sync logs a drift warning at every level. `Production` (Safe) never rebuilds it and flags the table `DriftPending`; `Developer` and `Migration` drop and recreate it. An index on a column renamed with `PreviousName` follows the rename and is not drift.
- `[Ignore]` — exclude a property from mapping.

### Column rename — preserve data

Set `PreviousName` so sync emits `CHANGE COLUMN old new …` instead of drop-and-add (which loses the data). Works at `Safe` and above; remove it once every environment has synced.

```csharp
[Column(Name = "email_address", PreviousName = "email")]
public string EmailAddress { get; set; } = "";
```

## Sync modes & levels

Two enums govern how aggressively sync changes the schema. `SyncMode` is the operator-facing knob (set per database in `config.mysql.json`); `SchemaSyncLevel` is the lower-level cap it maps onto.

### `SyncMode`

| Mode | Value | Behaviour |
|------|-------|-----------|
| `Developer` | `0` | Aggressive rolling updates — drops removed columns/indexes/FKs on every boot. |
| `Production` | `1` *(default)* | Additive only — adds and modifies, **never drops**. A change that needs a drop is deferred and the table is flagged `DriftPending`. |
| `Migration` | `2` | Deliberate one-shot destructive reconcile (takes a schema backup first). Idempotent — once everything matches with no drift pending, it does nothing and warns to switch back to `Production`. |

```json
{ "Databases": { "Default": { "SyncMode": "Production" } } }
```

### `SchemaSyncLevel`

| Level | Value | Behaviour |
|-------|-------|-----------|
| `None` | `0` | No schema changes. |
| `Safe` | `1` | Add / modify, never drop. |
| `Additive` | `2` | `Safe` + drop indexes / FKs. |
| `Full` | `3` | `Additive` + drop columns (dev only). |

`SyncMode` takes precedence and maps onto a level via `EffectiveSyncLevel`; the legacy `SchemaSyncLevel` / `AllowDestructiveSync` flags still work for back-compat.

## Running sync

```csharp
// One table, with a schema backup first (default true)
Result<SyncResult> r = await mysql.SyncTableAsync<User>(createBackup: true);

// A whole set as one pass under a single lock — recommended startup entry point
Result<Dictionary<string, SyncResult>> all =
    await mysql.SyncSchemaAsync(typeof(User), typeof(Order), typeof(Customer));

// Override the configured mode at runtime (e.g. flip Migration back to Production)
mysql.SetSyncMode(SyncMode.Production);
```

### The CRC sentinel — skipping unchanged tables

Each model's desired schema is hashed (CRC) into a per-table row in a `__schema_state` sentinel. Sync skips a table **entirely** — no `information_schema` diffing, no DDL — when the stored CRC matches the model and the table still exists. This makes startup sync near-free when nothing changed.

`SyncResult` reports what happened:

| Field | Meaning |
|-------|---------|
| `Success` | Whether the reconcile succeeded. |
| `TableName` | The table reconciled. |
| `Operations` | `List<string>` of DDL statements applied. |
| `Errors` | Any errors encountered. |
| `Duration` | How long it took. |
| `Skipped` | `true` when the CRC fast-path skipped the table. |
| `SchemaCrc` | The computed CRC. |
| `DriftPending` | `true` when a drop was deferred under `Production`. |

```csharp
var sync = (await mysql.SyncTableAsync<User>()).Value!;
if (sync.Skipped)        { /* unchanged — no work */ }
if (sync.DriftPending)   { /* a drop is deferred; reconcile under Migration when ready */ }
```

`mysql.SchemaState` exposes the underlying `SchemaStateStore` for inspection.

## Soft delete

`[SoftDelete(timestampColumn)]` marks a nullable `DateTime` column as the delete marker. `Repository.DeleteAsync` then sets it to a client-side `DateTime.UtcNow` bound as a parameter (the server's `NOW()` is not used) instead of issuing a physical `DELETE`, and single-table reads (`mysql.Query<T>()` terminals and the repository getters `GetByIdAsync`, `GetByColumnAsync`, `GetAllAsync`, `GetPagedAsync` and `FindAsync`) automatically exclude rows where it is set.

```csharp
[Table]
[SoftDelete(nameof(DeletedUtc))]
public class Account
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column] public DateTime? DeletedUtc { get; set; }
}

await repo.DeleteAsync(id);             // sets DeletedUtc = UtcNow
await repo.HardDeleteAsync(id);         // physically removes the row

var all = await mysql.Query<Account>().IncludeDeleted().ToListAsync();   // override the filter
```

> Auto-filtering applies to single-table reads only. It does **not** apply to joins, subquery filters, or the query builder's bulk `UpdateAsync` / `DeleteAsync` — those stay raw so you can target or restore deleted rows.

> `Repository.CountAsync()` applies the filter like every other repository read, so it agrees
> with `GetAllAsync()` and `GetPagedAsync()`. Use `mysql.Query<T>().IncludeDeleted().CountAsync()`
> to count soft-deleted rows as well.

## Retention

`[RetainDays(days, timestampColumn)]` opts an entity into a background `RetentionWorker` that deletes rows older than `days` in batches (`BatchSize` default 5000) until drained.

```csharp
[Table]
[RetainDays(90, nameof(CreatedUtc), BatchSize = 10000)]
public class AuditLog
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

Each pass issues `DELETE FROM table WHERE col < @cutoff LIMIT BatchSize` in a loop until a
statement deletes fewer rows than `BatchSize`; the cutoff is computed client-side as
`DateTime.UtcNow.AddDays(-days)` and bound as a parameter.

The worker is created during library start, waits 5 minutes, then runs every 24 hours. Its
entry list is **live**: registration happens inside `SyncTableAsync<T>` / `SyncSchemaAsync`, and
an entity registered after `CodeLogic.StartAsync()` — the usual order — is picked up as soon as
it is registered, starting the loop if it was not already running. There is no need to sync
retained entities before start.

`mysql.RunRetentionOnceAsync()` (or `RetentionWorker.RunOnceAsync()` on a worker you own)
performs a single purge pass and returns the number of rows deleted — useful for a maintenance
command, or for a test that should not wait out the timer.

## Imperative migrations

When a change can't be expressed declaratively — seed data, data backfills, splitting a column — write an `IMigration`. Migrations run in `MigrationVersion` order over a `__migrations` tracking table, each in its own transaction, under the shared lock, gated by the app version.

### Writing a migration

Derive from the abstract `Migration(appVersion, order, description)` base and override `UpAsync` (and `DownAsync` if you want rollback support):

```csharp
public sealed class SeedRoles() : Migration("1.4.0", 1, "Seed default roles")
{
    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);

    public override async Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("DELETE FROM roles WHERE name IN ('admin','user')", ct: ct);
}
```

`MigrationVersion` is a `readonly record struct (string AppVersion, int Order)` whose `ToString()` renders as `"1.4.0/001"`. `IMigration` exposes `Version`, `Description`, `UpAsync`, and `DownAsync`.

`IMigrationContext` gives you the live transaction and helpers:

| Member | Purpose |
|--------|---------|
| `Connection` | The `MySqlConnection`. |
| `Transaction` | The `MySqlTransaction`. |
| `ExecuteAsync(sql, parameters, ct)` | Non-query, returns affected rows. |
| `QueryAsync<T>(sql, parameters, ct)` | Materializes rows into `List<T>`. |
| `ScalarAsync<T>(sql, parameters, ct)` | Single value. |
| `SyncTableAsync<T>(ct)` | Bridge into declarative sync mid-migration. |

> MySQL implicitly commits on DDL, so a migration mixing `ALTER` with data changes is not atomic — keep `UpAsync` steps idempotent.

### Registering and running

```csharp
mysql.RegisterMigration(new SeedRoles())            // chainable, returns MySQL2Library
     .RegisterMigration(new BackfillSlugs());

mysql.RegisterMigrationsFrom(typeof(Program).Assembly);   // discover all IMigration in an assembly

// Preview
IReadOnlyList<MigrationPlanItem> pending = await mysql.GetPendingMigrationsAsync();

// Apply pending migrations in order
Result<MigrationRunResult> run = await mysql.MigrateAsync();
```

The runner applies each pending migration in its own transaction and warns when an applied migration's checksum has drifted from the registered source.

### Rollback

`RollbackAsync` runs `DownAsync` newest-first for every applied migration above the target version. It pre-flights the range and aborts cleanly — **before any change** — if a migration in range has no `DownAsync` override.

```csharp
Result<MigrationRunResult> rolled =
    await mysql.RollbackAsync(new MigrationVersion("1.3.0", 0));
```

## Backups & restore

`SyncTableAsync` / `SyncSchemaAsync` take a schema backup before altering an existing table (controlled by `createBackup`, and always taken in `Migration` mode). Snapshots are DDL-only, captured with `SHOW CREATE TABLE`, and written as `{table}_{yyyyMMdd_HHmmss}.sql` to `DataDirectory/backups`, or to the connection's `BackupDirectory` when that field is set (a relative path resolves under the data directory).

`RestoreSchemaAsync` replays a backup snapshot and clears the table's `__schema_state` row so the next sync reconciles from scratch:

```csharp
Result<bool> restored = await mysql.RestoreSchemaAsync("users");   // latest backup for the table
// backupFile is a file path, not a bare name — it is passed to File.Exists as given
Result<bool> fromFile = await mysql.RestoreSchemaAsync(
    "users", Path.Combine(dataDirectory, "backups", "users_20260620_141530.sql"));
```

> Restore replays DDL only — rows in the dropped/recreated table are lost. Use it to recover schema shape, not data.

## Database and table administration

`mysql.Admin` (also available as methods directly on the library) creates, drops and checks
databases and entity tables. It is meant for first-run setup, demos, per-test databases and
resets; day-to-day schema changes stay with sync and migrations.

```csharp
await mysql.CreateDatabaseAsync("shop_test");                   // CHARACTER SET / COLLATE from config
await mysql.CreateDatabaseAsync("shop_ci", charset: "utf8mb4", collation: "utf8mb4_0900_ai_ci");
bool exists = (await mysql.DatabaseExistsAsync("shop_test")).Value;
await mysql.DropDatabaseAsync("shop_test");

await mysql.CreateTableAsync<Order>();      // create only; never alters an existing table
bool has = (await mysql.TableExistsAsync<Order>()).Value;
await mysql.TruncateTableAsync<Order>();    // TRUNCATE TABLE — also resets AUTO_INCREMENT
await mysql.DropTableAsync<Order>();        // also forgets its __schema_state row
```

- All methods return `Result<bool>`. `Create*` returns `false` when the object already existed
  (with `ifNotExists: true`, the default) and `Drop*` returns `false` when there was nothing to
  drop (with `ifExists: true`). With the flag off, those cases are failures.
- Database operations run on a server-level connection (same host and credentials, no default
  database), so the user needs `CREATE` / `DROP` privileges. `DropDatabaseAsync` may drop any
  database, including the one the connection is configured for; idle pooled connections to it
  are cleared first.
- Character set and collation names must be plain identifiers (`[A-Za-z0-9_]`); anything else
  throws `ArgumentException` before reaching the server.
- `CreateTableAsync<T>` records the table in `__schema_state`, so the next sync skips it, and
  registers the entity like `SyncTableAsync<T>` does. MySQL refuses to truncate or drop a table
  that another table's foreign key references.

### Creating the database on startup

Set `CreateDatabaseIfMissing` on a database in `config.mysql.json` and the library creates it
(with that entry's `CharacterSet` / `Collation`) during initialization, before it first
connects. It is off by default.

```json
"Default": { "Host": "localhost", "Database": "shop", "CreateDatabaseIfMissing": true }
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
