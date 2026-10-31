using CL.Data.Configuration;
using CL.Data.Providers;
using CL.Data.Sqlite;
using Data.Conformance;

namespace Data.Sqlite.Tests;

/// <summary>SQLite in a temporary file: always available, so the conformance suite runs in every build.</summary>
public sealed class SqliteFixture : ProviderFixture
{
    private string? _file;

    public override DataProvider Provider => SqliteDataProvider.Instance;

    protected override DataConnectionConfig? CreateConfig(out string? skipReason)
    {
        skipReason = null;
        _file = Path.Combine(Path.GetTempPath(), $"cl-data-conf-{Guid.NewGuid():N}.db");
        return new DataConnectionConfig { Provider = "sqlite", Database = _file };
    }

    protected override IEnumerable<string> SchemaSql() =>
    [
        """
        CREATE TABLE conf_products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Sku TEXT NOT NULL UNIQUE,
            Name TEXT NOT NULL,
            Description TEXT NULL,
            Price NUMERIC NOT NULL,
            Stock INTEGER NOT NULL,
            Active INTEGER NOT NULL,
            Status TEXT NOT NULL,
            Ref BLOB NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            Version INTEGER NOT NULL,
            DeletedAt TEXT NULL)
        """,
        "CREATE TABLE conf_tags (ProductId INTEGER NOT NULL, Tag TEXT NOT NULL, Weight INTEGER NOT NULL, PRIMARY KEY (ProductId, Tag))",
        "CREATE TABLE conf_notes (Id BLOB PRIMARY KEY, TenantId INTEGER NOT NULL, Text TEXT NOT NULL)",
    ];

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _file, _file + "-wal", _file + "-shm" })
            if (f is not null && File.Exists(f)) File.Delete(f);
    }
}

[CollectionDefinition("sqlite")]
public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>;

[Collection("sqlite")]
public sealed class SqliteRepositoryTests(SqliteFixture fixture) : RepositoryConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteUpsertTests(SqliteFixture fixture) : UpsertConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteQueryTests(SqliteFixture fixture) : QueryConformance(fixture);

[Collection("sqlite")]
public sealed class SqlitePagingTests(SqliteFixture fixture) : PagingConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteProjectionTests(SqliteFixture fixture) : ProjectionConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteJoinTests(SqliteFixture fixture) : JoinConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteGroupingTests(SqliteFixture fixture) : GroupingConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteTransactionTests(SqliteFixture fixture) : TransactionConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteBatchTests(SqliteFixture fixture) : BatchConformance(fixture);

[Collection("sqlite")]
public sealed class SqliteSchemaTests(SqliteFixture fixture) : SchemaConformance(fixture);
[Collection("sqlite")]
public sealed class SqliteSyncTests(SqliteFixture fixture) : SyncConformance(fixture);
[Collection("sqlite")]
public sealed class SqliteMigrationTests(SqliteFixture fixture) : MigrationConformance(fixture);
