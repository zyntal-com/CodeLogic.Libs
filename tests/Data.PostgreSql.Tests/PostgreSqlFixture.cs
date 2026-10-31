using CL.Data.Configuration;
using CL.Data.PostgreSql;
using CL.Data.Providers;
using Data.Conformance;

namespace Data.PostgreSql.Tests;

/// <summary>PostgreSQL from <c>CL_DATA_PG_CS</c>; the suite skips when it is not set.</summary>
public sealed class PostgreSqlFixture : ProviderFixture
{
    public override DataProvider Provider => PostgreSqlDataProvider.Instance;

    protected override DataConnectionConfig? CreateConfig(out string? skipReason)
    {
        var cs = Environment.GetEnvironmentVariable("CL_DATA_PG_CS");
        skipReason = string.IsNullOrWhiteSpace(cs) ? "CL_DATA_PG_CS is not set." : null;
        return skipReason is null ? new DataConnectionConfig { Provider = "postgresql", ConnectionString = cs } : null;
    }

    protected override IEnumerable<string> SchemaSql() =>
    [
        "DROP TABLE IF EXISTS conf_tags",
        "DROP TABLE IF EXISTS conf_notes",
        "DROP TABLE IF EXISTS conf_products",
        """
        CREATE TABLE conf_products (
            "Id" bigserial PRIMARY KEY,
            "Sku" varchar(64) NOT NULL UNIQUE,
            "Name" varchar(200) NOT NULL,
            "Description" text NULL,
            "Price" numeric(18,2) NOT NULL,
            "Stock" integer NOT NULL,
            "Active" boolean NOT NULL,
            "Status" varchar(16) NOT NULL,
            "Ref" uuid NOT NULL,
            "CreatedAt" timestamptz NOT NULL,
            "UpdatedAt" timestamptz NOT NULL,
            "Version" integer NOT NULL,
            "DeletedAt" timestamptz NULL)
        """,
        """CREATE TABLE conf_tags ("ProductId" bigint NOT NULL, "Tag" varchar(32) NOT NULL, "Weight" integer NOT NULL, PRIMARY KEY ("ProductId", "Tag"))""",
        """CREATE TABLE conf_notes ("Id" uuid PRIMARY KEY, "TenantId" integer NOT NULL, "Text" varchar(500) NOT NULL)""",
    ];
}

[CollectionDefinition("postgresql")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;

[Collection("postgresql")] public sealed class PostgreSqlRepositoryTests(PostgreSqlFixture f) : RepositoryConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlUpsertTests(PostgreSqlFixture f) : UpsertConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlQueryTests(PostgreSqlFixture f) : QueryConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlPagingTests(PostgreSqlFixture f) : PagingConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlProjectionTests(PostgreSqlFixture f) : ProjectionConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlJoinTests(PostgreSqlFixture f) : JoinConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlGroupingTests(PostgreSqlFixture f) : GroupingConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlTransactionTests(PostgreSqlFixture f) : TransactionConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlBatchTests(PostgreSqlFixture f) : BatchConformance(f);

[Collection("postgresql")] public sealed class PostgreSqlSchemaTests(PostgreSqlFixture f) : SchemaConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlSyncTests(PostgreSqlFixture f) : SyncConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlMigrationTests(PostgreSqlFixture f) : MigrationConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlAdminTests(PostgreSqlFixture f) : AdminConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlBackupTests(PostgreSqlFixture f) : BackupConformance(f);
[Collection("postgresql")] public sealed class PostgreSqlChangedUpdateTests(PostgreSqlFixture f) : ChangedUpdateConformance(f);
