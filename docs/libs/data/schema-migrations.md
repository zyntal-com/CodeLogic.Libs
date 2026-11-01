# Schema & migrations

CL.Data keeps tables in line with entity classes (table sync), runs versioned migrations and seeds, and handles database
and table administration, backups and retention. All of it works on all four providers.

## Table sync

```csharp
var report = (await db.Schema.SyncAsync<Product>()).ValueOrThrow();
if (report.HasDrift) logger.Warning("Products table differs from its model");

await db.Schema.SyncAsync(maps);                                    // several entities at once
var plan = (await db.Schema.PlanAsync<Product>()).ValueOrThrow();   // what would change, without applying it
```

**How sync works:**
- **Comparison:** sync reads the live table (introspection), compares it with the model, and applies what the sync level allows. Everything else is reported as drift through `SchemaDriftDetectedEvent`.
- **Unchanged models:** a CRC of the normalized model is stored in `cl_data_schema`, so an unchanged model costs one lookup.
- **One node at a time:** sync takes an advisory lock, so only one node syncs a table at a time.

**Sync levels** (`syncMode` sets one; `schemaSyncLevel` overrides it):

| `syncMode` | Level | Applies |
|---|---|---|
| `Developer` | `Full` | everything, including dropping columns and narrowing types |
| `Production` (default) | `Safe` | create tables, add columns, widen types, add indexes and foreign keys |
| (override) | `Additive` | `Safe`, plus dropping indexes and foreign keys that are no longer declared |
| `Migration` | `None` | nothing; only migrations change the schema |

**Renames:** `[Column(PreviousName = "old_name")]` renames a column instead of dropping and adding it.

**Provider details:**
- **SQLite** rebuilds a table for changes it can't `ALTER`, then checks foreign keys afterwards.
- **MySQL** applies `[MySqlTable]`/`[MySqlColumn]` engine, charset and collation.
- **SQL Server** adds `ISJSON` checks to JSON columns.

**Backup before altering:** set `backupDirectory`, and sync writes a backup of a table before altering it.

**NOT NULL on existing data:** making a column NOT NULL first replaces its NULLs with the column's default (or an
implicit one: `''`, `0`, the epoch), in the same plan.

**Scripts instead of live changes:** `db.Schema.ScriptAsync(maps, level)` plans the same changes without running
anything and returns them as a `SchemaScript` (`Statements`, `ToSql()`, pending items as comments). Generate a
migration from the models against a copy of production, review and commit it, run it at cut-over, and run the
production app with `syncMode: Migration`. After the migration the script is empty, so the same call works as a
drift check at startup.

```csharp
var maps = new[] { EntityAccessorRegistry.Get<Order>().ValueOrThrow().Map, /* ... */ };
var script = (await db.Schema.ScriptAsync(maps, SchemaSyncLevel.Full)).ValueOrThrow();
File.WriteAllText("01-schema.sql", script.ToSql());
```

## Migrations

```csharp
public sealed class AddSkuIndex() : Migration("2.1.0", 1, "Index products by SKU")
{
    public override bool CanRollback => true;

    public override async ValueTask<Result> UpAsync(IMigrationContext context, CancellationToken ct) =>
        (await context.ExecuteAsync($"CREATE INDEX ix_products_sku ON products (Sku)", ct)).ToResult();

    public override async ValueTask<Result> DownAsync(IMigrationContext context, CancellationToken ct) =>
        (await context.ExecuteAsync($"DROP INDEX ix_products_sku ON products", ct)).ToResult();
}

var runner = db.Migrations(MigrationRunner.FromAssembly(typeof(AddSkuIndex).Assembly));
var pending = (await runner.PendingAsync()).ValueOrThrow();
var applied = (await runner.MigrateAsync()).ValueOrThrow();          // in version order, under a lock
await runner.RollbackAsync(new MigrationVersion("2.0.0", 0));         // down to a version
```

**How migrations run:**
- **Versions:** an application version plus an order number.
- **Tracking:** applied migrations are recorded in `cl_data_migrations` with a checksum. `AppliedAsync()` reports migrations whose code changed after they ran.
- **The context:** `IMigrationContext` runs SQL, applies schema changes, reads live tables, and can sync an entity's table (`SyncTableAsync<T>()`).

## Seeding

```csharp
var seeds = db.Seeds(
[
    DataSeed.Rows("countries", [new Country { Code = "DK", Name = "Denmark" }, new Country { Code = "SE", Name = "Sweden" }]),
    new DefaultAdminSeed(),            // a DataSeed subclass with custom logic
]);
var result = (await seeds.RunAsync()).ValueOrThrow();   // Applied / UpToDate
```

**How seeds run:**
- **Idempotent:** row seeds upsert, so a seed can run on every start.
- **Tracking:** seeds are recorded in `cl_data_seeds` by id and version. Bump the version to run a seed again.
- **Order:** seeds run by `order`.

## Administration

```csharp
await db.Admin.EnsureDatabaseAsync();                   // or "createDatabaseIfMissing": true in config
var exists = await db.Admin.TableExistsAsync<Product>();
var tables = await db.Admin.ListTablesAsync();
await db.Admin.TruncateAsync<Product>();
await db.Admin.DropTableAsync<Product>();
var version = await db.Admin.ServerVersionAsync();      // "8.4.3", "18.0", "16.0.4135.4", "3.46.1"
```

## Backup and restore

```csharp
var manifest = (await db.Backup.BackupAsync("backups/2026-10-31")).ValueOrThrow();   // DDL plus data, all tables or a list
await db.Backup.RestoreAsync("backups/2026-10-31");
await db.Backup.SnapshotAsync("backups/app.db");                                     // SQLite: VACUUM INTO, a consistent file copy
```

A restore checks `manifest.json` before it drops anything. Store types, collations, defaults and table options are written
into `CREATE TABLE`, so they must be plain values: a type name with an optional size and modifiers (`varchar(64)`,
`timestamp(3) with time zone`, `bigint unsigned`, `enum('a','b')`), a collation or option made of letters, digits and `_`, and
a default built only from literals, parentheses, casts and known functions (`CURRENT_TIMESTAMP`, `now()`, `newid()`,
`gen_random_uuid()`, ...). Every rows file must lie inside the backup directory. Anything else fails with `data.validation`.
Tables with user-defined types or computed defaults need a native dump instead. Restore only backups you trust: rows are
inserted as they are.

## Retention

Rows of `[RetainDays(30)]` entities older than their retention are deleted in batches:

```csharp
var purged = (await db.Retention.PurgeAsync<AuditLog>()).ValueOrThrow();
var job = db.Retention.Schedule(scheduler, [map], TimeSpan.FromHours(1));   // on a DataScheduler
```

Each purge publishes `RetentionPurgedEvent`.

For a `[TenantKey]` entity, a purge from a tenant view (`db.ForTenant(id).Retention`) deletes only that tenant's expired rows. Without a tenant, the purge is the system-wide job and applies the retention policy to every tenant's rows.

## Bookkeeping tables

| Table | Holds |
|---|---|
| `cl_data_schema` | model CRC per synced table |
| `cl_data_migrations` | applied migrations, with checksums |
| `cl_data_seeds` | applied seeds, with versions |
| `cl_data_cache_bus` | cross-node cache invalidations (only with the `Database` bus) |
