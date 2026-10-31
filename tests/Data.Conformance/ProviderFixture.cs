using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using Xunit;

namespace Data.Conformance;

/// <summary>
/// One provider under test. Each provider test project derives a fixture that supplies the provider, a
/// connection configuration (or a reason to skip) and the DDL for the conformance tables. The fixture creates
/// the tables once; tests call <see cref="ResetAsync"/> to start from empty tables.
/// </summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    private Database? _database;

    /// <summary>The provider.</summary>
    public abstract DataProvider Provider { get; }

    /// <summary>Why the suite is skipped (no connection string), or null when it runs.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The database under test.</summary>
    public Database Db => _database ?? throw new InvalidOperationException(SkipReason ?? "Fixture not initialized.");

    /// <summary>Options for the database (tests override to add interceptors, a fake clock, a tenant).</summary>
    protected virtual DatabaseOptions Options => new();

    /// <summary>The connection to test, or null with <see cref="SkipReason"/> set.</summary>
    protected abstract DataConnectionConfig? CreateConfig(out string? skipReason);

    /// <summary>Statements that create the conformance tables (dropping old ones first).</summary>
    protected abstract IEnumerable<string> SchemaSql();

    /// <summary>Tables to empty between tests, children first.</summary>
    protected virtual IReadOnlyList<string> Tables => [ConformanceTables.Tags, ConformanceTables.Products, ConformanceTables.Notes];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        DataProviderRegistry.Register(Provider);
        var config = CreateConfig(out var reason);
        if (config is null)
        {
            SkipReason = reason ?? "Provider not configured.";
            return;
        }

        _database = Database.Create("conformance", Provider, config, Options).ValueOrThrow();
        foreach (var statement in SchemaSql())
            (await _database.ExecuteSqlAsync(SqlText.Raw(statement))).ValueOrThrow();
    }

    /// <summary>Deletes all rows from the conformance tables.</summary>
    public async Task ResetAsync()
    {
        foreach (var table in Tables)
            (await Db.ExecuteSqlAsync($"DELETE FROM {Sql.Identifier(table)}")).ValueOrThrow();
    }

    /// <summary>Creates another database on the same connection with different options (tenant, clock, user).</summary>
    public Database CreateDatabase(DatabaseOptions options) =>
        Database.Create("conformance-" + Guid.NewGuid().ToString("N")[..8], Provider, Db.Config, options).ValueOrThrow();

    /// <inheritdoc />
    public virtual async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }
}

/// <summary>Base of every conformance suite: gives tests the fixture and skips when the provider is not configured.</summary>
public abstract class ConformanceSuite(ProviderFixture fixture) : IAsyncLifetime
{
    /// <summary>The provider fixture.</summary>
    protected ProviderFixture Fixture { get; } = fixture;

    /// <summary>The database under test.</summary>
    protected Database Db => Fixture.Db;

    /// <summary>Skips the current test when the provider is not configured.</summary>
    protected void RequireProvider() => Skip.If(Fixture.SkipReason is not null, Fixture.SkipReason);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (Fixture.SkipReason is null) await Fixture.ResetAsync();
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
