using CL.Data.Configuration;
using CL.Data.MySql;
using CL.Data.Providers;
using Data.Conformance;

namespace Data.MySql.Tests;

/// <summary>MySQL 8 from <c>CL_DATA_MYSQL_CS</c>; the suite skips when it is not set.</summary>
public sealed class MySqlFixture : ProviderFixture
{
    public override DataProvider Provider => MySqlDataProvider.Instance;

    protected override DataConnectionConfig? CreateConfig(out string? skipReason)
    {
        var cs = Environment.GetEnvironmentVariable("CL_DATA_MYSQL_CS");
        skipReason = string.IsNullOrWhiteSpace(cs) ? "CL_DATA_MYSQL_CS is not set." : null;
        return skipReason is null ? new DataConnectionConfig { Provider = "mysql", ConnectionString = cs, MySql = new() { AllowLoadLocalInfile = true } } : null;
    }

    protected override IEnumerable<string> SchemaSql() =>
    [
        "DROP TABLE IF EXISTS conf_tags",
        "DROP TABLE IF EXISTS conf_notes",
        "DROP TABLE IF EXISTS conf_products",
        """
        CREATE TABLE conf_products (
            Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
            Sku VARCHAR(64) NOT NULL UNIQUE,
            Name VARCHAR(200) NOT NULL,
            Description LONGTEXT NULL,
            Price DECIMAL(18,2) NOT NULL,
            Stock INT NOT NULL,
            Active TINYINT(1) NOT NULL,
            Status VARCHAR(16) NOT NULL,
            Ref BINARY(16) NOT NULL,
            CreatedAt DATETIME(6) NOT NULL,
            UpdatedAt DATETIME(6) NOT NULL,
            Version INT NOT NULL,
            DeletedAt DATETIME(6) NULL
        ) ENGINE=InnoDB
        """,
        "CREATE TABLE conf_tags (ProductId BIGINT NOT NULL, Tag VARCHAR(32) NOT NULL, Weight INT NOT NULL, PRIMARY KEY (ProductId, Tag)) ENGINE=InnoDB",
        "CREATE TABLE conf_notes (Id BINARY(16) NOT NULL PRIMARY KEY, TenantId INT NOT NULL, Text VARCHAR(500) NOT NULL) ENGINE=InnoDB",
    ];
}

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;

[Collection("mysql")] public sealed class MySqlRepositoryTests(MySqlFixture f) : RepositoryConformance(f);
[Collection("mysql")] public sealed class MySqlUpsertTests(MySqlFixture f) : UpsertConformance(f);
[Collection("mysql")] public sealed class MySqlQueryTests(MySqlFixture f) : QueryConformance(f);
[Collection("mysql")] public sealed class MySqlPagingTests(MySqlFixture f) : PagingConformance(f);
[Collection("mysql")] public sealed class MySqlProjectionTests(MySqlFixture f) : ProjectionConformance(f);
[Collection("mysql")] public sealed class MySqlJoinTests(MySqlFixture f) : JoinConformance(f);
[Collection("mysql")] public sealed class MySqlGroupingTests(MySqlFixture f) : GroupingConformance(f);
[Collection("mysql")] public sealed class MySqlTransactionTests(MySqlFixture f) : TransactionConformance(f);
[Collection("mysql")] public sealed class MySqlBatchTests(MySqlFixture f) : BatchConformance(f);

[Collection("mysql")] public sealed class MySqlSchemaTests(MySqlFixture f) : SchemaConformance(f);
[Collection("mysql")] public sealed class MySqlSyncTests(MySqlFixture f) : SyncConformance(f);
[Collection("mysql")] public sealed class MySqlMigrationTests(MySqlFixture f) : MigrationConformance(f);
[Collection("mysql")] public sealed class MySqlAdminTests(MySqlFixture f) : AdminConformance(f);
[Collection("mysql")] public sealed class MySqlBackupTests(MySqlFixture f) : BackupConformance(f);
[Collection("mysql")] public sealed class MySqlChangedUpdateTests(MySqlFixture f) : ChangedUpdateConformance(f);
[Collection("mysql")] public sealed class MySqlCompiledQueryTests(MySqlFixture f) : CompiledQueryConformance(f);
[Collection("mysql")] public sealed class MySqlAuditTests(MySqlFixture f) : AuditConformance(f);
[Collection("mysql")] public sealed class MySqlFilterTests(MySqlFixture f) : FilterConformance(f);
[Collection("mysql")] public sealed class MySqlSeedTests(MySqlFixture f) : SeedConformance(f);
[Collection("mysql")] public sealed class MySqlCacheTests(MySqlFixture f) : CacheConformance(f);
[Collection("mysql")] public sealed class MySqlReplicaTests(MySqlFixture f) : ReplicaConformance(f);
[Collection("mysql")] public sealed class MySqlTelemetryTests(MySqlFixture f) : TelemetryConformance(f);
[Collection("mysql")] public sealed class MySqlBulkTests(MySqlFixture f) : BulkConformance(f);
[Collection("mysql")] public sealed class MySqlServerTests(MySqlFixture f) : ServerConformance(f);
