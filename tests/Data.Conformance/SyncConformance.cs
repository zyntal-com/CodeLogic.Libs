using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Table sync: create, fingerprint skip, drift, additions, rebuild safety and advisory locks.</summary>
public abstract class SyncConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static IReadOnlyList<EntityMap> Maps =>
    [
        EntityAccessorRegistry.Get<SchemaChild>().ValueOrThrow().Map,    // child first: sync must reorder
        EntityAccessorRegistry.Get<SchemaParent>().ValueOrThrow().Map,
    ];

    private async Task ResetSchemaAsync()
    {
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_child")}")).ValueOrThrow();
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_parent")}")).ValueOrThrow();
        (await Db.Schema.SyncAsync([])).ValueOrThrow();   // creates the state table when missing
        (await Db.ExecuteSqlAsync($"DELETE FROM {Sql.Identifier(SchemaSync.StateTable)}")).ValueOrThrow();
    }

    private static SchemaSyncOptions At(SchemaSyncLevel level, bool force = false) => new() { Level = level, Force = force };

    private async Task<TableSchema> ParentAsync() =>
        (await Db.Provider.Introspector!.ReadTableAsync(Db, "sch_parent")).ValueOrThrow()!;

    private async Task RunAsync(SchemaChange change)
    {
        foreach (var statement in Db.Provider.Ddl.Generate(change))
            (await Db.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();
    }

    [SkippableFact]
    public async Task Creates_tables_in_dependency_order_then_skips_by_fingerprint()
    {
        RequireProvider();
        await ResetSchemaAsync();
        List<DataEvent> events = [];
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Events = events.Add });

        var first = (await db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe))).ValueOrThrow();
        Assert.Equal([TableSyncOutcome.Created, TableSyncOutcome.Created], first.Tables.Select(t => t.Outcome));
        Assert.EndsWith("sch_parent", first.Tables[0].Table);                  // referenced table first
        Assert.Equal(2, events.OfType<SchemaSyncedEvent>().Count(e => e.Created));

        var second = (await db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe))).ValueOrThrow();
        Assert.All(second.Tables, t => Assert.Equal(TableSyncOutcome.Unchanged, t.Outcome));

        var forced = (await db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe, force: true))).ValueOrThrow();
        Assert.All(forced.Tables, t => Assert.Equal(TableSyncOutcome.UpToDate, t.Outcome));
        Assert.True((await db.Schema.PlanAsync<SchemaParent>()).ValueOrThrow().IsEmpty);
    }

    [SkippableFact]
    public async Task Safe_level_adds_missing_columns_and_reports_extra_ones_as_drift()
    {
        RequireProvider();
        await ResetSchemaAsync();
        (await Db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe))).ValueOrThrow();
        var parent = await ParentAsync();

        // Someone added a column by hand and the model gained one (Note was dropped from the table).
        await RunAsync(new AddColumnChange("sch_parent", parent.Schema, new ColumnSchema("Extra", parent.Column("Count")!.StoreType, true)));
        var withoutNote = parent with { Columns = [.. parent.Columns.Where(c => c.Name != "Note"), new ColumnSchema("Extra", parent.Column("Count")!.StoreType, true)] };
        await RunAsync(new DropColumnChange("sch_parent", parent.Schema, "Note", withoutNote));

        List<DataEvent> events = [];
        await using var db = Fixture.CreateDatabase(new DatabaseOptions { Events = events.Add });
        var report = (await db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe, force: true))).ValueOrThrow();
        var result = Assert.Single(report.Tables, t => t.Table.EndsWith("sch_parent", StringComparison.Ordinal));
        Assert.Equal(TableSyncOutcome.Drift, result.Outcome);
        Assert.Contains(result.Applied, a => a.Contains("add column", StringComparison.Ordinal) && a.Contains("Note", StringComparison.Ordinal));
        Assert.Contains(result.Pending, p => p.Contains("drop column", StringComparison.Ordinal) && p.Contains("Extra", StringComparison.Ordinal));
        Assert.True(report.HasDrift);
        Assert.Contains(events, e => e is SchemaDriftDetectedEvent d && d.Table.EndsWith("sch_parent", StringComparison.Ordinal));
        Assert.NotNull((await ParentAsync()).Column("Note"));
        Assert.NotNull((await ParentAsync()).Column("Extra"));

        // Drift is not recorded, so the next (unforced) run looks again; Full removes the extra column.
        var full = (await db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Full))).ValueOrThrow();
        Assert.Equal(TableSyncOutcome.Altered, Assert.Single(full.Tables, t => t.Table.EndsWith("sch_parent", StringComparison.Ordinal)).Outcome);
        Assert.Null((await ParentAsync()).Column("Extra"));
    }

    [SkippableFact]
    public async Task Script_lists_the_changes_without_applying_them_and_is_empty_once_applied()
    {
        RequireProvider();
        await ResetSchemaAsync();
        var created = (await Db.Schema.ScriptAsync(Maps, SchemaSyncLevel.Safe)).ValueOrThrow();
        Assert.Equal(2, created.Tables.Count);
        Assert.EndsWith("sch_parent", created.Tables[0].Table);                   // referenced table first
        Assert.All(created.Tables, t => Assert.IsType<CreateTableChange>(t.Changes[0]));
        Assert.Contains("CREATE TABLE", created.ToSql(), StringComparison.Ordinal);
        Assert.Null((await Db.Provider.Introspector!.ReadTableAsync(Db, "sch_parent")).ValueOrThrow());   // nothing ran

        // Running the script is the same as syncing: afterwards the script is empty.
        foreach (var statement in created.Statements)
            (await Db.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();
        Assert.True((await Db.Schema.ScriptAsync(Maps)).ValueOrThrow().IsEmpty);

        // A hand-added column is drift: Safe lists it as pending only, Full scripts the drop.
        var parent = await ParentAsync();
        await RunAsync(new AddColumnChange("sch_parent", parent.Schema, new ColumnSchema("Extra", parent.Column("Count")!.StoreType, true)));
        var safe = (await Db.Schema.ScriptAsync(Maps, SchemaSyncLevel.Safe)).ValueOrThrow();
        Assert.True(safe.HasPending);
        Assert.Empty(safe.Statements);
        var full = (await Db.Schema.ScriptAsync(Maps, SchemaSyncLevel.Full)).ValueOrThrow();
        Assert.False(full.HasPending);
        Assert.IsType<DropColumnChange>(Assert.Single(Assert.Single(full.Tables).Changes));
        Assert.NotEmpty(full.Statements);                                       // ALTER ... DROP, or a rebuild on SQLite
    }

    [SkippableFact]
    public async Task Making_a_column_not_null_keeps_rows_that_held_null()
    {
        RequireProvider();
        await ResetSchemaAsync();
        (await Db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe))).ValueOrThrow();
        (await Db.Repository<SchemaParent>().InsertAsync(new SchemaParent { Code = "N", Region = "eu", Ref = Guid.NewGuid(), Seen = DateTimeOffset.UtcNow, Note = null })).ValueOrThrow();

        // The model says Note is NOT NULL now: Full backfills the NULL, then tightens the column.
        var parent = await ParentAsync();
        var note = parent.Column("Note")!;
        var change = new AlterColumnChange("sch_parent", parent.Schema, note, note with { Nullable = false },
            parent with { Columns = parent.Columns.Select(c => c.Name == "Note" ? c with { Nullable = false } : c).ToArray() });
        await RunAsync(change);

        var tightened = await ParentAsync();
        Assert.False(tightened.Column("Note")!.Nullable);
        var row = (await Db.Query<SchemaParent>().FirstOrDefaultAsync()).ValueOrThrow()!;
        Assert.Equal("", row.Note);
    }

    [SkippableFact]
    public async Task Changes_keep_rows_in_parent_and_child_tables()
    {
        RequireProvider();
        await ResetSchemaAsync();
        (await Db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Safe))).ValueOrThrow();
        var parentId = (await Db.Repository<SchemaParent>().InsertAsync(new SchemaParent { Code = "P", Region = "eu", Ref = Guid.NewGuid(), Seen = DateTimeOffset.UtcNow })).ValueOrThrow().Id;
        (await Db.Repository<SchemaChild>().InsertAsync(new SchemaChild { ParentId = parentId, Line = 1, Text = "child" })).ValueOrThrow();

        // An extra column forces a drop at Full; SQLite rebuilds the parent table, which must not cascade into the child.
        var parent = await ParentAsync();
        await RunAsync(new AddColumnChange("sch_parent", parent.Schema, new ColumnSchema("Extra", parent.Column("Count")!.StoreType, true)));
        (await Db.Schema.SyncAsync(Maps, At(SchemaSyncLevel.Full, force: true))).ValueOrThrow();

        Assert.Equal(1L, (await Db.Query<SchemaParent>().CountAsync()).ValueOrThrow());
        Assert.Equal(1L, (await Db.Query<SchemaChild>().CountAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Advisory_locks_exclude_other_sessions()
    {
        RequireProvider();
        var name = "cl_data_test_" + Guid.NewGuid().ToString("N")[..8];
        var first = await Db.AcquireLockAsync(name, TimeSpan.FromSeconds(5));
        if (!Db.Provider.Capabilities.AdvisoryLocks)
        {
            Assert.True(first.IsFailure);
            Assert.Equal(DataErrors.UnsupportedCode, first.Error!.Code);
            return;
        }

        await using (first.ValueOrThrow())
        {
            var blocked = await Db.AcquireLockAsync(name, TimeSpan.FromMilliseconds(300));
            Assert.True(blocked.IsFailure);
            Assert.Equal(DataErrors.LockNotAcquiredCode, blocked.Error!.Code);
        }

        await using var again = (await Db.AcquireLockAsync(name, TimeSpan.FromSeconds(5))).ValueOrThrow();
        Assert.Equal(name, again.Name);
    }
}
