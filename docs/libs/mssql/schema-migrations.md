# CL.MSSQL — Schema & Migrations

CL.MSSQL combines attribute-driven schema reconciliation with ordered imperative migrations. Both workflows use a dedicated SQL Server session holding `sys.sp_getapplock`.

## Mapping

Attributes live in `CL.MSSQL.Models`.

```csharp
[Table(Name = "users", Schema = "app", Comment = "Application users")]
public sealed class User
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 200, Unique = true, NotNull = true,
            Collation = "Latin1_General_100_CI_AS_SC_UTF8")]
    [Index(Name = "IX_users_email", Include = [nameof(CreatedUtc)])]
    public string Email { get; set; } = "";

    [Column(DataType = DataType.Json)]
    public string? ProfileJson { get; set; }

    [Column(DataType = DataType.DateTime2, DefaultValue = "SYSUTCDATETIME()")]
    public DateTime CreatedUtc { get; set; }

    [Column(DataType = DataType.RowVersion)]
    public byte[] Version { get; set; } = [];
}
```

`TableAttribute` exposes `Name`, `Schema` (default `dbo`), and `Comment`. `ColumnAttribute` exposes `Name`, `PreviousName`, native `DataType`, size/precision/scale, primary key, identity (`AutoIncrement`), nullability, uniqueness, indexing, default, collation, comment, and `StorageType`.

Native types cover SQL Server integer, decimal, money, real/float, bit, ANSI/Unicode string and MAX, binary and MAX, date/time variants, `uniqueidentifier`, XML, JSON, geometry/geography, and rowversion. JSON maps to `nvarchar(max)` with an `ISJSON` check. Rowversion columns are excluded from insert and update lists.

Default inference uses `nvarchar(255)` for strings, `datetime2` for `DateTime`, `uniqueidentifier` for `Guid`, `bit` for booleans, `varbinary(max)` for bytes, and `int` for enums.

Indexes support unique and composite definitions. `[Index(Include = [...])]` emits a true SQL Server `INCLUDE` list. Indexes are matched by **name**: when an existing index keeps its name but its definition differs from the model (key columns or their order, `UNIQUE`, `INCLUDE`), sync logs a drift warning at every level. `Production` (Safe) never rebuilds it and flags the table `DriftPending`; `Developer` and `Migration` drop and recreate it. An index on a column renamed with `PreviousName` follows the rename and is not drift. Foreign keys support cascade, set-null, set-default, and no-action behavior. Comments use `MS_Description` extended properties.

## Synchronization modes

| Mode | Behavior |
|---|---|
| `Production` | Applies additive/non-data-losing changes and records destructive drift as pending. |
| `Developer` | Performs full reconciliation, including removal of obsolete managed objects. |
| `Migration` | Takes a catalog DDL snapshot and performs a one-shot full reconciliation. |

```csharp
await mssql.SyncTableAsync<User>();
await mssql.SyncSchemaAsync(typeof(User), typeof(Order));
mssql.SetSyncMode(SyncMode.Production);
```

Schema inspection uses `sys.schemas`, `sys.tables`, `sys.columns`, `sys.types`, indexes, defaults, checks, foreign keys, and extended properties. Model CRC and reconciliation status are stored in `[dbo].[__schema_state]` using UTC timestamps. `PreviousName` invokes `sys.sp_rename` for in-place column renames.

## Soft delete

`[SoftDelete(timestampColumn)]` marks a nullable `DateTime` column as the delete marker.
`Repository.DeleteAsync` then sets it to `DateTime.UtcNow` instead of issuing a physical
`DELETE`, and single-table reads (`mssql.Query<T>()` terminals and the repository getters —
including `Repository.CountAsync`) automatically exclude rows where it is set.

```csharp
[Table(Name = "accounts", Schema = "dbo")]
[SoftDelete(nameof(DeletedUtc))]
public class Account
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(DataType = DataType.DateTime2)] public DateTime? DeletedUtc { get; set; }
}

await repo.DeleteAsync(id);             // stamps DeletedUtc
await repo.HardDeleteAsync(id);         // physically removes the row

var all = await mssql.Query<Account>().IncludeDeleted().ToListAsync();   // override the filter
```

> Auto-filtering applies to single-table reads only. It does **not** apply to joins, subquery
> filters, the query builder's bulk `UpdateAsync` / `DeleteAsync`, or the by-key writes
> `Repository.UpdateAsync` / `AdjustAsync` / `IncrementAsync` / `DecrementAsync` — those stay
> raw so you can target or restore deleted rows.

## Retention

`[RetainDays(days, timestampColumn)]` opts an entity into a background `RetentionWorker` that
deletes rows older than `days` in bounded `DELETE TOP (@batch)` passes (`BatchSize` default
5000), looping until one pass deletes fewer rows than `BatchSize`.

```csharp
[Table(Name = "audit_log", Schema = "dbo")]
[RetainDays(90, nameof(CreatedUtc), BatchSize = 10000)]
public class AuditLog
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(DataType = DataType.DateTime2)] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

The worker is created during the library's start phase and its entity list is **live**: every
type you register through `SyncTableAsync<T>` / `SyncSchemaAsync` is handed to it, and the
background loop starts the first time one of them carries `[RetainDays]` — including
registrations made long after `CodeLogic.StartAsync()` has returned, which is the normal
application flow. Once running it waits 5 minutes, purges, then repeats every 24 hours, and a
new entity registered mid-flight is picked up on the next pass.

For an operator-triggered or test purge, `RunRetentionOnceAsync` runs one pass immediately over
every registered `[RetainDays]` entity and returns the number of rows deleted:

```csharp
int removed = await mssql.RunRetentionOnceAsync();
```

You can also drive a worker of your own over a specific set of entities:

```csharp
var worker = new RetentionWorker(mssql.ConnectionManager, logger, [typeof(AuditLog)]);
int removed = await worker.RunOnceAsync();
```

## Imperative migrations

Implement `IMigration` or derive from `Migration`, register instances, then inspect or execute the ordered plan.

```csharp
// The Migration base takes (appVersion, order, description); Version and Description are
// supplied by that constructor and are not virtual.
public sealed class SeedRoles() : Migration("1.0.0", 1, "Seed roles")
{
    public override Task UpAsync(IMigrationContext db, CancellationToken ct) =>
        db.ExecuteAsync("INSERT INTO [app].[roles] ([name]) VALUES (N'admin')", ct: ct);

    public override Task DownAsync(IMigrationContext db, CancellationToken ct) =>
        db.ExecuteAsync("DELETE FROM [app].[roles] WHERE [name]=N'admin'", ct: ct);
}

mssql.RegisterMigration(new SeedRoles());              // or RegisterMigrationsFrom(assembly)
var pending = await mssql.GetPendingMigrationsAsync();  // IReadOnlyList<MigrationPlanItem>
await mssql.MigrateAsync();                             // caller-driven; never auto-run on start
await mssql.RollbackAsync(new MigrationVersion("1.0.0", 0));
```

`DownAsync` is optional: the `Migration` base throws `NotSupportedException` unless you
override it, so a rollback that reaches an irreversible migration fails rather than skipping it.
SQL Server commits DDL implicitly, so a migration that mixes `ALTER` with data changes is not
atomic — keep `UpAsync` idempotent and split heavy DDL from heavy backfill.

Migration IDs, descriptions, checksums, and application times are stored in `[dbo].[__migrations]` with `SYSUTCDATETIME()`.

## Backups and restore

Schema snapshots are generated from SQL Server catalogs for library-managed columns, identity/default/check constraints, keys, indexes/includes, foreign keys, collations, and comments. Restore validates the presence of a matching `CREATE TABLE`, drops the target, and recreates objects in dependency order. It is schema-only and destructive to table data.

Triggers, temporal history, partitioning, replication, and other externally managed advanced objects are outside snapshot fidelity.

## Database and table administration

`mssql.Admin` (also available as methods directly on the library) creates, drops and checks
databases and entity tables. It is meant for first-run setup, demos, per-test databases and
resets; day-to-day schema changes stay with sync and migrations.

```csharp
await mssql.CreateDatabaseAsync("shop_test");                 // server default collation
await mssql.CreateDatabaseAsync("shop_ci", collation: "Latin1_General_100_CI_AS_SC_UTF8");
bool exists = (await mssql.DatabaseExistsAsync("shop_test")).Value;
await mssql.DropDatabaseAsync("shop_test");

await mssql.CreateTableAsync<Order>();      // create only; never alters an existing table
bool has = (await mssql.TableExistsAsync<Order>()).Value;
bool raw = (await mssql.TableExistsAsync("app.orders")).Value;  // schema.table; a bare name means dbo
await mssql.TruncateTableAsync<Order>();    // TRUNCATE TABLE — also reseeds the IDENTITY column
await mssql.DropTableAsync<Order>();        // also forgets its __schema_state row
```

- All methods return `Result<bool>`. `Create*` returns `false` when the object already existed
  (with `ifNotExists: true`, the default) and `Drop*` returns `false` when there was nothing to
  drop (with `ifExists: true`). With the flag off, those cases are failures.
- Database operations run on an unpooled server-level connection (same server and login, with
  `master` as the current database), so the login needs `CREATE DATABASE` / `DROP` permission.
  SQL Server has no per-database character set, so `CreateDatabaseAsync` takes only a collation
  (use a `_UTF8` collation or `nvarchar` columns for Unicode text). The collation name must be a
  plain identifier (`[A-Za-z0-9_]`) and the database name at most 128 characters; anything else
  throws `ArgumentException` before reaching the server.
- `DropDatabaseAsync` may drop any database, including the one the connection is configured
  for. Idle pooled connections to it are cleared first, and other sessions still using it are
  disconnected (`ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK IMMEDIATE`), which rolls back
  their open transactions.
- Table operations use the entity's `[Table(Schema = …)]` schema (default `dbo`), which must
  already exist. `CreateTableAsync<T>` records the table in `__schema_state` under
  `schema.table`, so the next sync skips it, and registers the entity like `SyncTableAsync<T>`
  does. SQL Server refuses to truncate or drop a table that another table's foreign key
  references.

### Creating the database on startup

Set `createDatabaseIfMissing` on a database in `config.mssql.json` and the library creates it
during initialization, before it first connects. The name is the `Initial Catalog` of the
built connection string, so a raw `connectionString` works too, and the database gets the
server's default collation. It is off by default; a failure to create it is logged as a
warning.

```json
"Default": { "host": "localhost", "database": "shop", "createDatabaseIfMissing": true }
```
