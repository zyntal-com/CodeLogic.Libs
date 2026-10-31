using CL.Data;
using CL.Data.Errors;
using CL.Data.Migrations;
using CL.Data.Schema;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Entity created by the conformance migrations.</summary>
[Table(Name = "mig_items")]
public sealed class MigrationItem
{
    /// <summary>Identity.</summary>
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    /// <summary>Name.</summary>
    [Column(Size = 50)] public string Name { get; set; } = "";
}

/// <summary>A migration built from delegates (tests).</summary>
public sealed class DelegateMigration(string appVersion, int order, string description,
    Func<IMigrationContext, CancellationToken, ValueTask<Result>> up,
    Func<IMigrationContext, CancellationToken, ValueTask<Result>>? down = null) : Migration(appVersion, order, description)
{
    /// <inheritdoc />
    public override bool CanRollback => down is not null;

    /// <inheritdoc />
    public override ValueTask<Result> UpAsync(IMigrationContext context, CancellationToken ct) => up(context, ct);

    /// <inheritdoc />
    public override ValueTask<Result> DownAsync(IMigrationContext context, CancellationToken ct) =>
        down is null ? base.DownAsync(context, ct) : down(context, ct);
}

/// <summary>Migrations: apply, record, filter by app version, roll back, checksum drift, failure atomicity.</summary>
public abstract class MigrationConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private static async ValueTask<Result> Done<T>(ValueTask<Result<T>> work)
    {
        var result = await work;
        return result.IsFailure ? result.Error! : Result.Success();
    }

    private static IMigration CreateTable() => new DelegateMigration("1.0.0", 1, "create mig_items",
        (c, ct) => Done(c.SyncTableAsync<MigrationItem>(ct: ct)),
        (c, ct) => Done(c.ExecuteAsync($"DROP TABLE {Sql.Identifier("mig_items")}", ct)));

    private static IMigration Seed(string description = "seed first item") => new DelegateMigration("1.0.0", 2, description,
        (c, ct) => Done(c.Db.Repository<MigrationItem>().InsertAsync(new MigrationItem { Name = "first" }, ct)),
        (c, ct) => Done(c.ExecuteAsync($"DELETE FROM {Sql.Identifier("mig_items")} WHERE {Sql.Identifier("Name")} = {"first"}", ct)));

    private static IMigration AddColumn() => new DelegateMigration("1.1.0", 1, "add mig_items.Note",
        async (c, ct) =>
        {
            var table = (await c.ReadTableAsync("mig_items", ct: ct)).ValueOrThrow()!;
            return await c.ApplyAsync(new AddColumnChange("mig_items", table.Schema, new ColumnSchema("Note", table.Column("Name")!.StoreType, true)), ct);
        },
        async (c, ct) =>
        {
            var table = (await c.ReadTableAsync("mig_items", ct: ct)).ValueOrThrow()!;
            var without = table with { Columns = [.. table.Columns.Where(col => col.Name != "Note")] };
            return await c.ApplyAsync(new DropColumnChange("mig_items", table.Schema, "Note", without), ct);
        });

    private async Task ResetAsync()
    {
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("mig_items")}")).ValueOrThrow();
        (await Db.ExecuteSqlAsync($"DROP TABLE IF EXISTS {Sql.Identifier("mig_failed")}")).ValueOrThrow();
        (await Db.Migrations([]).PendingAsync()).ValueOrThrow();   // creates the history table when missing
        (await Db.ExecuteSqlAsync($"DELETE FROM {Sql.Identifier(MigrationRunner.HistoryTable)}")).ValueOrThrow();
    }

    private async Task<TableSchema?> ItemsAsync() => (await Db.Provider.Introspector!.ReadTableAsync(Db, "mig_items")).ValueOrThrow();

    [SkippableFact]
    public async Task Migrate_applies_in_order_and_records_history()
    {
        RequireProvider();
        await ResetAsync();
        var runner = Db.Migrations([AddColumn(), Seed(), CreateTable()]);   // registration order does not matter

        Assert.Equal(["1.0.0/001", "1.0.0/002", "1.1.0/001"], (await runner.PendingAsync()).ValueOrThrow().Select(p => p.Version.ToString()));
        var run = (await runner.MigrateAsync()).ValueOrThrow();
        Assert.Equal(3, run.Applied.Count);
        Assert.Empty((await runner.PendingAsync()).ValueOrThrow());
        Assert.Equal(3, (await runner.AppliedAsync()).ValueOrThrow().Count);
        Assert.NotNull((await ItemsAsync())!.Column("Note"));
        Assert.Equal(1L, (await Db.Query<MigrationItem>().CountAsync()).ValueOrThrow());

        Assert.Empty((await runner.MigrateAsync()).ValueOrThrow().Applied);   // idempotent
    }

    [SkippableFact]
    public async Task App_version_limits_what_runs()
    {
        RequireProvider();
        await ResetAsync();
        var runner = Db.Migrations([CreateTable(), Seed(), AddColumn()], new MigrationOptions { AppVersion = "1.0.5" });
        Assert.Equal(2, (await runner.MigrateAsync()).ValueOrThrow().Applied.Count);
        Assert.Null((await ItemsAsync())!.Column("Note"));
    }

    [SkippableFact]
    public async Task Rollback_reverts_newest_first()
    {
        RequireProvider();
        await ResetAsync();
        var runner = Db.Migrations([CreateTable(), Seed(), AddColumn()]);
        (await runner.MigrateAsync()).ValueOrThrow();

        var partial = (await runner.RollbackAsync(new MigrationVersion("1.0.0", 1))).ValueOrThrow();
        Assert.Equal(["1.1.0/001", "1.0.0/002"], partial.Applied.Select(v => v.ToString()));
        Assert.Null((await ItemsAsync())!.Column("Note"));
        Assert.Equal(0L, (await Db.Query<MigrationItem>().CountAsync()).ValueOrThrow());

        (await runner.RollbackAsync(new MigrationVersion("0.0.0", 0))).ValueOrThrow();
        Assert.Null(await ItemsAsync());
        Assert.Empty((await runner.AppliedAsync()).ValueOrThrow());
    }

    [SkippableFact]
    public async Task Rollback_refuses_irreversible_migrations_and_checksums_detect_edits()
    {
        RequireProvider();
        await ResetAsync();
        var irreversible = new DelegateMigration("1.0.0", 3, "no way back", (c, ct) => ValueTask.FromResult(Result.Success()));
        (await Db.Migrations([CreateTable(), Seed(), irreversible]).MigrateAsync()).ValueOrThrow();

        var refused = await Db.Migrations([CreateTable(), Seed(), irreversible]).RollbackAsync(new MigrationVersion("0.0.0", 0));
        Assert.True(refused.IsFailure);
        Assert.Equal(DataErrors.UnsupportedCode, refused.Error!.Code);
        Assert.NotNull(await ItemsAsync());   // nothing was rolled back

        var edited = Db.Migrations([CreateTable(), Seed("seed the first item (edited)"), irreversible]);
        var applied = (await edited.AppliedAsync()).ValueOrThrow();
        Assert.True(Assert.Single(applied, a => a.Id == "1.0.0/002").ChecksumChanged);
        Assert.Equal(["1.0.0/002"], (await edited.MigrateAsync()).ValueOrThrow().ChecksumChanged);
    }

    [SkippableFact]
    public async Task Failed_migration_is_not_recorded_and_rolls_back_where_ddl_is_transactional()
    {
        RequireProvider();
        await ResetAsync();
        var failing = new DelegateMigration("2.0.0", 1, "fails halfway", async (c, ct) =>
        {
            var created = await c.ExecuteAsync($"CREATE TABLE {Sql.Identifier("mig_failed")} ({Sql.Identifier("Id")} INT NOT NULL)", ct);
            if (created.IsFailure) return created.Error!;
            return DataErrors.Validation("boom");
        });
        var runner = Db.Migrations([CreateTable(), failing]);

        var run = await runner.MigrateAsync();
        Assert.True(run.IsFailure);
        Assert.Equal(DataErrors.MigrationFailedCode, run.Error!.Code);
        Assert.Equal(["1.0.0/001"], (await runner.AppliedAsync()).ValueOrThrow().Select(a => a.Id));   // the first stays applied
        Assert.Equal("2.0.0/001", Assert.Single((await runner.PendingAsync()).ValueOrThrow()).Version.ToString());
        if (Db.Provider.Ddl.TransactionalDdl)
            Assert.Null((await Db.Provider.Introspector!.ReadTableAsync(Db, "mig_failed")).ValueOrThrow());
    }
}
