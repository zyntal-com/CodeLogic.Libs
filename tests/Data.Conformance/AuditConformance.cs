using CL.Data;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CL.Data.Schema;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>An entity with every audit column.</summary>
[Table(Name = "conf_audit")]
public sealed class AuditedNote
{
    /// <summary>Key.</summary>
    [Column(Primary = true)] public long Id { get; set; }
    /// <summary>Text.</summary>
    [Column(Size = 100)] public string Text { get; set; } = "";
    /// <summary>Counter.</summary>
    public int Hits { get; set; }
    /// <summary>Created.</summary>
    [CreatedAt] public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Updated.</summary>
    [UpdatedAt] public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Creator.</summary>
    [CreatedBy, Column(Size = 50)] public string? CreatedBy { get; set; }
    /// <summary>Last editor.</summary>
    [UpdatedBy, Column(Size = 50)] public string? UpdatedBy { get; set; }
}

/// <summary>Audit columns on every write path, and entity save hooks.</summary>
public abstract class AuditConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class User(string name) : IDataUserAccessor
    {
        public string? CurrentUser => name;
    }

    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 2, 9, 30, 0, TimeSpan.Zero);

    private async Task<(Database Alice, Database Bob, Clock Clock)> SetupAsync(params IDataInterceptor[] interceptors)
    {
        (await Db.Schema.SyncAsync<AuditedNote>(new SchemaSyncOptions { Force = true })).ValueOrThrow();
        (await Db.Admin.TruncateAsync<AuditedNote>()).ThrowIfFailure();
        var clock = new Clock(T1);
        var alice = Fixture.CreateDatabase(new DatabaseOptions { Time = clock, UserAccessor = new User("alice"), Interceptors = interceptors });
        var bob = Fixture.CreateDatabase(new DatabaseOptions { Time = clock, UserAccessor = new User("bob"), Interceptors = interceptors });
        return (alice, bob, clock);
    }

    private async Task<AuditedNote> ReadAsync(long id) => (await Db.Query<AuditedNote>().FirstOrDefaultAsync(n => n.Id == id)).ValueOrThrow()!;

    [SkippableFact]
    public async Task Audit_columns_are_stamped_on_every_write_path()
    {
        RequireProvider();
        var (alice, bob, clock) = await SetupAsync();
        await using var _ = alice;
        await using var __ = bob;

        (await alice.Repository<AuditedNote>().InsertAsync(new AuditedNote { Id = 1, Text = "a" })).ValueOrThrow();
        var inserted = await ReadAsync(1);
        Assert.Equal((T1, T1, "alice", "alice"), (inserted.CreatedAt, inserted.UpdatedAt, inserted.CreatedBy, inserted.UpdatedBy));

        clock.Now = T2;
        inserted.Text = "b";
        inserted.CreatedBy = "mallory";   // created columns are never written by an update
        (await bob.Repository<AuditedNote>().UpdateAsync(inserted)).ValueOrThrow();
        var updated = await ReadAsync(1);
        Assert.Equal((T1, T2, "alice", "bob"), (updated.CreatedAt, updated.UpdatedAt, updated.CreatedBy, updated.UpdatedBy));

        // Upsert: the insert path stamps everything, the update path keeps the created columns.
        clock.Now = T1;
        (await alice.Repository<AuditedNote>().UpsertAsync(new AuditedNote { Id = 2, Text = "u1" })).ValueOrThrow();
        clock.Now = T2;
        (await bob.Repository<AuditedNote>().UpsertAsync(new AuditedNote { Id = 2, Text = "u2" })).ValueOrThrow();
        var upserted = await ReadAsync(2);
        Assert.Equal(("u2", T1, T2, "alice", "bob"), (upserted.Text, upserted.CreatedAt, upserted.UpdatedAt, upserted.CreatedBy, upserted.UpdatedBy));

        // Set-based update and counters stamp the updated columns too.
        var later = T2.AddHours(1);
        clock.Now = later;
        (await alice.Query<AuditedNote>().Where(n => n.Id == 1).UpdateAsync(n => new AuditedNote { Text = "set" })).ValueOrThrow();
        var setBased = await ReadAsync(1);
        Assert.Equal(("set", later, "alice"), (setBased.Text, setBased.UpdatedAt, setBased.UpdatedBy));

        clock.Now = later.AddHours(1);
        (await bob.Repository<AuditedNote>().IncrementAsync(2L, n => n.Hits)).ValueOrThrow();
        var adjusted = await ReadAsync(2);
        Assert.Equal((1, later.AddHours(1), "bob"), (adjusted.Hits, adjusted.UpdatedAt, adjusted.UpdatedBy));
    }

    private sealed class Hooks : IDataInterceptor
    {
        public readonly List<string> Saved = [];

        public ValueTask<CodeLogic.Core.Results.Error?> OnSavingAsync(EntitySaveContext context)
        {
            if (context.Kind == SaveKind.Delete) return ValueTask.FromResult<CodeLogic.Core.Results.Error?>(DataErrors.Validation("notes cannot be deleted"));
            foreach (var note in context.Entities.OfType<AuditedNote>()) note.Text = note.Text.Trim();   // changes are saved
            return ValueTask.FromResult<CodeLogic.Core.Results.Error?>(null);
        }

        public ValueTask OnSavedAsync(EntitySaveContext context, CodeLogic.Core.Results.Error? error)
        {
            lock (Saved) Saved.Add($"{context.Kind}:{context.Entities.Count}:{(error is null ? "ok" : error.Code)}:{string.Join(",", context.ChangedProperties ?? [])}");
            return ValueTask.CompletedTask;
        }
    }

    [SkippableFact]
    public async Task Save_hooks_can_change_cancel_and_observe_saves()
    {
        RequireProvider();
        var hooks = new Hooks();
        var (alice, bob, _) = await SetupAsync(hooks);
        await using var __ = bob;
        await using var db = alice;
        var repo = db.Repository<AuditedNote>();

        var note = (await repo.InsertAsync(new AuditedNote { Id = 3, Text = "  padded  " })).ValueOrThrow();
        Assert.Equal("padded", (await ReadAsync(3)).Text);

        (await repo.UpdateAsync(note, n => n.Hits = 5)).ValueOrThrow();
        var refused = await repo.DeleteAsync(note);
        Assert.Equal(DataErrors.ValidationCode, refused.Error!.Code);
        Assert.NotNull(await ReadAsync(3));

        Assert.Equal(["Insert:1:ok:", "Update:1:ok:Hits"], hooks.Saved);   // a cancelled save does not reach OnSaved
    }
}
