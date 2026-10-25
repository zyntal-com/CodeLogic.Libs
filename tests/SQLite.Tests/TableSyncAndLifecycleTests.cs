using CL.SQLite;
using CL.SQLite.Events;
using CL.SQLite.Models;
using CL.SQLite.Services;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace SQLite.Tests;

// ── Entities for the sync tests ───────────────────────────────────────────────

[SQLiteTable("sync_alpha")]
public sealed class SyncAlpha
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.TEXT)]
    public string V { get; set; } = "";
}

[SQLiteTable("sync_beta")]
public sealed class SyncBeta
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.INTEGER)]
    public int V { get; set; }
}

/// <summary>Carries every index-shaped attribute option so the created indexes can be inspected.</summary>
[SQLiteTable("sync_indexed")]
[SQLiteIndex("a", "b", Name = "ix_sync_named")]
[SQLiteIndex("c", IsUnique = true)]
public sealed class SyncIndexed
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "a", DataType = SQLiteDataType.TEXT)]
    public string A { get; set; } = "";

    [SQLiteColumn(ColumnName = "b", DataType = SQLiteDataType.INTEGER, IsIndexed = true)]
    public int B { get; set; }

    [SQLiteColumn(ColumnName = "c", DataType = SQLiteDataType.TEXT, IsUnique = true)]
    public string C { get; set; } = "";

    [SQLiteColumn(ColumnName = "d", DataType = SQLiteDataType.INTEGER, DefaultValue = "5")]
    public int D { get; set; }
}

/// <summary>Foreign-key parent/child pair used to check the generated REFERENCES clause.</summary>
[SQLiteTable("sync_fk_parent")]
public sealed class SyncFkParent
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    // A second column is needed only because an entity consisting solely of an auto-increment
    // primary key cannot be inserted — see Inserting_an_entity_whose_only_column_is_the_key.
    [SQLiteColumn(ColumnName = "label", DataType = SQLiteDataType.TEXT)]
    public string Label { get; set; } = "";
}

/// <summary>Entity whose only column is an auto-increment primary key.</summary>
[SQLiteTable("sync_id_only")]
public sealed class SyncIdOnly
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }
}

[SQLiteTable("sync_fk_child")]
public sealed class SyncFkChild
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "parent_id", DataType = SQLiteDataType.INTEGER)]
    [SQLiteForeignKey("sync_fk_parent", "id", OnDelete = ForeignKeyAction.Cascade)]
    public long ParentId { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class TableSyncAndLifecycleTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public TableSyncAndLifecycleTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private Task<List<string>> ColumnsOf(string table) =>
        Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}');";
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            return names;
        });

    private Task<List<(string Name, string Sql)>> IndexesOf(string table) =>
        Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name, COALESCE(sql, '') FROM sqlite_master WHERE type='index' AND tbl_name=@t;";
            cmd.Parameters.AddWithValue("@t", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<(string, string)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        });

    // ── SyncTableAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncTableAsync_creates_the_table_the_first_time_and_is_idempotent_after()
    {
        var repo = Lib.GetRepository<SyncAlpha>();
        await repo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_alpha\";");

        var created = await Lib.TableSync.SyncTableAsync<SyncAlpha>();
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.StartsWith("Created table", created.Value!.Message);

        var again = await Lib.TableSync.SyncTableAsync<SyncAlpha>();
        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.Contains("up to date", again.Value!.Message);
    }

    [Fact]
    public async Task SyncTableAsync_adds_a_column_that_is_missing_from_an_existing_table()
    {
        var repo = Lib.GetRepository<SyncBeta>();
        await repo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_beta\";");
        await repo.RawExecuteAsync("CREATE TABLE \"sync_beta\" (\"id\" INTEGER PRIMARY KEY AUTOINCREMENT);");

        var synced = await Lib.TableSync.SyncTableAsync<SyncBeta>();
        Assert.True(synced.IsSuccess, synced.Error?.Message);
        Assert.Contains("added columns [v]", synced.Value!.Message);

        Assert.Equal(["id", "v"], await ColumnsOf("sync_beta"));
    }

    [Fact]
    public async Task Adding_a_column_records_a_migration()
    {
        var repo = Lib.GetRepository<SyncBeta>();
        await repo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_beta\";");
        await repo.RawExecuteAsync("CREATE TABLE \"sync_beta\" (\"id\" INTEGER PRIMARY KEY AUTOINCREMENT);");
        await Lib.MigrationTracker.RemoveMigrationRecordAsync("add_column_sync_beta_v");

        Assert.True((await Lib.TableSync.SyncTableAsync<SyncBeta>()).IsSuccess);

        Assert.True(await Lib.MigrationTracker.HasMigrationBeenAppliedAsync("add_column_sync_beta_v"));
    }

    [Fact]
    public async Task Creating_a_table_records_a_create_table_migration()
    {
        await Lib.GetRepository<SyncAlpha>().RawExecuteAsync("DROP TABLE IF EXISTS \"sync_alpha\";");
        await Lib.MigrationTracker.RemoveMigrationRecordAsync("create_table_sync_alpha");

        Assert.True((await Lib.TableSync.SyncTableAsync<SyncAlpha>()).IsSuccess);

        Assert.True(await Lib.MigrationTracker.HasMigrationBeenAppliedAsync("create_table_sync_alpha"));
        Assert.Contains(await Lib.MigrationTracker.GetAppliedMigrationsAsync(),
            m => m.MigrationId == "create_table_sync_alpha");
    }

    [Fact]
    public async Task SyncTableAsync_creates_both_class_level_and_property_level_indexes()
    {
        await Lib.GetRepository<SyncIndexed>().RawExecuteAsync("DROP TABLE IF EXISTS \"sync_indexed\";");
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncIndexed>()).IsSuccess);

        var indexes = await IndexesOf("sync_indexed");
        var byName = indexes.ToDictionary(i => i.Name, i => i.Sql);

        // [SQLiteIndex("a","b", Name = "ix_sync_named")] — explicit name, both columns.
        Assert.True(byName.ContainsKey("ix_sync_named"));
        Assert.Contains("\"a\"", byName["ix_sync_named"]);
        Assert.Contains("\"b\"", byName["ix_sync_named"]);
        Assert.DoesNotContain("UNIQUE", byName["ix_sync_named"]);

        // [SQLiteIndex("c", IsUnique = true)] — derived name, UNIQUE.
        Assert.True(byName.ContainsKey("idx_sync_indexed_c"));
        Assert.Contains("UNIQUE", byName["idx_sync_indexed_c"]);

        // [SQLiteColumn(IsIndexed = true)] on "b" — property-level index.
        Assert.True(byName.ContainsKey("idx_sync_indexed_b"));
        Assert.DoesNotContain("UNIQUE", byName["idx_sync_indexed_b"]);
    }

    [Fact]
    public async Task A_unique_column_actually_rejects_a_duplicate()
    {
        await Lib.GetRepository<SyncIndexed>().RawExecuteAsync("DROP TABLE IF EXISTS \"sync_indexed\";");
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncIndexed>()).IsSuccess);
        var repo = Lib.GetRepository<SyncIndexed>();
        var value = "u" + Guid.NewGuid().ToString("N")[..8];

        Assert.True((await repo.InsertAsync(new SyncIndexed { C = value })).IsSuccess);

        var duplicate = await repo.InsertAsync(new SyncIndexed { C = value });
        Assert.True(duplicate.IsFailure);
        Assert.Contains("UNIQUE", duplicate.Error!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_DefaultValue_on_a_column_is_applied_by_SQLite_when_the_column_is_omitted()
    {
        await Lib.GetRepository<SyncIndexed>().RawExecuteAsync("DROP TABLE IF EXISTS \"sync_indexed\";");
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncIndexed>()).IsSuccess);
        var repo = Lib.GetRepository<SyncIndexed>();
        var marker = "d" + Guid.NewGuid().ToString("N")[..8];

        // The repository always writes every column, so the default only shows through raw SQL.
        Assert.True((await repo.RawExecuteAsync(
            "INSERT INTO \"sync_indexed\" (\"a\", \"c\") VALUES (@a, @c);",
            new Dictionary<string, object?> { ["@a"] = marker, ["@c"] = marker })).IsSuccess);

        var row = await Lib.GetQueryBuilder<SyncIndexed>().Where(r => r.A == marker).FirstOrDefaultAsync();
        Assert.True(row.IsSuccess, row.Error?.Message);
        Assert.Equal(5, row.Value!.D);
    }

    [Fact]
    public async Task A_generated_foreign_key_is_enforced_end_to_end()
    {
        var childRepo = Lib.GetRepository<SyncFkChild>();
        await childRepo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_fk_child\";");
        await childRepo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_fk_parent\";");
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncFkParent>()).IsSuccess);
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncFkChild>()).IsSuccess);

        var orphan = await childRepo.InsertAsync(new SyncFkChild { ParentId = 999_999 });
        Assert.True(orphan.IsFailure);
        Assert.Contains("FOREIGN KEY", orphan.Error!.ToString(), StringComparison.OrdinalIgnoreCase);

        var parent = await Lib.GetRepository<SyncFkParent>().InsertAsync(new SyncFkParent { Label = "p" });
        Assert.True(parent.IsSuccess, parent.Error?.ToString());
        var parentId = parent.Value;
        var child = await childRepo.InsertAsync(new SyncFkChild { ParentId = parentId });
        Assert.True(child.IsSuccess, child.Error?.ToString());

        // ON DELETE CASCADE comes from the attribute.
        Assert.True((await Lib.GetRepository<SyncFkParent>().DeleteAsync(parentId)).IsSuccess);
        var remaining = await Lib.GetQueryBuilder<SyncFkChild>().Where(c => c.ParentId == parentId).CountAsync();
        Assert.Equal(0, remaining.Value);
    }

    /// <summary>
    /// <c>Repository.InsertAsync</c> skips every auto-increment column when building its column
    /// list, so an entity whose only column is the auto-increment primary key has nothing to
    /// write and is inserted as <c>INSERT INTO "t" DEFAULT VALUES;</c>.
    /// </summary>
    [Fact]
    public async Task Inserting_an_entity_whose_only_column_is_the_key_succeeds()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncIdOnly>()).IsSuccess);

        var result = await Lib.GetRepository<SyncIdOnly>().InsertAsync(new SyncIdOnly());
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value > 0);
    }

    [Fact]
    public async Task Two_key_only_inserts_get_distinct_row_ids_and_the_entity_is_back_filled()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncIdOnly>()).IsSuccess);
        var repo = Lib.GetRepository<SyncIdOnly>();

        var first = new SyncIdOnly();
        var second = new SyncIdOnly();
        Assert.True((await repo.InsertAsync(first)).IsSuccess);
        Assert.True((await repo.InsertAsync(second)).IsSuccess);

        Assert.True(first.Id > 0);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task SyncTableAsync_reports_a_failure_for_an_unknown_connection_id()
    {
        var result = await Lib.TableSync.SyncTableAsync<SyncAlpha>("NoSuchDatabase");

        Assert.True(result.IsFailure);
        Assert.Contains("NoSuchDatabase", result.Error!.ToString());
    }

    // ── SyncTablesAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SyncTablesAsync_syncs_every_type_and_keys_the_results_by_table_name()
    {
        var repo = Lib.GetRepository<SyncAlpha>();
        await repo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_alpha\";");
        await repo.RawExecuteAsync("DROP TABLE IF EXISTS \"sync_beta\";");

        var results = await Lib.TableSync.SyncTablesAsync([typeof(SyncAlpha), typeof(SyncBeta)]);

        Assert.Equal(2, results.Count);
        Assert.True(results["sync_alpha"].IsSuccess, results["sync_alpha"].Error?.Message);
        Assert.True(results["sync_beta"].IsSuccess, results["sync_beta"].Error?.Message);
        Assert.Equal(["id", "v"], await ColumnsOf("sync_alpha"));
        Assert.Equal(["id", "v"], await ColumnsOf("sync_beta"));
    }

    [Fact]
    public async Task SyncTablesAsync_with_no_types_returns_an_empty_dictionary()
        => Assert.Empty(await Lib.TableSync.SyncTablesAsync([]));

    [Fact]
    public async Task SyncTablesAsync_keys_results_case_insensitively()
    {
        var results = await Lib.TableSync.SyncTablesAsync([typeof(SyncAlpha)]);
        Assert.True(results.ContainsKey("SYNC_ALPHA"));
    }

    [Fact]
    public async Task SyncTablesAsync_records_a_per_type_failure_without_abandoning_the_rest()
    {
        var results = await Lib.TableSync.SyncTablesAsync([typeof(BadDdlProbe), typeof(SyncAlpha)]);

        Assert.Equal(2, results.Count);
        Assert.True(results["repo_bad_ddl_probe"].IsFailure);
        Assert.True(results["sync_alpha"].IsSuccess, results["sync_alpha"].Error?.Message);
    }

    /// <summary>
    /// <c>SyncTablesAsync</c> reflects over <c>SyncTableAsync&lt;T&gt;</c>, whose generic
    /// constraint is <c>where T : class</c>. A value type therefore fails at
    /// <c>MakeGenericMethod</c> and is captured as that entry's failure rather than thrown.
    /// </summary>
    [Fact]
    public async Task SyncTablesAsync_captures_a_value_type_as_a_failure_rather_than_throwing()
    {
        var results = await Lib.TableSync.SyncTablesAsync([typeof(int)]);

        var entry = Assert.Single(results);
        Assert.Equal("Int32", entry.Key);
        Assert.True(entry.Value.IsFailure);
    }

    // ── SyncNamespaceAsync ────────────────────────────────────────────────────

    /// <summary>
    /// <c>SyncNamespaceAsync</c> captures <c>Assembly.GetCallingAssembly()</c> in a non-async
    /// wrapper, before the state machine starts, so it really does see this test assembly.
    /// </summary>
    [Fact]
    public async Task SyncNamespaceAsync_syncs_the_entities_in_the_callers_namespace()
    {
        var results = await Lib.TableSync.SyncNamespaceAsync("SQLite.Tests");

        Assert.NotEmpty(results);
        Assert.True(results.ContainsKey("sync_alpha"));
    }

    [Fact]
    public async Task SyncNamespaceAsync_accepts_an_explicit_assembly()
    {
        var results = await Lib.TableSync.SyncNamespaceAsync(
            typeof(SyncAlpha).Assembly, "SQLite.Tests");

        Assert.True(results.ContainsKey("sync_alpha"));
        Assert.True(results["sync_alpha"].IsSuccess, results["sync_alpha"].Error?.Message);
    }

    [Fact]
    public async Task SyncNamespaceAsync_returns_an_empty_dictionary_for_a_namespace_with_no_entities()
        => Assert.Empty(await Lib.TableSync.SyncNamespaceAsync("No.Such.Namespace"));

    // ── Events ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Syncing_a_table_publishes_a_TableSyncedEvent_marked_created()
    {
        var bus = CodeLogic.CodeLogic.GetEventBus();
        var seen = new List<TableSyncedEvent>();
        bus.Subscribe<TableSyncedEvent>(e => { lock (seen) seen.Add(e); });

        await Lib.GetRepository<SyncAlpha>().RawExecuteAsync("DROP TABLE IF EXISTS \"sync_alpha\";");
        var before = DateTime.UtcNow.AddSeconds(-5);
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncAlpha>()).IsSuccess);

        for (var i = 0; i < 60; i++)
        {
            lock (seen) if (seen.Any(e => e.TableName == "sync_alpha")) break;
            await Task.Delay(25);
        }

        TableSyncedEvent evt;
        lock (seen) evt = Assert.Single(seen, e => e.TableName == "sync_alpha" && e.Created);
        Assert.StartsWith("Created table", evt.Message);
        Assert.InRange(evt.SyncedAt, before, DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task A_no_op_sync_publishes_a_TableSyncedEvent_that_is_not_marked_created()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<SyncAlpha>()).IsSuccess); // ensure it exists

        var bus = CodeLogic.CodeLogic.GetEventBus();
        var seen = new List<TableSyncedEvent>();
        bus.Subscribe<TableSyncedEvent>(e => { lock (seen) seen.Add(e); });

        Assert.True((await Lib.TableSync.SyncTableAsync<SyncAlpha>()).IsSuccess);

        for (var i = 0; i < 60; i++)
        {
            lock (seen) if (seen.Any(e => e.TableName == "sync_alpha")) break;
            await Task.Delay(25);
        }

        TableSyncedEvent evt;
        lock (seen) evt = seen.First(e => e.TableName == "sync_alpha");
        Assert.False(evt.Created);
        Assert.Contains("up to date", evt.Message);
    }

    [Fact]
    public async Task A_failed_sync_publishes_no_event()
    {
        var bus = CodeLogic.CodeLogic.GetEventBus();
        var seen = new List<TableSyncedEvent>();
        bus.Subscribe<TableSyncedEvent>(e => { lock (seen) seen.Add(e); });

        Assert.True((await Lib.TableSync.SyncTableAsync<BadDdlProbe>()).IsFailure);
        await Task.Delay(200);

        lock (seen) Assert.DoesNotContain(seen, e => e.TableName == "repo_bad_ddl_probe");
    }

    /// <summary>
    /// A <c>SlowQueryThresholdMs</c> of 0 makes *every* query slow, so one query is enough to
    /// see the event. The repository is constructed directly because the fixture's configuration
    /// leaves the threshold at its 500 ms default, under which nothing here would be slow.
    /// </summary>
    [Fact]
    public async Task A_slow_query_publishes_a_SlowQueryEvent()
    {
        var bus = CodeLogic.CodeLogic.GetEventBus();
        var seen = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e => { lock (seen) seen.Add(e); });

        Assert.True((await Lib.TableSync.SyncTableAsync<SyncAlpha>()).IsSuccess);
        var repo = new Repository<SyncAlpha>(Lib.ConnectionManager, null, "Default", slowQueryThresholdMs: 0);
        Assert.True((await repo.CountAsync()).IsSuccess);

        for (var i = 0; i < 60; i++)
        {
            lock (seen) if (seen.Any(e => e.TableName == "sync_alpha")) break;
            await Task.Delay(25);
        }

        SlowQueryEvent evt;
        lock (seen) evt = seen.First(e => e.TableName == "sync_alpha");
        Assert.Contains("COUNT(*)", evt.Query, StringComparison.OrdinalIgnoreCase);
        Assert.True(evt.ElapsedMs >= 0);
    }

    /// <summary>
    /// The threshold still gates the event: nothing is published for a query that finishes
    /// inside it, which is what the default 500 ms configuration produces for these queries.
    /// </summary>
    [Fact]
    public async Task No_SlowQueryEvent_is_published_below_the_threshold()
    {
        var bus = CodeLogic.CodeLogic.GetEventBus();
        var seen = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e => { lock (seen) seen.Add(e); });

        Assert.True((await Lib.TableSync.SyncTableAsync<SyncBeta>()).IsSuccess);
        var repo = new Repository<SyncBeta>(Lib.ConnectionManager, null, "Default", slowQueryThresholdMs: 60_000);
        for (var i = 0; i < 5; i++)
            Assert.True((await repo.CountAsync()).IsSuccess);
        await Task.Delay(300);

        lock (seen) Assert.DoesNotContain(seen, e => e.TableName == "sync_beta");
    }

    [Fact]
    public void The_event_records_are_plain_value_types()
    {
        var at = DateTime.UtcNow;
        var a = new TableSyncedEvent("t", true, "m", at);
        Assert.Equal(a, new TableSyncedEvent("t", true, "m", at));
        Assert.NotEqual(a, a with { Created = false });

        var s = new SlowQueryEvent("t", "SELECT 1", 42, at);
        Assert.Equal(s, new SlowQueryEvent("t", "SELECT 1", 42, at));
        Assert.Equal(42, s.ElapsedMs);
    }

    // ── Library lifecycle and manifest ────────────────────────────────────────

    [Fact]
    public void The_manifest_identifies_the_library()
    {
        var m = Lib.Manifest;

        Assert.Equal("CL.SQLite", m.Id);
        Assert.Equal("SQLite Library", m.Name);
        Assert.Equal("Zyntal.com", m.Author);
        Assert.False(string.IsNullOrWhiteSpace(m.Version));
        Assert.False(string.IsNullOrWhiteSpace(m.Description));
        Assert.Contains("sqlite", m.Tags);
        Assert.Contains("database", m.Tags);
    }

    [Fact]
    public void The_manifest_instance_is_stable_across_reads()
        => Assert.Same(Lib.Manifest, Lib.Manifest);

    [Fact]
    public void A_booted_library_exposes_its_services()
    {
        Assert.NotNull(Lib.ConnectionManager);
        Assert.NotNull(Lib.TableSync);
        Assert.NotNull(Lib.MigrationTracker);
        Assert.Same(Lib.TableSync.MigrationTracker, Lib.MigrationTracker);
        Assert.Contains("Default", Lib.ConnectionManager.GetConnectionIds());
    }

    [Fact]
    public void The_configured_database_path_is_the_one_the_fixture_wrote()
    {
        var path = Lib.ConnectionManager.GetDatabasePath();

        Assert.Equal(Path.Combine(_fx.TempDir, "test.db"), Path.GetFullPath(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task The_booted_database_is_in_WAL_mode_as_the_fixture_configured()
    {
        var mode = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            return Convert.ToString(await cmd.ExecuteScalarAsync())!;
        });

        Assert.Equal("wal", mode.ToLowerInvariant());
    }

    [Fact]
    public async Task HealthCheckAsync_reports_the_configured_databases_in_its_data()
    {
        var health = await Lib.HealthCheckAsync();

        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.NotNull(health.Data);
        Assert.Equal(1, Convert.ToInt32(health.Data!["totalDatabases"]));
        Assert.Equal(0, Convert.ToInt32(health.Data["failedDatabases"]));

        var connections = Assert.IsType<Dictionary<string, Dictionary<string, object>>>(health.Data["connections"]);
        var def = connections["Default"];
        Assert.Equal(Lib.ConnectionManager.GetDatabasePath(), def["databasePath"]);
        Assert.Equal(0, Convert.ToInt32(def["activeConnections"]));
    }

    [Fact]
    public async Task TestConnectionAsync_on_the_booted_database_succeeds()
        => Assert.True(await Lib.ConnectionManager.TestConnectionAsync());

    /// <summary>
    /// An instance that has never been through <c>OnConfigureAsync</c>/<c>OnInitializeAsync</c>
    /// must refuse to hand out services rather than return null. A second instance is safe to
    /// construct because nothing touches the runtime until a lifecycle phase runs.
    /// </summary>
    [Fact]
    public void An_uninitialized_library_throws_from_every_service_accessor()
    {
        var fresh = new SQLiteLibrary();

        Assert.Throws<InvalidOperationException>(() => fresh.ConnectionManager);
        Assert.Throws<InvalidOperationException>(() => fresh.TableSync);
        Assert.Throws<InvalidOperationException>(() => fresh.MigrationTracker);
        Assert.Throws<InvalidOperationException>(() => fresh.GetRepository<SyncAlpha>());
        Assert.Throws<InvalidOperationException>(() => fresh.GetQueryBuilder<SyncAlpha>());
        Assert.Equal("CL.SQLite", fresh.Manifest.Id); // the manifest needs no initialization
    }

    [Fact]
    public async Task Stopping_and_disposing_an_uninitialized_library_is_safe_and_idempotent()
    {
        var fresh = new SQLiteLibrary();

        await fresh.OnStopAsync();
        await fresh.OnStopAsync();
        fresh.Dispose();
        fresh.Dispose();
    }

    /// <summary>
    /// A library that never saw <c>OnInitializeAsync</c> reports healthy-because-disabled rather
    /// than unhealthy: <c>_isEnabled</c> is still false, which is the same state a configuration
    /// with no enabled databases leaves it in.
    /// </summary>
    [Fact]
    public async Task An_uninitialized_library_health_checks_as_healthy_but_disabled()
    {
        var health = await new SQLiteLibrary().HealthCheckAsync();

        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.Contains("disabled", health.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_library_implements_the_framework_contract()
    {
        Assert.IsAssignableFrom<ILibrary>(Lib);
        Assert.IsAssignableFrom<IDisposable>(Lib);
    }
}
