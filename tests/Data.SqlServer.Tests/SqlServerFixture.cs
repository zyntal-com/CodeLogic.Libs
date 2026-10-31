using CL.Data.Configuration;
using CL.Data.Providers;
using CL.Data.SqlServer;
using Data.Conformance;

namespace Data.SqlServer.Tests;

/// <summary>SQL Server from <c>CL_DATA_MSSQL_CS</c>; the suite skips when it is not set.</summary>
public sealed class SqlServerFixture : ProviderFixture
{
    public override DataProvider Provider => SqlServerDataProvider.Instance;

    protected override DataConnectionConfig? CreateConfig(out string? skipReason)
    {
        var cs = Environment.GetEnvironmentVariable("CL_DATA_MSSQL_CS");
        skipReason = string.IsNullOrWhiteSpace(cs) ? "CL_DATA_MSSQL_CS is not set." : null;
        return skipReason is null
            ? new DataConnectionConfig { Provider = "sqlserver", ConnectionString = cs, SqlServer = new() { TrustServerCertificate = true, Encrypt = false } }
            : null;
    }

    protected override IEnumerable<string> SchemaSql() =>
    [
        // Readers see the last committed version instead of blocking on writers (recommended for CL.Data on SQL Server).
        "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE",
        "DROP TABLE IF EXISTS dbo.conf_tags",
        "DROP TABLE IF EXISTS dbo.conf_notes",
        "DROP TABLE IF EXISTS dbo.conf_products",
        """
        CREATE TABLE dbo.conf_products (
            [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [Sku] nvarchar(64) NOT NULL UNIQUE,
            [Name] nvarchar(200) NOT NULL,
            [Description] nvarchar(max) NULL,
            [Price] decimal(18,2) NOT NULL,
            [Stock] int NOT NULL,
            [Active] bit NOT NULL,
            [Status] nvarchar(16) NOT NULL,
            [Ref] uniqueidentifier NOT NULL,
            [CreatedAt] datetimeoffset(7) NOT NULL,
            [UpdatedAt] datetimeoffset(7) NOT NULL,
            [Version] int NOT NULL,
            [DeletedAt] datetimeoffset(7) NULL)
        """,
        "CREATE TABLE dbo.conf_tags ([ProductId] bigint NOT NULL, [Tag] nvarchar(32) NOT NULL, [Weight] int NOT NULL, PRIMARY KEY ([ProductId], [Tag]))",
        "CREATE TABLE dbo.conf_notes ([Id] uniqueidentifier NOT NULL PRIMARY KEY, [TenantId] int NOT NULL, [Text] nvarchar(500) NOT NULL)",
    ];
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

[Collection("sqlserver")] public sealed class SqlServerRepositoryTests(SqlServerFixture f) : RepositoryConformance(f);
[Collection("sqlserver")] public sealed class SqlServerUpsertTests(SqlServerFixture f) : UpsertConformance(f);
[Collection("sqlserver")] public sealed class SqlServerQueryTests(SqlServerFixture f) : QueryConformance(f);
[Collection("sqlserver")] public sealed class SqlServerPagingTests(SqlServerFixture f) : PagingConformance(f);
[Collection("sqlserver")] public sealed class SqlServerProjectionTests(SqlServerFixture f) : ProjectionConformance(f);
[Collection("sqlserver")] public sealed class SqlServerJoinTests(SqlServerFixture f) : JoinConformance(f);
[Collection("sqlserver")] public sealed class SqlServerGroupingTests(SqlServerFixture f) : GroupingConformance(f);
[Collection("sqlserver")] public sealed class SqlServerTransactionTests(SqlServerFixture f) : TransactionConformance(f);
[Collection("sqlserver")] public sealed class SqlServerBatchTests(SqlServerFixture f) : BatchConformance(f);

[Collection("sqlserver")] public sealed class SqlServerSchemaTests(SqlServerFixture f) : SchemaConformance(f);
[Collection("sqlserver")] public sealed class SqlServerSyncTests(SqlServerFixture f) : SyncConformance(f);
[Collection("sqlserver")] public sealed class SqlServerMigrationTests(SqlServerFixture f) : MigrationConformance(f);
[Collection("sqlserver")] public sealed class SqlServerAdminTests(SqlServerFixture f) : AdminConformance(f);
[Collection("sqlserver")] public sealed class SqlServerBackupTests(SqlServerFixture f) : BackupConformance(f);
[Collection("sqlserver")] public sealed class SqlServerChangedUpdateTests(SqlServerFixture f) : ChangedUpdateConformance(f);
[Collection("sqlserver")] public sealed class SqlServerCompiledQueryTests(SqlServerFixture f) : CompiledQueryConformance(f);
[Collection("sqlserver")] public sealed class SqlServerAuditTests(SqlServerFixture f) : AuditConformance(f);
[Collection("sqlserver")] public sealed class SqlServerFilterTests(SqlServerFixture f) : FilterConformance(f);
[Collection("sqlserver")] public sealed class SqlServerSeedTests(SqlServerFixture f) : SeedConformance(f);
[Collection("sqlserver")] public sealed class SqlServerCacheTests(SqlServerFixture f) : CacheConformance(f);
[Collection("sqlserver")] public sealed class SqlServerReplicaTests(SqlServerFixture f) : ReplicaConformance(f);
[Collection("sqlserver")] public sealed class SqlServerTelemetryTests(SqlServerFixture f) : TelemetryConformance(f);
[Collection("sqlserver")] public sealed class SqlServerBulkTests(SqlServerFixture f) : BulkConformance(f);
