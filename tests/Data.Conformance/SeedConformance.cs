using CL.Data;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Schema;
using CL.Data.Seeding;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Reference data seeded by the conformance suite.</summary>
[Table(Name = "conf_roles")]
public sealed class SeedRole
{
    /// <summary>Key.</summary>
    [Column(Primary = true)] public int Id { get; set; }
    /// <summary>Name.</summary>
    [Column(Size = 50)] public string Name { get; set; } = "";
}

/// <summary>Seeds: run once, re-run on a version bump, in order, atomically.</summary>
public abstract class SeedConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private sealed class DelegateSeed(string id, int order, Func<Database, CancellationToken, ValueTask<Result>> run, int version = 1)
        : DataSeed(id, order, version)
    {
        public override ValueTask<Result> SeedAsync(Database db, CancellationToken ct) => run(db, ct);
    }

    private async Task ResetAsync()
    {
        (await Db.Admin.DropTableAsync<SeedRole>()).ThrowIfFailure();
        (await Db.Schema.SyncAsync<SeedRole>(new SchemaSyncOptions { Force = true })).ValueOrThrow();
        (await Db.Seeds([]).RunAsync()).ValueOrThrow();   // creates the history table
        (await Db.ExecuteSqlAsync($"DELETE FROM {Sql.Identifier(SeedRunner.HistoryTable)}")).ValueOrThrow();
    }

    private async Task<string[]> NamesAsync() => (await Db.Query<SeedRole>().OrderBy(r => r.Id).ToListAsync()).ValueOrThrow().Select(r => r.Name).ToArray();

    [SkippableFact]
    public async Task Seeds_run_once_and_again_after_a_version_bump()
    {
        RequireProvider();
        await ResetAsync();
        var v1 = DataSeed.Rows<SeedRole>("roles", [new() { Id = 1, Name = "admin" }, new() { Id = 2, Name = "user" }]);

        Assert.Equal(["roles"], (await Db.Seeds([v1]).RunAsync()).ValueOrThrow().Applied);
        Assert.Equal(["admin", "user"], await NamesAsync());

        (await Db.ExecuteSqlAsync($"UPDATE conf_roles SET {Sql.Identifier("Name")} = {"edited"} WHERE {Sql.Identifier("Id")} = {1}")).ValueOrThrow();
        var again = (await Db.Seeds([v1]).RunAsync()).ValueOrThrow();
        Assert.Equal(["roles"], again.UpToDate);
        Assert.Equal(["edited", "user"], await NamesAsync());   // not re-applied

        var v2 = DataSeed.Rows<SeedRole>("roles", [new() { Id = 1, Name = "administrator" }, new() { Id = 2, Name = "user" }, new() { Id = 3, Name = "guest" }], version: 2);
        Assert.Equal(["roles"], (await Db.Seeds([v2]).RunAsync()).ValueOrThrow().Applied);
        Assert.Equal(["administrator", "user", "guest"], await NamesAsync());
    }

    [SkippableFact]
    public async Task Seeds_run_in_order_and_failures_roll_back()
    {
        RequireProvider();
        await ResetAsync();
        var roles = DataSeed.Rows<SeedRole>("roles", [new() { Id = 1, Name = "admin" }], order: 1);
        var dependent = new DelegateSeed("upper", 2, async (db, ct) =>
        {
            var admin = (await db.Repository<SeedRole>().GetByIdAsync(1, ct)).ValueOrThrow()!;   // needs the earlier seed
            admin.Name = admin.Name.ToUpperInvariant();
            var updated = await db.Repository<SeedRole>().UpdateAsync(admin, ct);
            return updated.IsFailure ? updated.Error! : Result.Success();
        });
        var failing = new DelegateSeed("broken", 3, async (db, ct) =>
        {
            (await db.Repository<SeedRole>().InsertAsync(new SeedRole { Id = 9, Name = "partial" }, ct)).ValueOrThrow();
            return DataErrors.Validation("boom");
        });

        var run = await Db.Seeds([failing, dependent, roles]).RunAsync();   // registration order does not matter
        Assert.True(run.IsFailure);
        Assert.Equal(["ADMIN"], await NamesAsync());                         // the failed seed's insert was rolled back

        var retry = (await Db.Seeds([roles, dependent, DataSeed.Rows<SeedRole>("broken", [new() { Id = 9, Name = "fixed" }], order: 3)]).RunAsync()).ValueOrThrow();
        Assert.Equal(["broken"], retry.Applied);                             // only the unrecorded seed runs again
        Assert.Equal(["ADMIN", "fixed"], await NamesAsync());
    }
}
