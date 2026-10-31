using CL.Data;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Providers;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

[DataRow]
public sealed record Person(long Id, string Name, decimal Balance, DateTimeOffset JoinedAt, Guid Ref, bool Active);

public sealed class RawSqlTests : IAsyncLifetime
{
    private Database _db = null!;
    private string? _file;

    public async Task InitializeAsync()
    {
        DataProviderRegistry.Register(SqliteDataProvider.Instance);
        _file = Path.Combine(Path.GetTempPath(), $"cl-data-{Guid.NewGuid():N}.db");
        _db = Database.Create("test", new DataConnectionConfig { Provider = "sqlite", Database = _file }).ValueOrThrow();
        (await _db.ExecuteSqlAsync($"""
            CREATE TABLE people (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE, Balance NUMERIC, JoinedAt TEXT, Ref BLOB, Active INTEGER)
            """)).ValueOrThrow();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _file, _file + "-wal", _file + "-shm" })
            if (f is not null && File.Exists(f)) File.Delete(f);
    }

    private async Task<Person> InsertAsync(long id, string name, decimal balance)
    {
        var person = new Person(id, name, balance, new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)), Guid.NewGuid(), true);
        var rows = await _db.ExecuteSqlAsync(
            $"INSERT INTO people (Id, Name, Balance, JoinedAt, Ref, Active) VALUES ({person.Id}, {person.Name}, {person.Balance}, {person.JoinedAt}, {person.Ref}, {person.Active})");
        Assert.Equal(1, rows.ValueOrThrow());
        return person;
    }

    [Fact]
    public async Task Round_trips_typed_values_through_parameters()
    {
        var inserted = await InsertAsync(1, "Ann", 10.25m);
        var people = (await _db.SqlQueryAsync<Person>($"SELECT * FROM people WHERE Id = {1L}")).ValueOrThrow();

        var person = Assert.Single(people);
        Assert.Equal(inserted with { JoinedAt = inserted.JoinedAt.ToUniversalTime() }, person with { JoinedAt = person.JoinedAt });
        Assert.Equal(inserted.JoinedAt, person.JoinedAt);          // same instant, stored as UTC
        Assert.Equal(TimeSpan.Zero, person.JoinedAt.Offset);
    }

    [Fact]
    public async Task Scalars_and_identifiers()
    {
        await InsertAsync(1, "Ann", 1m);
        await InsertAsync(2, "Bob", 2m);

        Assert.Equal(2L, (await _db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM {Sql.Identifier("people")}")).ValueOrThrow());
        var names = (await _db.SqlQueryAsync<string>($"SELECT Name FROM people ORDER BY Name DESC")).ValueOrThrow();
        Assert.Equal(["Bob", "Ann"], names);
        Assert.Null((await _db.SqlScalarAsync<string>($"SELECT Name FROM people WHERE Id = {99}")).ValueOrThrow());
    }

    [Fact]
    public async Task Decimals_sort_numerically()
    {
        await InsertAsync(1, "a", 9m);
        await InsertAsync(2, "b", 10m);
        await InsertAsync(3, "c", 100.5m);
        var balances = (await _db.SqlQueryAsync<decimal>($"SELECT Balance FROM people ORDER BY Balance")).ValueOrThrow();
        Assert.Equal([9m, 10m, 100.5m], balances);
    }

    [Fact]
    public async Task Injection_attempt_stays_a_value()
    {
        await InsertAsync(1, "Ann", 1m);
        var evil = "x' OR '1'='1";
        var rows = (await _db.SqlQueryAsync<Person>($"SELECT * FROM people WHERE Name = {evil}")).ValueOrThrow();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Duplicate_key_maps_to_conflict()
    {
        await InsertAsync(1, "Ann", 1m);
        var result = await _db.ExecuteSqlAsync($"INSERT INTO people (Id, Name) VALUES ({2}, {"Ann"})");
        Assert.True(result.IsFailure);
        Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(DataErrors.DuplicateKeyCode, result.Error!.Code);
        Assert.DoesNotContain("Ann", result.Error.Message);        // values never in the message
    }

    [Fact]
    public async Task Syntax_error_maps_to_provider_error()
    {
        var result = await _db.ExecuteSqlAsync($"SELEC nonsense");
        Assert.Equal(DataErrors.ProviderErrorCode, result.Error!.Code);
    }

    [Fact]
    public async Task Cancellation_maps_to_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await _db.SqlQueryAsync<long>($"SELECT 1", cts.Token);
        Assert.Equal(DataErrors.CancelledCode, result.Error!.Code);
    }

    [Fact]
    public async Task Ping_succeeds()
    {
        Assert.True((await _db.PingAsync()).IsSuccess);
    }

    [Fact]
    public async Task Memory_database_survives_across_pooled_connections()
    {
        await using var memory = Database.Create("mem", new DataConnectionConfig { Provider = "sqlite", Database = ":memory:" }).ValueOrThrow();
        (await memory.ExecuteSqlAsync($"CREATE TABLE t (x INTEGER)")).ValueOrThrow();
        (await memory.ExecuteSqlAsync($"INSERT INTO t VALUES ({42})")).ValueOrThrow();
        Assert.Equal(42L, (await memory.SqlScalarAsync<long>($"SELECT x FROM t")).ValueOrThrow());
    }

    [Fact]
    public void Unknown_provider_and_invalid_config_fail_cleanly()
    {
        var missing = Database.Create("x", new DataConnectionConfig { Provider = "nope", Database = "x" });
        Assert.Equal(DataErrors.ProviderMissingCode, missing.Error!.Code);

        var invalid = Database.Create("x", new DataConnectionConfig { Provider = "sqlite" });
        Assert.Equal(DataErrors.ValidationCode, invalid.Error!.Code);
        Assert.Contains("x.database", invalid.Error.Details);
    }

    [Fact]
    public void Sql_text_never_contains_values()
    {
        var secret = "hunter2";
        SqlText text = $"SELECT * FROM t WHERE a = {secret} AND b = {5} AND c = {Sql.Identifier("col")}";
        var rendered = text.Render(SqliteDataProvider.Instance.Dialect);
        Assert.Equal("SELECT * FROM t WHERE a = @p0 AND b = @p1 AND c = \"col\"", rendered);
        Assert.Equal(2, text.Parameters.Count);
        Assert.DoesNotContain(secret, rendered);
    }
}
