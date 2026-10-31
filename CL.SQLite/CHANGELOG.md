# CL.SQLite — Changelog

All notable changes to **CodeLogic.SQLite** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). This library's API is unchanged, and
  its results can now be matched exhaustively with `switch`.

### Changed

- `Microsoft.Data.Sqlite` 11.0 (RC3 until .NET 11 GA). The `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 security pin stays.

## 2026-10-26

### Added

- **Typed `QueryBuilder.UpdateAsync(setter)`** — `t => new T { Status = "done", Counter = t.Counter + 1 }`.
  Only the assigned properties are written. Values that read the row become SQL expressions,
  including `Math.Min` / `Math.Max` (SQLite's scalar `min` / `max`); every captured value except
  `NULL` and primitive numbers is bound as a parameter.
- **`QueryBuilder.UpdateAndGetAsync(setter)`** updates matching rows and returns them as they are
  afterwards, in one `UPDATE … RETURNING *` statement (SQLite 3.35+; the bundled engine is 3.50.4).
  `OrderBy`, `Take` and `Skip` pick the rows by `rowid`; the order of the returned rows is not
  guaranteed.
- **`QueryBuilder.AnyAsync()` / `AnyAsync(predicate)`** — `SELECT 1 … LIMIT 1`.
- **`Repository.UpsertAsync(entity, (current, incoming) => new T { … }, conflictTarget)`** — insert,
  or update the existing row from its current values, in one `INSERT … ON CONFLICT (…) DO UPDATE`.
  The conflict target is inferred when the entity has exactly one candidate unique key. The plain
  `UpsertAsync(entity)` (`INSERT OR REPLACE`) is unchanged.
- **Database and table administration** on `SQLiteLibrary.Admin` (`DatabaseAdmin`) and as library
  methods: `CreateDatabaseAsync(path)`, `DropDatabaseAsync(path)` (also deletes the `-wal`, `-shm`
  and `-journal` files and drains pooled connections to the file), `DatabaseExistsAsync(path)`,
  `CreateTableAsync<T>`, `DropTableAsync<T>`, `TruncateTableAsync<T>` (`DELETE` plus
  `sqlite_sequence` reset), `TableExistsAsync<T>` / `TableExistsAsync(name)`.
- `QueryBuilder.ForUpdate` / `ForShare` and the `LockWait` enum, for API parity with the server
  libraries. They always throw `NotSupportedException` — SQLite has no row locks and CL.SQLite has
  no transactions; use `UpdateAndGetAsync` for an atomic read-modify-write.

## 2026-10-24

### Added

- **Live config changes per database** (requires CodeLogic 4.9+). `SQLiteLibrary` implements
  `IConfigReloadable`: a change to `config.sqlite.json` made through `Libraries.UpdateConfigAsync`,
  `Libraries.ReloadConfigAsync` or the config file watcher is applied without a restart and touches
  only the databases that changed. A changed path, cache mode, WAL, foreign-key, connection-timeout
  or pool-size setting gives that database a fresh pool and opens it once; other settings update in
  place and keep the pooled connections; added/removed databases are registered/released;
  untouched databases keep their connections. A database that cannot be opened leaves the change
  saved, still applies the rest, and is reported as not applied.
- `ConnectionManager.ReplaceConfigurationAsync` and `ConnectionManager.UnregisterConfigurationAsync`
  (same names as CL.MySQL2, CL.MSSQL and CL.PostgreSQL).

### Fixed

- **Re-registering a connection ID while one of its connections was in use corrupted the new
  pool.** `RegisterConfiguration` disposed the old pool outright, and the in-use connection was
  later released into the new one: its active count went negative, its slot semaphore was released
  without a matching acquire (`SemaphoreFullException` at a pool size of 1), and a connection
  opened with the old settings joined the new pool. Every connection now returns to the pool that
  issued it; a replaced pool is retired (idle connections closed, Microsoft.Data.Sqlite's own
  handles for that file cleared) and closes in-use connections when they come back.

## 2026-09-13

### Fixed

- **A bulk `UpdateAsync` dictionary key could escape its quoted identifier.** Keys were
  interpolated into the `SET` clause verbatim, so a key containing a double quote could
  close its own identifier and append an assignment to a column the caller never named.
  Keys are now resolved against the entity's mapped columns — by column name or property
  name — and only the resolved name is quoted; an unmapped key is rejected. This matches
  what the three sibling libraries already did through `RequireColumn`.
- **A `Guid` or `DateTime` primary key could not be read back or deleted.** `InsertAsync`
  wrote key values through the value converter while `GetByIdAsync`, `GetByKeysAsync` and
  `DeleteByKeysAsync` bound them raw, and the provider maps a raw `Guid` to a BLOB — so the
  lookup never matched the row that had just been written, returning "not found" and
  deleting nothing. All key paths now use the same converter as the write path.
- Bulk `UpdateAsync` values also go through the value converter, so the bulk path and the
  repository write the same representation for the same column.
- **`ids.Contains(x.Id)` on a `List<T>` or `HashSet<T>` threw** with
  `InvalidOperationException: variable 'x' ... referenced from scope '', but it is not
  defined`. Two shapes reach the membership branch with their operands in opposite
  positions — the static `Enumerable.Contains(collection, item)` and the one-argument
  instance `collection.Contains(item)` — but both read the collection from the first
  argument, so the instance form was handed the item expression (a lambda parameter) to
  evaluate. Arrays were unaffected because they bind to the static form. Since a
  `List<T>` is the idiomatic way to write an IN clause in C#, this broke ordinary usage.

- **Composite primary keys produced invalid DDL.** `GenerateCreateTableSql` emitted one inline
  `PRIMARY KEY` per key column, so SQLite rejected the table with *"table has more than one
  primary key"* and no entity with a multi-column key could ever be synced. Two or more key
  columns are now declared as a single table-level `PRIMARY KEY (k1, k2)` constraint, each key
  column `NOT NULL`; a single key stays inline, which is the only form that accepts
  `AUTOINCREMENT`.
- **Inserting an entity whose only column is the auto-increment key** emitted
  `INSERT INTO "t" () VALUES ()` — an empty column list — and failed with a syntax error. It now
  emits `INSERT INTO "t" DEFAULT VALUES`.
- **`Skip`/`Offset` without `Take`/`Limit` was a syntax error.** SQLite's grammar is
  `LIMIT expr [OFFSET expr]`; a bare `OFFSET` is now emitted as `LIMIT -1 OFFSET n`.
- **Column names that are not legal parameter tokens broke every write.** `Repository`
  insert/upsert/update and `QueryBuilder.UpdateAsync` derived parameter names from the column
  name (`@{col}`, `@set_{col}`, `@upd_{col}`), so a column such as `"select from"` produced a
  malformed statement. All four sites now bind positionally (`@p0`, `@p1`, …).
- **`Select` emitted an unquoted column list, and the wrong names for an anonymous
  projection.** Reserved-word and awkward column names were syntax errors, and
  `Select(o => new { o.CreatedUtc })` emitted the C# property name rather than the mapped
  column. Every projected member is now resolved back to the source entity's
  `[SQLiteColumn]` name and double-quoted, matching `WHERE` and `ORDER BY`.
- **`ToPagedListAsync` on a grouped query reported the first group's row count** as
  `TotalItems`, because it appended `GROUP BY` to its `COUNT(*)`. The grouped query is now
  wrapped — `SELECT COUNT(*) FROM (SELECT 1 FROM … GROUP BY …)` — so the total is the number of
  groups, which is what the page is paging through.
- **`CountAsync` silently dropped `GroupBy`.** On a grouped builder it now returns the number of
  groups, by the same wrap, and so agrees with `ToPagedListAsync`.
- **`DateTimeOffset` was write-only.** There was a write conversion but no matching read branch,
  so materializing a row with a `DateTimeOffset` property failed. The
  `yyyy-MM-dd HH:mm:ss.fffzzz` form the writer produces is now parsed back, offset included, and
  the duplicated converter pair in `Repository` and `QueryBuilder` is one shared helper.
- **`SyncNamespaceAsync` never found anything.** It called `Assembly.GetCallingAssembly()` from
  inside an `async` method, where the stack is the state machine rather than the caller. The
  assembly is captured in a non-async wrapper before the state machine starts.
- **`FirstOrDefaultAsync` permanently capped the builder** by calling `Limit(1)` on shared
  state, so a builder reused after it returned at most one row. The limit is now local to
  the call.
- **`Repository.CountAsync` and `QueryBuilder.CountAsync` were never timed**, so a slow count
  was neither logged nor (now) published. Both are instrumented like every other terminal.
- **A corrupt `migration_history.json` was discarded in silence.** Recovery is unchanged — the
  file is treated as empty and overwritten on the next write — but a warning naming the file is
  logged first, so the loss is traceable.

### Added

- **`SlowQueryEvent` is now published** for any repository or query-builder statement at or
  above `slowQueryThresholdMs`, alongside the existing logger warning. It carries `TableName`,
  `Query`, `ElapsedMs` and `DetectedAt`. The record has existed since the library shipped but
  nothing ever constructed it. Publishing goes through the new `SQLiteObservability` sink, which
  `SQLiteLibrary` binds to the event bus at initialization — the same shape the `CL.MySQL2` and
  `CL.PostgreSQL` siblings use.
- **`SyncNamespaceAsync(Assembly, …)` overload**, so callers can name the assembly to scan
  instead of depending on the call stack. The existing signature is unchanged.
- **`SQLiteObservability`** (public, static): the event sink above, and the holder for the
  library's localized strings.
- `[assembly: InternalsVisibleTo("SQLite.Tests")]`, matching `CL.MSSQL` / `CL.MySQL2` /
  `CL.PostgreSQL`.

### Changed

- **`connectionTimeoutSeconds` is now applied** as the connection string's `Default Timeout`.
  Its default of 30 is Microsoft.Data.Sqlite's own default, so a configuration that never set it
  opens exactly the connection it opened before. `commandTimeoutSeconds` remains unwired and is
  now documented as such: SQLite has one timeout knob, not two — a command's `CommandTimeout` is
  inherited from the connection's `DefaultTimeout`, which `connectionTimeoutSeconds` owns — and
  its declared default of 120 would not have reproduced today's 30.
- **`skipTableSync` is now honoured.** With it set, `SyncTableAsync` / `SyncTablesAsync` /
  `SyncNamespaceAsync` read and write nothing for that database and return a success whose
  message says the sync was skipped. The default is `false`, so nothing changes unless you set it.
- **`enableForeignKeys: false` now actually disables enforcement.** Only `PRAGMA
  foreign_keys=ON` was ever sent, and Microsoft.Data.Sqlite enables foreign keys by default, so
  the false setting did nothing. Both directions are now sent explicitly. The default is `true`
  and its behaviour is unchanged. **If you have been setting `false` and relying on constraints
  still being enforced, they will now be off.**
- **`cacheMode: "Private"` now requests `Cache=Private`** instead of opening the same connection
  as `Default`. `Default` still omits the keyword entirely.
- **`[SQLiteColumn(Size = n)]` is now emitted** as a length modifier on the declared type
  (`"col" TEXT(64)`) in `CREATE TABLE` and `ALTER TABLE ADD COLUMN`. SQLite records the declared
  type but does not enforce the length, and the modifier does not change type affinity; it was
  applied rather than deprecated so the generated file reads correctly in other tools. `Size = 0`
  (the default) emits nothing, so existing entities generate byte-identical DDL.
- **`SumAsync` / `MinAsync` / `MaxAsync` now throw `NotSupportedException` on a grouped
  builder** rather than quietly ignoring the `GroupBy` and aggregating the whole filtered set.
  A per-group aggregate has no single scalar answer; the message points at `ToListAsync`.
- The eight localized strings that were declared but never used (`ConnectionCreated`,
  `ConnectionReused`, `ConnectionReleased`, `TableSyncStarted`, `TableCreated`, `TableSynced`,
  `TableSyncFailed`, `SlowQueryDetected`) now back their log sites, which previously hard-coded
  English. They were used rather than deleted because `SQLiteStrings` is public surface. Log
  wording changes slightly; no log site was added or removed.

### Documentation

- **Configuration** is documented against the wired behaviour above: what
  `connectionTimeoutSeconds` maps to, why `commandTimeoutSeconds` is not applied and what to use
  instead, what `skipTableSync` short-circuits, and that `enableForeignKeys: false` now really
  turns enforcement off.
- **`cacheMode`**: all three values are documented, including that `Default` omits the keyword
  and leaves the provider's own choice in place.
- **`maxPoolSize`** is documented as what it is: a cap on concurrently live connections as well
  as pooled ones. Once it is reached the next caller waits, rather than getting a fresh
  connection on demand as the old text claimed.
- **`DataType` is not inferred.** The docs claimed an omitted `DataType` was inferred from the
  property type. It is not — an omitted `DataType` is the enum default, `INTEGER`, so a `string`
  property with no explicit `DataType` is declared `INTEGER`. Documented, with a note on when
  that matters under SQLite's dynamic typing.
- **`DateTimeOffset` round-trips** and is documented with its storage format, next to the note
  that a `DateTime` carries no kind or offset and always reads back as `Unspecified`.
- **`[SQLiteColumn(Size = …)]`** is documented as a declared length that SQLite records but
  never enforces.
- **`GroupBy` and the terminals**: the query-builder page and the `GroupBy` XML comment now
  state which terminals grouping reaches — rows, `CountAsync` and `ToPagedListAsync` count
  groups; the typed aggregates refuse it.
- **`Select`** is documented as resolving every projected member, anonymous projections
  included, to the mapped column name, quoted.
- **`Where` expression support is now enumerated**, including `IN` translation from
  `collection.Contains(x.Id)` for arrays, `List<T>`, `HashSet<T>` and any `IEnumerable`
  (an empty collection emits `1=0`), the `bool`-member shorthand, and the fact that an
  unsupported predicate throws `NotSupportedException` out of `QueryBuilder.Where` itself
  rather than returning a failed `Result` (`Repository.FindAsync` does return a failed `Result`).
- **Events**: both `TableSyncedEvent` and `SlowQueryEvent` are listed with their payloads and
  the conditions that raise them.
- **Added a "Not included" section** naming what this library does not have that its
  `CL.MySQL2` / `CL.MSSQL` / `CL.PostgreSQL` siblings do — result cache, `SqlFn`, typed joins,
  cursor paging, transaction scopes, soft delete, bulk insert, a migration runner — plus how to
  get multi-statement atomicity via `ConnectionManager.ExecuteAsync`.
- `SyncNamespaceAsync` is documented as scanning the **calling assembly**, with the explicit
  `Assembly` overload for everything else.
- Noted that there is no `AverageAsync` / `AnyAsync` / `SingleAsync`, and that
  `ToPagedListAsync` rejects `page` or `pageSize` below 1.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- Query-builder parameter re-keying could corrupt SQL when a predicate emitted
  11+ parameters (`@p1` substring-collided with `@p10`/`@p11`); parameters are
  now renamed longest-name-first.
- WHERE-clause column names are now quoted, so entity properties mapped to SQL
  reserved words (e.g. `Order`, `Group`, `Index`) generate valid SQL.
- The connection pool now caps the number of concurrently live connections at
  `MaxPoolSize` (previously only the *returned* count was capped, allowing
  unbounded open connections under load).
- `GetPagedAsync` / `ToPagedListAsync` now validate that `page` and `pageSize`
  are >= 1 instead of generating a negative `OFFSET`.

### Documentation

- Full README and multi-page docs rewrite to house style. The README is now a
  concise NuGet/GitHub-friendly page (badges, tagline, install, quick start,
  features, configuration table + JSON, docs link, requirements, license). The
  docs site moves from a single `sqlite.md` page to a two-page set under
  `docs/libs/sqlite/`: an **Overview** (connection pool + WAL, entity attributes,
  schema sync, repository CRUD incl. composite keys, configuration, migration
  ledger, health check, events) and a **Query Builder** deep-dive (`Where`,
  ordering with `ThenBy`, projections, `GroupBy` aggregates, paging, terminals,
  bulk update/delete, raw SQL). Examples now use the library's actual `Result`
  surface (`.IsSuccess` / `.Value`). Navigation and the docs landing card were
  updated to point at the new pages. No API changes — documentation only.

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README to match the shipping API: the query builder is obtained
  via `GetQueryBuilder<T>()` (there is no `sqlite.Query<T>()`), all data
  operations return `Result` / `Result<T>`, and entities require
  `[SQLiteTable]` / `[SQLiteColumn]` annotations. The previous Quick Start no
  longer compiled.
- Documented the configuration as the real `databases` map (per-named-database
  `databasePath`, `useWAL`, `cacheMode`, `maxPoolSize`, `slowQueryThresholdMs`,
  timeouts, `skipTableSync`, `enableForeignKeys`), replacing the inaccurate
  `connections` array with `journalMode`/`poolSize`.
- Documented previously undocumented user-facing surface that already shipped:
  the full query builder (`Select`, `GroupBy`, `Sum`/`Max`/`Min`, predicate
  `DeleteAsync`/`UpdateAsync`, `ToPagedListAsync`), repository `UpsertAsync`,
  composite-key (`GetByKeysAsync`/`DeleteByKeysAsync`), `GetPagedAsync`, raw SQL
  (`RawQueryAsync`/`RawExecuteAsync`), attribute-driven schema sync
  (`SyncTableAsync`/`SyncTablesAsync`/`SyncNamespaceAsync` with
  `[SQLiteIndex]`/`[SQLiteForeignKey]`), and the `MigrationTracker`. No code
  changes — documentation only.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.3] — 2026-04-16

### Fixed

- Added missing `<param name="connectionId">` XML doc tags so the public API
  no longer trips doc-warning gates.

## [4.0.2] — 2026-04-09

### Changed

- Annotated SQLite configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Embedded-DB sibling of CL.MySQL2 with the same
repository pattern and attribute-driven schema sync.

### Notes

- The MySQL2 4.0 query-builder rewrite (projection pushdown, SQL aggregation,
  smart-cache pools) has not been ported to CL.SQLite yet — repository
  CRUD only.
- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.SQLite).
