using CL.Data.Backup;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Logical backup and restore, SQLite snapshots, and backup before destructive schema changes.</summary>
public abstract class BackupConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static IReadOnlyList<EntityMap> Maps =>
    [
        EntityAccessorRegistry.Get<SchemaParent>().ValueOrThrow().Map,
        EntityAccessorRegistry.Get<SchemaChild>().ValueOrThrow().Map,
    ];

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "cl-data-backup-" + Guid.NewGuid().ToString("N")[..8]);

    private async Task FreshTablesAsync()
    {
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_child")}")).ValueOrThrow();
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("sch_parent")}")).ValueOrThrow();
        (await Db.Schema.SyncAsync(Maps, new SchemaSyncOptions { Force = true, Level = SchemaSyncLevel.Safe })).ValueOrThrow();
    }

    private async Task<List<(long Id, string Code, string? Note, decimal Amount, bool Enabled, Guid Ref, DateTimeOffset Seen, int? Count)>> ParentsAsync() =>
        (await Db.Query<SchemaParent>().OrderBy(p => p.Id).ToListAsync()).ValueOrThrow()
            .Select(p => (p.Id, p.Code, p.Note, p.Amount, p.Enabled, p.Ref, p.Seen, p.Count)).ToList();

    [SkippableFact]
    public async Task Backup_and_restore_round_trip_rows_and_identity()
    {
        RequireProvider();
        await FreshTablesAsync();
        var parents = Db.Repository<SchemaParent>();
        var seen = new DateTimeOffset(2026, 10, 31, 12, 30, 15, 123, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            var parent = (await parents.InsertAsync(new SchemaParent
            {
                Code = $"C{i}", Region = "eu", Note = i % 2 == 0 ? null : $"note {i}", Amount = 10.25m * i, Enabled = i % 2 == 0,
                Ref = Guid.NewGuid(), Seen = seen.AddMinutes(i), Count = i == 3 ? null : i,
            })).ValueOrThrow();
            (await Db.Repository<SchemaChild>().InsertAsync(new SchemaChild { ParentId = parent.Id, Line = 1, Text = $"child {i}" })).ValueOrThrow();
        }
        var before = await ParentsAsync();

        var directory = TempDirectory();
        try
        {
            var manifest = (await Db.Backup.BackupAsync(directory, ["sch_child", "sch_parent"])).ValueOrThrow();
            Assert.Equal(["sch_parent", "sch_child"], manifest.Tables.Select(t => t.Table));   // referenced table first
            Assert.All(manifest.Tables, t => Assert.Equal(5L, t.Rows));

            (await Db.ExecuteSqlAsync($"DELETE FROM {Sql.Identifier("sch_child")}")).ValueOrThrow();
            (await Db.ExecuteSqlAsync($"UPDATE {Sql.Identifier("sch_parent")} SET {Sql.Identifier("Amount")} = {999m}")).ValueOrThrow();

            (await Db.Backup.RestoreAsync(directory)).ValueOrThrow();
            Assert.Equal(before, await ParentsAsync());
            Assert.Equal(5L, (await Db.Query<SchemaChild>().CountAsync()).ValueOrThrow());

            var next = (await parents.InsertAsync(new SchemaParent { Code = "N", Region = "eu", Ref = Guid.NewGuid(), Seen = seen })).ValueOrThrow();
            Assert.True(next.Id > before.Max(p => p.Id), $"identity continued at {next.Id}");

            var again = await Db.Backup.BackupAsync(directory, ["sch_parent"]);
            Assert.True(again.IsFailure);   // the directory is not empty
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Snapshots_are_sqlite_only()
    {
        RequireProvider();
        var file = Path.Combine(Path.GetTempPath(), $"cl-data-snap-{Guid.NewGuid():N}.db");
        try
        {
            var snapshot = await Db.Backup.SnapshotAsync(file);
            if (Db.Provider.Name == "sqlite")
            {
                snapshot.ThrowIfFailure();
                Assert.True(new FileInfo(file).Length > 0);
            }
            else
            {
                Assert.True(snapshot.IsFailure);
                Assert.Equal(DataErrors.UnsupportedCode, snapshot.Error!.Code);
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [SkippableFact]
    public async Task Destructive_sync_backs_the_table_up_first()
    {
        RequireProvider();
        await FreshTablesAsync();
        (await Db.Repository<SchemaParent>().InsertAsync(new SchemaParent { Code = "B", Region = "eu", Ref = Guid.NewGuid(), Seen = DateTimeOffset.UtcNow })).ValueOrThrow();
        var parent = (await Db.Provider.Introspector!.ReadTableAsync(Db, "sch_parent")).ValueOrThrow()!;
        foreach (var statement in Db.Provider.Ddl.Generate(new AddColumnChange("sch_parent", parent.Schema, new ColumnSchema("Extra", parent.Column("Count")!.StoreType, true))))
            (await Db.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();

        var root = TempDirectory();
        Db.Config.BackupDirectory = root;
        try
        {
            (await Db.Schema.SyncAsync(Maps, new SchemaSyncOptions { Force = true, Level = SchemaSyncLevel.Full })).ValueOrThrow();
            var folder = Assert.Single(Directory.GetDirectories(root));
            Assert.True(File.Exists(Path.Combine(folder, DataBackup.ManifestFile)));
            var rows = Assert.Single(Directory.GetFiles(folder, "*.jsonl"));
            Assert.Equal(2, File.ReadAllLines(rows).Length);   // header + one row
            Assert.Null((await Db.Provider.Introspector!.ReadTableAsync(Db, "sch_parent")).ValueOrThrow()!.Column("Extra"));
        }
        finally
        {
            Db.Config.BackupDirectory = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
