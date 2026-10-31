using CL.Data;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

namespace Data.Tests.Mapping;

/// <summary>A positional record: properties are init-only and set through the primary constructor.</summary>
[Table(Name = "gen_records")]
public sealed record GenRecord([property: Column(Primary = true)] long Id, [property: Column(Size = 40)] string Name, int? Score);

/// <summary>Required and init-only members, and an initializer that must survive when its column is not selected.</summary>
[Table(Name = "gen_required")]
public sealed class GenRequired
{
    [Column(Primary = true)] public required long Id { get; init; }
    [Column(Size = 40, Comment = "quote \" backslash \\ tab \t")] public required string Name { get; set; }
    [Column(Size = 40)] public string Note { get; init; } = "default-note";
    public DateTimeOffset At { get; set; }
}

/// <summary>Columns declared on a base class.</summary>
public abstract class GenBase
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>A derived entity with an ignored computed property.</summary>
[Table(Name = "gen_derived")]
public sealed class GenDerived : GenBase
{
    [Column(Size = 40)] public string Title { get; set; } = "";
    [Ignore] public string Shout => Title + "!";
}

public sealed class GeneratedShapesTests : IAsyncLifetime
{
    private Database _db = null!;

    public async Task InitializeAsync()
    {
        _db = Database.Create("gen", SqliteDataProvider.Instance, new DataConnectionConfig { Provider = "sqlite", Database = ":memory:" }).ValueOrThrow();
        (await _db.Schema.SyncAsync<GenRecord>()).ValueOrThrow();
        (await _db.Schema.SyncAsync<GenRequired>()).ValueOrThrow();
        (await _db.Schema.SyncAsync<GenDerived>()).ValueOrThrow();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public void All_shapes_have_generated_accessors()
    {
        Assert.True(EntityAccessorRegistry.HasGenerated<GenRecord>());
        Assert.True(EntityAccessorRegistry.HasGenerated<GenRequired>());
        Assert.True(EntityAccessorRegistry.HasGenerated<GenDerived>());
        var map = EntityAccessorRegistry.Get<GenRequired>().ValueOrThrow().Map;
        Assert.Equal("quote \" backslash \\ tab \t", map.FindProperty(nameof(GenRequired.Name))!.Comment);
        Assert.Null(EntityAccessorRegistry.Get<GenDerived>().ValueOrThrow().Map.FindProperty(nameof(GenDerived.Shout)));
    }

    [Fact]
    public async Task Records_round_trip_through_their_constructor()
    {
        var repo = _db.Repository<GenRecord>();
        (await repo.InsertAsync(new GenRecord(1, "one", null))).ValueOrThrow();
        (await repo.InsertAsync(new GenRecord(2, "two", 20))).ValueOrThrow();
        var rows = (await _db.Query<GenRecord>().OrderBy(r => r.Id).ToListAsync()).ValueOrThrow();
        Assert.Equal([new GenRecord(1, "one", null), new GenRecord(2, "two", 20)], rows);
    }

    [Fact]
    public async Task Required_and_init_members_are_set_and_unselected_columns_keep_initializers()
    {
        var at = new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero);
        (await _db.Repository<GenRequired>().InsertAsync(new GenRequired { Id = 7, Name = "seven", Note = "stored", At = at })).ValueOrThrow();

        var full = (await _db.Query<GenRequired>().FirstOrDefaultAsync(r => r.Id == 7)).ValueOrThrow()!;
        Assert.Equal(("seven", "stored", at), (full.Name, full.Note, full.At));

        var partial = Assert.Single((await _db.SqlQueryAsync<GenRequired>($"SELECT {CL.Data.Sql.Sql.Identifier("Id")}, {CL.Data.Sql.Sql.Identifier("Name")} FROM {CL.Data.Sql.Sql.Identifier("gen_required")}")).ValueOrThrow());
        Assert.Equal((7L, "seven", "default-note"), (partial.Id, partial.Name, partial.Note));
    }

    [Fact]
    public async Task Inherited_columns_and_identity_work()
    {
        var entity = new GenDerived { Title = "hello", CreatedUtc = DateTimeOffset.UnixEpoch };
        (await _db.Repository<GenDerived>().InsertAsync(entity)).ValueOrThrow();
        Assert.True(entity.Id > 0);   // identity written back through the generated SetValue
        var read = (await _db.Query<GenDerived>().FirstOrDefaultAsync(d => d.Id == entity.Id)).ValueOrThrow()!;
        Assert.Equal(("hello", "hello!"), (read.Title, read.Shout));
        Assert.Equal(EntityKey.Of(entity.Id), EntityAccessorRegistry.Get<GenDerived>().ValueOrThrow().GetKey(read));
    }
}
