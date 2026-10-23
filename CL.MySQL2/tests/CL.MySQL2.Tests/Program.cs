using CodeLogic;                    // Libraries, CodeLogicOptions
using static CodeLogic.CodeLogic;   // InitializeAsync / ConfigureAsync / StartAsync / StopAsync
using CL.MySQL2;
using CL.MySQL2.Models;
using CL.MySQL2.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  CL.MySQL2 — comprehensive test suite (console runner)
//  Boots the real CodeLogic runtime against a local MySQL/MariaDB on
//  127.0.0.1:3310 (database cl_test) and exercises every major aspect of the
//  library: schema sync (+ the 3 SyncModes & CRC sentinel), data types, CRUD,
//  the LINQ query builder, joins, subqueries, aggregates, paging, raw SQL,
//  soft deletes, transactions, the result cache & coordinator, imperative
//  migrations & rollback, backup/restore, and connection management.
//  Prints PASS/FAIL per check; exits non-zero on any failure.
// ─────────────────────────────────────────────────────────────────────────────

// Framework root + DB connection are environment-driven so the suite carries no machine-specific
// paths and runs anywhere (CI / local). Defaults target a local MySQL/MariaDB on 127.0.0.1:3310.
static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
var Fw     = Env("CL_MYSQL2_TEST_ROOT", Path.Combine(Path.GetTempPath(), "cl_mysql2_tests"));
var dbHost = Env("CL_MYSQL2_TEST_HOST", "127.0.0.1");
var dbPort = Env("CL_MYSQL2_TEST_PORT", "3310");
var dbName = Env("CL_MYSQL2_TEST_DB",   "cl_test");
var dbUser = Env("CL_MYSQL2_TEST_USER", "root");
var dbPass = Env("CL_MYSQL2_TEST_PASS", "");

int total = 0, fails = 0;
void Check(string name, bool ok, string? detail = null)
{
    total++; if (!ok) fails++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(!ok && detail is not null ? $" — {detail}" : "")}");
}
void Section(string title) => Console.WriteLine($"\n=== {title} ===");

var init = await InitializeAsync(o =>
{
    o.FrameworkRootPath = Fw;
    o.AppVersion = "1.0.0";
    o.HandleShutdownSignals = false;
});
if (!init.Success) { Console.Error.WriteLine($"init failed: {init.Message}"); return 1; }

var cfgDir = Path.Combine(Fw, "Libraries", "CL.MySQL2");
Directory.CreateDirectory(cfgDir);
File.WriteAllText(Path.Combine(cfgDir, "config.mysql.json"), $$"""
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "{{dbHost}}",
      "port": {{dbPort}},
      "database": "{{dbName}}",
      "username": "{{dbUser}}",
      "password": "{{dbPass}}",
      "syncMode": "developer",
      "characterSet": "utf8mb4"
    }
  }
}
""");

await Libraries.LoadAsync<MySQL2Library>();
await ConfigureAsync();
await StartAsync();

var mysql = Libraries.Get<MySQL2Library>();
if (mysql is null) { Console.Error.WriteLine("MySQL2Library not available after start"); return 1; }
Console.WriteLine("CodeLogic booted; MySQL2 library running.");

try
{
    // ── 0. Clean slate ────────────────────────────────────────────────────────
    await Exec(mysql, """
        DROP TABLE IF EXISTS t_child, t_parent, t_emp, t_dept, t_person, t_account,
            t_kv, t_widget, t_note, t_evo, t_ren, t_mode, t_dev2, t_migt, t_locktest, t_backup,
            t_guard, t_add, t_future_marker, t_event
    """);
    foreach (var tbl in new[] { "t_person","t_widget","t_evo","t_ren","t_mode","t_dev2","t_locktest","t_backup","t_parent","t_child","t_guard","t_add" })
        await mysql.SchemaState.RemoveStateAsync(tbl);

    // ── 1. Connection management ──────────────────────────────────────────────
    Section("Connection management");
    Check("TestConnectionAsync succeeds", (await mysql.TestConnectionAsync()).Value);
    var info = await mysql.ConnectionManager.GetServerInfoAsync("Default");
    Check("server info has a version", !string.IsNullOrWhiteSpace(info.Version), info.Version);

    // ── 2. Schema sync: create / alter / index / data types ───────────────────
    Section("Schema sync — create / alter / index");
    Check("create t_evo (v1)", (await mysql.SyncTableAsync<EvoV1>(createBackup: false)).IsSuccess);
    await Exec(mysql, "INSERT INTO t_evo (name, note) VALUES ('keep', 'n')");
    var evo = (await mysql.SyncTableAsync<EvoV2>(createBackup: false)).Value!;
    var evoCols = await ColumnNames(mysql, "t_evo");
    Check("alter added new column 'extra'", evoCols.Contains("extra"));
    Check("alter kept data (row survives widening)",
        (await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM t_evo")).Value == 1);
    var nameType = (await mysql.SqlScalarAsync<string>(
        "SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_evo' AND COLUMN_NAME='name'")).Value ?? "";
    Check("alter widened varchar(50)->(100)", nameType.Contains("100"), nameType);
    Check("alter added index on 'note'", await HasIndexOn(mysql, "t_evo", "note"));

    // ── 3. Composite index + foreign key ──────────────────────────────────────
    Section("Composite index + foreign key");
    Check("create t_parent", (await mysql.SyncTableAsync<Parent>(createBackup: false)).IsSuccess);
    Check("create t_child (FK + composite index)", (await mysql.SyncTableAsync<Child>(createBackup: false)).IsSuccess);
    var fkCount = (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_child' AND REFERENCED_TABLE_NAME='t_parent'")).Value;
    Check("foreign key present on t_child", fkCount >= 1, $"got {fkCount}");
    Check("composite index ix_ab present", await HasNamedIndex(mysql, "t_child", "ix_ab"));

    var analyzer = new CL.MySQL2.Core.SchemaAnalyzer();
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        var stmts = await analyzer.GenerateAlterStatementsAsync(typeof(ChildDrift), conn, SchemaSyncLevel.Safe);
        Check("Schema sync drift at Safe level has no DROP", !stmts.Any(s => s.Contains("DROP", StringComparison.OrdinalIgnoreCase)));
        return true;
    }, "Default");

    // ── 4. Column rename (PreviousName) preserves data ────────────────────────
    Section("Column rename");
    Check("create t_ren (email)", (await mysql.SyncTableAsync<RenA>(createBackup: false)).IsSuccess);
    await Exec(mysql, "INSERT INTO t_ren (email) VALUES ('a@b.com')");
    var ren = (await mysql.SyncTableAsync<RenB>(createBackup: false)).Value!;
    Check("rename emitted CHANGE COLUMN",
        ren.Operations.Any(o => o.Contains("CHANGE COLUMN", StringComparison.OrdinalIgnoreCase)));
    var renRow = (await mysql.Query<RenB>().FirstOrDefaultAsync()).Value;
    Check("rename preserved data", renRow?.EmailAddress == "a@b.com", renRow?.EmailAddress);
    var renCols = await ColumnNames(mysql, "t_ren");
    Check("old column gone, new column present", !renCols.Contains("email") && renCols.Contains("email_address"));

    // ── 5. Data-type round-trip ───────────────────────────────────────────────
    Section("Data-type round-trip");
    Check("create t_widget", (await mysql.SyncTableAsync<Widget>(createBackup: false)).IsSuccess);
    var wrepo = mysql.GetRepository<Widget>();
    var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var code = Guid.NewGuid();
    var binKey = Guid.NewGuid();
    var w = (await wrepo.InsertAsync(new Widget
    {
        Code = code, BinKey = binKey, Title = "Gadget", Body = "long text body",
        Price = 12.34m, Qty = 7, Ratio = 3.5, Flag = true, WhenUtc = when,
        Meta = "{\"k\":1}", Maybe = null
    })).Value!;
    var got = (await wrepo.GetByIdAsync(w.Id)).Value!;
    Check("Guid (char) round-trips", got.Code == code, $"{got.Code}");
    Check("Guid (binary) round-trips", got.BinKey == binKey, $"{got.BinKey}");
    Check("decimal round-trips", got.Price == 12.34m, $"{got.Price}");
    Check("int round-trips", got.Qty == 7);
    Check("double round-trips", Math.Abs(got.Ratio - 3.5) < 1e-9);
    Check("bool round-trips", got.Flag);
    Check("datetime round-trips (to second)", got.WhenUtc == when, $"{got.WhenUtc:o}");
    Check("text round-trips", got.Body == "long text body");
    Check("json round-trips", got.Meta is not null && got.Meta.Contains("\"k\""));
    Check("nullable int stays null", got.Maybe is null);

    // ── 6. Repository CRUD ────────────────────────────────────────────────────
    Section("Repository CRUD");
    Check("create t_person", (await mysql.SyncTableAsync<Person>(createBackup: false)).IsSuccess);
    var prepo = mysql.GetRepository<Person>();
    var p1 = (await prepo.InsertAsync(new Person { Name = "Ann", Age = 30, Balance = 100m, Active = true, Score = 9.0, CreatedUtc = DateTime.UtcNow })).Value!;
    Check("InsertAsync populates PK", p1.Id > 0);
    var many = await prepo.InsertManyAsync(new[]
    {
        new Person { Name = "Ben", Age = 25, Balance = 50m,  Active = true,  Score = 7.5, CreatedUtc = DateTime.UtcNow },
        new Person { Name = "Cat", Age = 41, Balance = 200m, Active = false, Score = 6.0, CreatedUtc = DateTime.UtcNow },
        new Person { Name = "Dan", Age = 19, Balance = 0m,   Active = true,  Score = 8.0, CreatedUtc = DateTime.UtcNow },
    });
    Check("InsertManyAsync inserts 3", many.IsSuccess && many.Value == 3, $"{many.Value}");
    Check("CountAsync = 4", (await prepo.CountAsync()).Value == 4);
    Check("GetByIdAsync", (await prepo.GetByIdAsync(p1.Id)).Value?.Name == "Ann");
    Check("GetByColumnAsync", (await prepo.GetByColumnAsync("name", "Ben")).Value!.Count == 1);
    Check("GetAllAsync = 4", (await prepo.GetAllAsync()).Value!.Count == 4);
    Check("FindAsync (Age > 28)", (await prepo.FindAsync(x => x.Age > 28)).Value!.Count == 2);
    p1.Balance = 150m;
    Check("UpdateAsync(entity)", (await prepo.UpdateAsync(p1)).IsSuccess
        && (await prepo.GetByIdAsync(p1.Id)).Value!.Balance == 150m);
    var paged = (await prepo.GetPagedAsync(1, 2)).Value!;
    Check("GetPagedAsync page1 size2", paged.Items.Count == 2 && paged.TotalItems == 4 && paged.TotalPages == 2);
    var dan = (await prepo.GetByColumnAsync("name", "Dan")).Value![0];
    Check("DeleteAsync (hard, no soft-delete attr)", (await prepo.DeleteAsync(dan.Id)).Value);
    Check("CountAsync = 3 after delete", (await prepo.CountAsync()).Value == 3);

    // ── 7. Upsert + increment/decrement ───────────────────────────────────────
    Section("Upsert + increment");
    Check("create t_kv", (await mysql.SyncTableAsync<KV>(createBackup: false)).IsSuccess);
    var kv = mysql.GetRepository<KV>();
    await kv.UpsertAsync(new KV { Id = 1, Val = "a" });
    await kv.UpsertAsync(new KV { Id = 1, Val = "b" });
    Check("Upsert updates in place (Val=b)", (await kv.GetByIdAsync(1L)).Value?.Val == "b");
    Check("Upsert did not duplicate (1 row)", (await kv.CountAsync()).Value == 1);

    Check("create t_account", (await mysql.SyncTableAsync<Account>(createBackup: false)).IsSuccess);
    var arepo = mysql.GetRepository<Account>();
    var acct = (await arepo.InsertAsync(new Account { Owner = "ada", Credits = 10 })).Value!;
    await arepo.IncrementAsync(acct.Id, a => a.Credits, 5);
    Check("IncrementAsync (+5 → 15)", (await arepo.GetByIdAsync(acct.Id)).Value?.Credits == 15);
    await arepo.DecrementAsync(acct.Id, a => a.Credits, 3);
    Check("DecrementAsync (-3 → 12)", (await arepo.GetByIdAsync(acct.Id)).Value?.Credits == 12);

    // ── 7b. InsertIgnore + duplicate key opt-in ──────────────────────────────
    Section("InsertIgnore + duplicate key opt-in");
    await Exec(mysql, "DROP TABLE IF EXISTS t_event");
    Check("create t_event", (await mysql.SyncTableAsync<EventItem>(createBackup: false)).IsSuccess);
    var eventRepo = mysql.GetRepository<EventItem>();

    var e1 = new EventItem { ClientEventId = "ev-1", Payload = "first" };
    var res1 = await eventRepo.InsertIgnoreAsync(e1);
    Check("InsertIgnoreAsync first insert -> Inserted", res1.IsSuccess && res1.Value == InsertOutcome.Inserted);
    Check("InsertIgnoreAsync sets auto-increment Id", e1.Id > 0);

    var eDup = new EventItem { ClientEventId = "ev-1", Payload = "dup" };
    var resDup = await eventRepo.InsertIgnoreAsync(eDup);
    Check("InsertIgnoreAsync same key -> Duplicate", resDup.IsSuccess && resDup.Value == InsertOutcome.Duplicate);
    Check("InsertIgnoreAsync duplicate leaves DB row unchanged",
        (await mysql.Query<EventItem>().Where(x => x.ClientEventId == "ev-1").FirstOrDefaultAsync()).Value?.Payload == "first");

    var batch = new[]
    {
        new EventItem { ClientEventId = "ev-1", Payload = "dup" },
        new EventItem { ClientEventId = "ev-2", Payload = "two" },
        new EventItem { ClientEventId = "ev-3", Payload = "three" }
    };
    var resMany = await eventRepo.InsertManyIgnoreAsync(batch);
    Check("InsertManyIgnoreAsync mix returns inserted count (2)", resMany.IsSuccess && resMany.Value == 2);
    Check("InsertManyIgnoreAsync all duplicates returns 0", (await eventRepo.InsertManyIgnoreAsync(batch)).Value == 0);

    var resInsertFail = await eventRepo.InsertAsync(new EventItem { ClientEventId = "ev-1" });
    Check("InsertAsync duplicate key (opt-in off) -> mysql.insert_failed",
        !resInsertFail.IsSuccess && resInsertFail.Error?.Code == "mysql.insert_failed");

    eventRepo.DistinguishDuplicateKeyError = true;
    var resInsertDup = await eventRepo.InsertAsync(new EventItem { ClientEventId = "ev-1" });
    Check("InsertAsync duplicate key (opt-in on) -> mysql.duplicate_key",
        !resInsertDup.IsSuccess && resInsertDup.Error?.Code == "mysql.duplicate_key");
    var resManyDup = await eventRepo.InsertManyAsync(new[] { new EventItem { ClientEventId = "ev-1" } });
    Check("InsertManyAsync duplicate key (opt-in on) -> mysql.duplicate_key",
        !resManyDup.IsSuccess && resManyDup.Error?.Code == "mysql.duplicate_key");

    // ── 8. Query builder — filter / order / page / aggregates ─────────────────
    Section("Query builder");
    var asc = (await mysql.Query<Person>().OrderBy(p => p.Age).ToListAsync()).Value!;
    Check("OrderBy ascending", asc.Count == 3 && asc[0].Name == "Ben" && asc[^1].Name == "Cat");
    var desc = (await mysql.Query<Person>().OrderByDescending(p => p.Age).Take(1).ToListAsync()).Value!;
    Check("OrderByDescending + Take(1)", desc.Count == 1 && desc[0].Name == "Cat");
    var skip = (await mysql.Query<Person>().OrderBy(p => p.Age).Skip(1).Take(1).ToListAsync()).Value!;
    Check("Skip(1).Take(1)", skip.Count == 1 && skip[0].Name == "Ann");
    Check("Where + FirstOrDefault", (await mysql.Query<Person>().Where(p => p.Name == "Ann").FirstOrDefaultAsync()).Value?.Age == 30);
    Check("CountAsync with filter", (await mysql.Query<Person>().Where(p => p.Active).CountAsync()).Value == 2);
    Check("SumAsync(Balance)", (await mysql.Query<Person>().SumAsync(p => p.Balance)).Value == 400m);
    Check("MaxAsync(Age)", (await mysql.Query<Person>().MaxAsync(p => p.Age)).Value == 41);
    Check("MinAsync(Age)", (await mysql.Query<Person>().MinAsync(p => p.Age)).Value == 25);
    Check("AverageAsync(Age)", Math.Abs((await mysql.Query<Person>().AverageAsync(p => p.Age)).Value - 32.0) < 0.001);

    // Nullable aggregates on rows
    Check("MaxAsync((DateTime?)CreatedUtc) on rows", (await mysql.Query<Person>().MaxAsync(p => (DateTime?)p.CreatedUtc)).Value != null);
    Check("MinAsync((DateTime?)CreatedUtc) on rows", (await mysql.Query<Person>().MinAsync(p => (DateTime?)p.CreatedUtc)).Value != null);
    Check("SumAsync((int?)Age) on rows", (await mysql.Query<Person>().SumAsync(p => (int?)p.Age)).Value == (30 + 25 + 41));

    // Nullable aggregates on empty set
    var emptyQ = mysql.Query<Person>().Where(p => p.Age < 0);
    Check("MaxAsync((DateTime?)CreatedUtc) on empty -> null", (await emptyQ.MaxAsync(p => (DateTime?)p.CreatedUtc)).Value == null);
    Check("MinAsync((DateTime?)CreatedUtc) on empty -> null", (await emptyQ.MinAsync(p => (DateTime?)p.CreatedUtc)).Value == null);
    Check("SumAsync((int?)Age) on empty -> null", (await emptyQ.SumAsync(p => (int?)p.Age)).Value == null);
    Check("MaxAsync(CreatedUtc) non-nullable on empty -> default", (await emptyQ.MaxAsync(p => p.CreatedUtc)).Value == default);
    Check("SumAsync(Age) non-nullable on empty -> 0", (await emptyQ.SumAsync(p => p.Age)).Value == 0);
    var page = (await mysql.Query<Person>().OrderBy(p => p.Age).ToPagedListAsync(1, 2)).Value!;
    Check("ToPagedListAsync", page.Items.Count == 2 && page.TotalItems == 3);
    var proj = (await mysql.Query<Person>().Where(p => p.Active).Select(p => new NameOnly { Name = p.Name }).ToListAsync()).Value!;
    Check("Select projection", proj.Count == 2 && proj.All(x => !string.IsNullOrEmpty(x.Name)));

    // bulk update + bulk delete
    var upd = await mysql.Query<Person>().Where(p => p.Age < 30).UpdateAsync(p => new Person { Active = false });
    Check("bulk UpdateAsync (Age<30 → inactive)", upd.IsSuccess
        && (await mysql.Query<Person>().Where(p => p.Active).CountAsync()).Value == 1);
    var bulkDel = await mysql.Query<Person>().Where(p => p.Age < 0).DeleteAsync();
    Check("bulk DeleteAsync (no match → 0)", bulkDel.IsSuccess && bulkDel.Value == 0);

    // ── 9. Typed joins ────────────────────────────────────────────────────────
    Section("Typed joins");
    await mysql.SyncTableAsync<Dept>(createBackup: false);
    await mysql.SyncTableAsync<Emp>(createBackup: false);
    var drepo = mysql.GetRepository<Dept>();
    var eng = (await drepo.InsertAsync(new Dept { Name = "Eng" })).Value!;
    var sales = (await drepo.InsertAsync(new Dept { Name = "Sales" })).Value!;
    var erepo = mysql.GetRepository<Emp>();
    await erepo.InsertAsync(new Emp { DeptId = eng.Id, Name = "E1", Salary = 100m });
    await erepo.InsertAsync(new Emp { DeptId = eng.Id, Name = "E2", Salary = 120m });
    await erepo.InsertAsync(new Emp { DeptId = sales.Id, Name = "S1", Salary = 90m });
    var inner = (await mysql.Query<Emp>()
        .Where(e => e.Salary >= 100m)
        .Join<Dept, long, EmpView>(e => e.DeptId, d => d.Id,
            (e, d) => new EmpView { Emp = e.Name, Dept = d.Name, Salary = e.Salary })
        .OrderByDescending((e, d) => e.Salary)
        .ToListAsync()).Value!;
    Check("inner join filters + projects", inner.Count == 2 && inner[0].Emp == "E2" && inner[0].Dept == "Eng");
    var leftCount = (await mysql.Query<Emp>()
        .Join<Dept, long, EmpView>(e => e.DeptId, d => d.Id,
            (e, d) => new EmpView { Emp = e.Name, Dept = d.Name, Salary = e.Salary }, JoinType.Left)
        .CountAsync()).Value;
    Check("left join counts all emps", leftCount == 3, $"{leftCount}");

    // ── 10. Subqueries ────────────────────────────────────────────────────────
    Section("Subqueries");
    Check("WhereExists", (await mysql.Query<Dept>().WhereExists<Emp>((d, e) => e.DeptId == d.Id).CountAsync()).Value == 2);
    Check("WhereNotExists", (await mysql.Query<Dept>().WhereNotExists<Emp>((d, e) => e.DeptId == d.Id && e.Salary > 500m).CountAsync()).Value == 2);
    Check("WhereIn", (await mysql.Query<Emp>().WhereIn<Dept, long>(e => e.DeptId, d => d.Id, d => d.Name == "Eng").CountAsync()).Value == 2);
    Check("WhereNotIn", (await mysql.Query<Emp>().WhereNotIn<Dept, long>(e => e.DeptId, d => d.Id, d => d.Name == "Eng").CountAsync()).Value == 1);

    // ── 11. Raw SQL escape hatch ──────────────────────────────────────────────
    Section("Raw SQL");
    var rawRows = await mysql.SqlQueryAsync<Person>("SELECT * FROM t_person WHERE name = @n", new Dictionary<string, object?> { ["@n"] = "Ann" });
    Check("SqlQueryAsync materializes", rawRows.IsSuccess && rawRows.Value!.Count == 1 && rawRows.Value[0].Name == "Ann");
    Check("SqlScalarAsync", (await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM t_person")).Value == 3);
    Check("ExecuteSqlAsync returns affected", (await mysql.ExecuteSqlAsync("UPDATE t_person SET score = score WHERE 1=0")).Value == 0);

    // ── 12. Soft deletes ──────────────────────────────────────────────────────
    Section("Soft deletes");
    Check("create t_note", (await mysql.SyncTableAsync<Note>(createBackup: false)).IsSuccess);
    var nrepo = mysql.GetRepository<Note>();
    var n1 = (await nrepo.InsertAsync(new Note { Text = "keep" })).Value!;
    var n2 = (await nrepo.InsertAsync(new Note { Text = "drop" })).Value!;
    Check("DeleteAsync soft-deletes", (await nrepo.DeleteAsync(n2.Id)).Value);
    Check("query hides soft-deleted", (await mysql.Query<Note>().ToListAsync()).Value!.Count == 1);
    Check("IncludeDeleted shows all", (await mysql.Query<Note>().IncludeDeleted().ToListAsync()).Value!.Count == 2);
    Check("GetById hides soft-deleted", (await nrepo.GetByIdAsync(n2.Id)).Value is null);
    Check("HardDeleteAsync removes", (await nrepo.HardDeleteAsync(n2.Id)).Value);
    Check("hard delete gone", (await mysql.Query<Note>().IncludeDeleted().CountAsync()).Value == 1);

    // ── 13. Transactions ──────────────────────────────────────────────────────
    Section("Transactions");
    var before = (await prepo.CountAsync()).Value;
    await using (var tx = await mysql.BeginTransactionAsync())
    {
        var txRepo = new Repository<Person>(mysql.ConnectionManager, null, tx);
        await txRepo.InsertAsync(new Person { Name = "Committed", Age = 50, Balance = 1m, Active = true, Score = 1, CreatedUtc = DateTime.UtcNow });
        await tx.CommitAsync();
    }
    Check("commit persists row", (await prepo.CountAsync()).Value == before + 1);
    await using (var tx = await mysql.BeginTransactionAsync())
    {
        var txRepo = new Repository<Person>(mysql.ConnectionManager, null, tx);
        await txRepo.InsertAsync(new Person { Name = "Ghost", Age = 99, Balance = 1m, Active = true, Score = 1, CreatedUtc = DateTime.UtcNow });
        await tx.RollbackAsync();
    }
    Check("rollback discards row", (await prepo.CountAsync()).Value == before + 1
        && (await mysql.Query<Person>().Where(p => p.Name == "Ghost").CountAsync()).Value == 0);

    // ── 14. Result cache + coordinator ────────────────────────────────────────
    Section("Result cache + coordinator");
    var c1 = (await mysql.Query<Person>().WithCache(TimeSpan.FromMinutes(5)).ToListAsync()).Value!.Count;
    await prepo.InsertAsync(new Person { Name = "CacheNew", Age = 33, Balance = 5m, Active = true, Score = 5, CreatedUtc = DateTime.UtcNow });
    var c2 = (await mysql.Query<Person>().WithCache(TimeSpan.FromMinutes(5)).ToListAsync()).Value!.Count;
    Check("cache invalidated on insert (count grows)", c2 == c1 + 1, $"{c1}->{c2}");
    Check("cache stats track t_person", mysql.GetCacheStats().EntriesByTable.ContainsKey("t_person"));
    var fake = new FakeCoordinator();
    QueryCache.UseCoordinator(fake);
    Check("coordinator OnInvalidation wired", fake.HandlerWired);
    await mysql.Query<Person>().Where(p => p.Age < 0).UpdateAsync(p => new Person { Score = 0 });
    Check("mutation fans out to peers", fake.Published.Contains("t_person"));
    var bv = mysql.GetCacheStats().TableVersions.TryGetValue("t_account", out var b0) ? b0 : 0;
    fake.FireInvalidation("t_account");
    var av = mysql.GetCacheStats().TableVersions.TryGetValue("t_account", out var a0) ? a0 : 0;
    Check("peer broadcast bumps local version", av == bv + 1, $"{bv}->{av}");

    // ── 15. Sync modes + CRC sentinel ─────────────────────────────────────────
    Section("Sync modes + CRC sentinel");
    await Exec(mysql, "DROP TABLE IF EXISTS t_mode");
    await mysql.SchemaState.RemoveStateAsync("t_mode");
    mysql.SetSyncMode(SyncMode.Production);
    var sm1 = (await mysql.SyncTableAsync<ModeV1>(createBackup: false)).Value!;
    Check("first sync creates (not skipped)", !sm1.Skipped && sm1.Operations.Count > 0);
    var sm2 = (await mysql.SyncTableAsync<ModeV1>(createBackup: false)).Value!;
    Check("re-sync skipped via CRC", sm2.Skipped && sm2.SchemaCrc == sm1.SchemaCrc);
    Check("state row Synced", (await mysql.SchemaState.GetStateAsync("t_mode"))?.Status == SchemaSyncStatus.Synced);
    await Exec(mysql, "INSERT INTO t_mode (name, temp) VALUES ('x','y')");
    var prod = (await mysql.SyncTableAsync<ModeV2>(createBackup: false)).Value!;
    Check("Production keeps removed column", (await ColumnNames(mysql, "t_mode")).Contains("temp"));
    Check("Production flags DriftPending", prod.DriftPending);
    mysql.SetSyncMode(SyncMode.Migration);
    await mysql.SyncSchemaAsync(typeof(ModeV2));
    Check("Migration drops removed column", !(await ColumnNames(mysql, "t_mode")).Contains("temp"));
    Check("Migration leaves state Synced", (await mysql.SchemaState.GetStateAsync("t_mode"))?.Status == SchemaSyncStatus.Synced);
    Check("Migration re-run is no-op", (await mysql.SyncSchemaAsync(typeof(ModeV2))).Value!["t_mode"].Skipped);

    await Exec(mysql, "DROP TABLE IF EXISTS t_dev2");
    await mysql.SchemaState.RemoveStateAsync("t_dev2");
    mysql.SetSyncMode(SyncMode.Developer);
    await mysql.SyncTableAsync<DevA>(createBackup: false);
    var dev = (await mysql.SyncTableAsync<DevB>(createBackup: false)).Value!;
    Check("Developer drops removed column immediately",
        !(await ColumnNames(mysql, "t_dev2")).Contains("extra")
        && dev.Operations.Any(o => o.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase)));

    // ── 16. Concurrent schema sync (cross-node lock) ──────────────────────────
    Section("Concurrent schema sync (lock)");
    await Exec(mysql, "DROP TABLE IF EXISTS t_locktest");
    await mysql.SchemaState.RemoveStateAsync("t_locktest");
    var rr = await Task.WhenAll(mysql.SyncSchemaAsync(typeof(LockEnt)), mysql.SyncSchemaAsync(typeof(LockEnt)));
    Check("both concurrent passes succeed", rr.All(r => r.IsSuccess));
    Check("table created exactly once", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_locktest'")).Value == 1);

    // ── 17. Imperative migrations + rollback ──────────────────────────────────
    Section("Imperative migrations + rollback");
    await Exec(mysql, "DROP TABLE IF EXISTS t_migt");
    await Exec(mysql, "DELETE FROM __migrations WHERE MigrationId LIKE '1.0.0/%'");
    mysql.RegisterMigration(new M1Create());
    mysql.RegisterMigration(new M2Seed());
    Check("2 migrations pending", (await mysql.GetPendingMigrationsAsync()).Count == 2);
    Check("both applied in order", (await mysql.MigrateAsync()).Value!.Count == 2);
    Check("migration applied schema + data", (await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM t_migt")).Value == 2);
    Check("re-run is no-op", (await mysql.MigrateAsync()).Value!.Count == 0);
    Check("nothing pending after apply", (await mysql.GetPendingMigrationsAsync()).Count == 0);

    // Partial rollback to order 1 → reverts only M2Seed (order 2), keeps M1Create (order 1).
    var rbPartial = await mysql.RollbackAsync(new MigrationVersion("1.0.0", 1));
    Check("partial rollback reverts only newer (1)", rbPartial.IsSuccess && rbPartial.Value!.Count == 1, rbPartial.Error?.Message);
    Check("partial rollback kept M1 (t_migt present)", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_migt'")).Value == 1);
    Check("partial rollback removed only seed row", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM t_migt WHERE label='seed'")).Value == 0);
    Check("1 pending after partial rollback", (await mysql.GetPendingMigrationsAsync()).Count == 1);
    // Full rollback to 0 → reverts the remaining M1Create (drops t_migt).
    var rb = await mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
    Check("full rollback reverts remaining (1)", rb.IsSuccess && rb.Value!.Count == 1, rb.Error?.Message);
    Check("full rollback dropped t_migt", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_migt'")).Value == 0);
    Check("both pending again after full rollback", (await mysql.GetPendingMigrationsAsync()).Count == 2);

    await mysql.MigrateAsync();                 // re-apply reversible pair
    mysql.RegisterMigration(new M3NoDown());    // irreversible
    await mysql.MigrateAsync();
    var rb2 = await mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
    Check("rollback aborts on irreversible migration", rb2.IsFailure);
    Check("no partial rollback — t_migt remains", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_migt'")).Value == 1);

    // ── 18. Backup + restore ──────────────────────────────────────────────────
    Section("Backup + restore");
    mysql.SetSyncMode(SyncMode.Developer);
    await Exec(mysql, "DROP TABLE IF EXISTS t_backup");
    await mysql.SchemaState.RemoveStateAsync("t_backup");
    await mysql.SyncTableAsync<BackupEnt>(createBackup: false);
    Check("backup schema written", (await mysql.BackupManager.BackupTableSchemaAsync("t_backup")).Value);
    await Exec(mysql, "ALTER TABLE t_backup ADD COLUMN stray VARCHAR(10) NULL");
    Check("stray column added (pre-restore)", (await ColumnNames(mysql, "t_backup")).Contains("stray"));
    var restore = await mysql.RestoreSchemaAsync("t_backup");
    Check("restore succeeds", restore.IsSuccess, restore.Error?.Message);
    Check("restore reverted schema (stray gone)", !(await ColumnNames(mysql, "t_backup")).Contains("stray"));

    // ── 19. CRC existence guard (table dropped out-of-band → recreated) ────────
    Section("CRC existence guard");
    mysql.SetSyncMode(SyncMode.Developer);
    await Exec(mysql, "DROP TABLE IF EXISTS t_guard");
    await mysql.SchemaState.RemoveStateAsync("t_guard");
    await mysql.SyncTableAsync<GuardEnt>(createBackup: false);          // creates, state = Synced
    await Exec(mysql, "DROP TABLE t_guard");                            // drop WITHOUT clearing state row
    var guard = (await mysql.SyncTableAsync<GuardEnt>(createBackup: false)).Value!;
    Check("recreates table dropped out-of-band (not skipped)",
        !guard.Skipped && (await mysql.SqlScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_guard'")).Value == 1);

    // ── 20. Production additive ADD (new column, stays Synced) ─────────────────
    Section("Production additive add");
    mysql.SetSyncMode(SyncMode.Production);
    await Exec(mysql, "DROP TABLE IF EXISTS t_add");
    await mysql.SchemaState.RemoveStateAsync("t_add");
    await mysql.SyncTableAsync<AddV1>(createBackup: false);            // id, name
    var add = (await mysql.SyncTableAsync<AddV2>(createBackup: false)).Value!;  // id, name, note
    Check("Production adds new column", (await ColumnNames(mysql, "t_add")).Contains("note"));
    Check("Production add applied (not skipped)", !add.Skipped && add.Operations.Count > 0);
    Check("Production add stays Synced (no drift)",
        !add.DriftPending && (await mysql.SchemaState.GetStateAsync("t_add"))?.Status == SchemaSyncStatus.Synced);

    // ── 21. Migration edge cases: app-version gating, drift, failure ───────────
    Section("Migration edge cases");
    // (state at this point: M1Create/M2Seed/M3NoDown are registered + applied)
    // 21a App-version gating: a migration above the running app version (1.0.0) must be skipped.
    mysql.RegisterMigration(new MFuture());                            // tagged 2.0.0
    var pendGated = await mysql.GetPendingMigrationsAsync();
    Check("future-version migration excluded from pending",
        pendGated.All(p => p.Version.AppVersion != "2.0.0"));
    var gatedRun = await mysql.MigrateAsync();
    Check("future-version migration not applied", gatedRun.IsSuccess && gatedRun.Value!.Count == 0);
    Check("gated migration's table was never created", (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='t_future_marker'")).Value == 0);

    // 21b Checksum drift: corrupt an applied migration's recorded checksum → run is still safe and
    // does NOT re-apply it (a drift warning is logged; behaviour asserted here).
    await Exec(mysql, "UPDATE __migrations SET Checksum='deadbeef' WHERE MigrationId='1.0.0/001_M1Create'");
    var driftRun = await mysql.MigrateAsync();
    Check("checksum drift does not break the run or re-apply", driftRun.IsSuccess && driftRun.Value!.Count == 0);

    // 21c Failure handling: a throwing migration → failure returned, not recorded, can be retried.
    mysql.RegisterMigration(new MFail());                              // order 5, bad SQL in Up
    var failRun = await mysql.MigrateAsync();
    Check("failing migration returns failure", failRun.IsFailure);
    Check("failed migration is NOT recorded (still pending)",
        (await mysql.GetPendingMigrationsAsync()).Any(p => p.MigrationId == "1.0.0/005_MFail"));
}
finally
{
    await StopAsync();
}

Console.WriteLine($"\n{new string('=', 4)} {total - fails}/{total} passed ====");
if (fails > 0) Console.WriteLine($"{fails} FAILED");
return fails == 0 ? 0 : 1;

// ── helpers ──────────────────────────────────────────────────────────────────
static async Task Exec(MySQL2Library mysql, string sql) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
        return true;
    }, "Default");

static async Task<HashSet<string>> ColumnNames(MySQL2Library mysql, string table) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t";
        cmd.Parameters.AddWithValue("@t", table);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }, "Default");

static async Task<bool> HasIndexOn(MySQL2Library mysql, string table, string column) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND COLUMN_NAME=@c";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }, "Default");

static async Task<bool> HasNamedIndex(MySQL2Library mysql, string table, string index) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND INDEX_NAME=@i";
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@i", index);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }, "Default");

// ── entities ─────────────────────────────────────────────────────────────────
[Table(Name = "t_person")]
public class Person
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 60, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "age", DataType = DataType.Int, NotNull = true)] public int Age { get; set; }
    [Column(Name = "balance", DataType = DataType.Decimal, Precision = 12, Scale = 2, NotNull = true)] public decimal Balance { get; set; }
    [Column(Name = "active", DataType = DataType.TinyInt, NotNull = true)] public bool Active { get; set; }
    [Column(Name = "score", DataType = DataType.Double, NotNull = true)] public double Score { get; set; }
    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)] public DateTime CreatedUtc { get; set; }
}

public class NameOnly { public string Name { get; set; } = ""; }

[Table(Name = "t_kv")]
public class KV
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true)] public long Id { get; set; }
    [Column(Name = "val", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Val { get; set; } = "";
}

[Table(Name = "t_account")]
public class Account
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "owner", DataType = DataType.VarChar, Size = 50, NotNull = true, Unique = true)] public string Owner { get; set; } = "";
    [Column(Name = "credits", DataType = DataType.Int, NotNull = true)] public int Credits { get; set; }
}

[Table(Name = "t_widget")]
public class Widget
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "code", DataType = DataType.Char, Size = 36, NotNull = true)] public Guid Code { get; set; }
    [Column(Name = "bin_key", StorageType = StorageType.Binary, NotNull = true)] public Guid BinKey { get; set; }
    [Column(Name = "title", DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Title { get; set; } = "";
    [Column(Name = "body", DataType = DataType.Text)] public string? Body { get; set; }
    [Column(Name = "price", DataType = DataType.Decimal, Precision = 12, Scale = 2, NotNull = true)] public decimal Price { get; set; }
    [Column(Name = "qty", DataType = DataType.Int, NotNull = true)] public int Qty { get; set; }
    [Column(Name = "ratio", DataType = DataType.Double, NotNull = true)] public double Ratio { get; set; }
    [Column(Name = "flag", DataType = DataType.TinyInt, NotNull = true)] public bool Flag { get; set; }
    [Column(Name = "when_utc", DataType = DataType.DateTime, NotNull = true)] public DateTime WhenUtc { get; set; }
    [Column(Name = "meta", DataType = DataType.Json)] public string? Meta { get; set; }
    [Column(Name = "maybe", DataType = DataType.Int)] public int? Maybe { get; set; }
}

[Table(Name = "t_note")]
[SoftDelete(nameof(DeletedUtc))]
public class Note
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "text", DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Text { get; set; } = "";
    [Column(Name = "deleted_utc", DataType = DataType.DateTime)] public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "t_dept")]
public class Dept
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_emp")]
public class Emp
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "dept_id", DataType = DataType.BigInt, NotNull = true)] public long DeptId { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "salary", DataType = DataType.Decimal, Precision = 10, Scale = 2, NotNull = true)] public decimal Salary { get; set; }
}

public class EmpView { public string Emp { get; set; } = ""; public string Dept { get; set; } = ""; public decimal Salary { get; set; } }

// schema-evolution pair (same table)
[Table(Name = "t_evo")]
public class EvoV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "note", DataType = DataType.VarChar, Size = 50)] public string? Note { get; set; }
}

[Table(Name = "t_evo")]
public class EvoV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "note", DataType = DataType.VarChar, Size = 50, Index = true)] public string? Note { get; set; }
    [Column(Name = "extra", DataType = DataType.Int)] public int? Extra { get; set; }
}

// composite index + FK
[Table(Name = "t_parent")]
public class Parent
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "code", DataType = DataType.VarChar, Size = 30, NotNull = true, Unique = true)] public string Code { get; set; } = "";
}

[Table(Name = "t_child")]
[CompositeIndex("ix_ab", "a", "b")]
public class Child
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id", DataType = DataType.BigInt, NotNull = true)]
    [ForeignKey("t_parent", "id", OnDelete = ForeignKeyAction.Cascade)]
    public long ParentId { get; set; }
    [Column(Name = "a", DataType = DataType.Int, NotNull = true)] public int A { get; set; }
    [Column(Name = "b", DataType = DataType.Int, NotNull = true)] public int B { get; set; }
}

[Table(Name = "t_child")]
[CompositeIndex("ix_ab", "b", "a")]
public class ChildDrift
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id", DataType = DataType.BigInt, NotNull = true)] public long ParentId { get; set; }
    [Column(Name = "a", DataType = DataType.Int, NotNull = true)] public int A { get; set; }
    [Column(Name = "b", DataType = DataType.Int, NotNull = true)] public int B { get; set; }
}

[Table(Name = "t_event")]
public class EventItem
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "client_event_id", DataType = DataType.VarChar, Size = 64, NotNull = true, Unique = true)] public string ClientEventId { get; set; } = "";
    [Column(Name = "payload", DataType = DataType.VarChar, Size = 100)] public string? Payload { get; set; }
}

// rename pair
[Table(Name = "t_ren")]
public class RenA
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "email", DataType = DataType.VarChar, Size = 200, NotNull = true)] public string Email { get; set; } = "";
}

[Table(Name = "t_ren")]
public class RenB
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "email_address", DataType = DataType.VarChar, Size = 200, PreviousName = "email", NotNull = true)] public string EmailAddress { get; set; } = "";
}

// sync-mode pairs
[Table(Name = "t_mode")]
public class ModeV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "temp", DataType = DataType.VarChar, Size = 50)] public string? Temp { get; set; }
}

[Table(Name = "t_mode")]
public class ModeV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_dev2")]
public class DevA
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "extra", DataType = DataType.VarChar, Size = 50)] public string? Extra { get; set; }
}

[Table(Name = "t_dev2")]
public class DevB
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_locktest")]
public class LockEnt
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "val", DataType = DataType.VarChar, Size = 50)] public string? Val { get; set; }
}

[Table(Name = "t_backup")]
public class BackupEnt
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_migt")]
public class MigThing
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Label { get; set; } = "";
}

// migrations
public sealed class M1Create : Migration
{
    public M1Create() : base("1.0.0", 1, "create t_migt + seed") { }
    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct)
    {
        await ctx.SyncTableAsync<MigThing>(ct);
        await ctx.ExecuteAsync("INSERT INTO t_migt (label) VALUES ('up')", ct: ct);
    }
    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("DROP TABLE IF EXISTS t_migt", ct: ct);
}

public sealed class M2Seed : Migration
{
    public M2Seed() : base("1.0.0", 2, "seed extra row") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("INSERT INTO t_migt (label) VALUES ('seed')", ct: ct);
    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("DELETE FROM t_migt WHERE label='seed'", ct: ct);
}

public sealed class M3NoDown : Migration
{
    public M3NoDown() : base("1.0.0", 3, "irreversible") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("INSERT INTO t_migt (label) VALUES ('nodown')", ct: ct);
}

// Tagged above the running app version (1.0.0) → must be gated out of pending/apply.
public sealed class MFuture : Migration
{
    public MFuture() : base("2.0.0", 1, "future-gated") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("CREATE TABLE t_future_marker (id INT)", ct: ct);
    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("DROP TABLE IF EXISTS t_future_marker", ct: ct);
}

// UpAsync targets a non-existent table → throws, exercising failure handling.
public sealed class MFail : Migration
{
    public MFail() : base("1.0.0", 5, "failing migration") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("INSERT INTO t_does_not_exist_zzz (x) VALUES (1)", ct: ct);
}

[Table(Name = "t_guard")]
public class GuardEnt
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_add")]
public class AddV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "t_add")]
public class AddV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "note", DataType = DataType.VarChar, Size = 100)] public string? Note { get; set; }
}

// fake multi-node coordinator
public sealed class FakeCoordinator : ICacheCoordinator
{
    public readonly List<string> Published = [];
    private Action<string>? _handler;
    public bool HandlerWired => _handler is not null;
    public void FireInvalidation(string table) => _handler?.Invoke(table);
    public Task PublishInvalidationAsync(string tableName, CancellationToken ct = default) { Published.Add(tableName); return Task.CompletedTask; }
    public void OnInvalidation(Action<string> handler) => _handler = handler;
    public Task<bool> TryAcquireRefreshLeaseAsync(string poolName, TimeSpan lease, CancellationToken ct = default) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
