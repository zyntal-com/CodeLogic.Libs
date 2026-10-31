using CL.Data;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.MySql;
using CL.Data.PostgreSql;
using CL.Data.Providers;
using CL.Data.Sql;
using CL.Data.SqlServer;
using CL.Data.Writes;
using Data.Tests.Mapping;
using Microsoft.Extensions.Time.Testing;

namespace Data.Tests.Writes;

public sealed class WritePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly IEntityAccessor<TestUser> Users = EntityAccessorRegistry.Get<TestUser>().ValueOrThrow();

    private static WriteContext Pg(int? tenant = 7, int limit = 65535, int batch = 1000) => new()
    {
        Dialect = new PostgreSqlSqlDialect(),
        Capabilities = new ProviderCapabilities { Returning = true, Schemas = true, ParameterLimit = limit },
        DefaultSchema = "public",
        Time = new FakeTimeProvider(Now),
        User = "alice",
        Tenant = tenant,
        MaxBatchSize = batch,
    };

    private static WriteContext MySql(int? tenant = 7) => new()
    {
        Dialect = new MySqlSqlDialect(),
        Capabilities = new ProviderCapabilities { Returning = false, ParameterLimit = 65535 },
        Time = new FakeTimeProvider(Now),
        Tenant = tenant,
    };

    private static TestUser NewUser(string email = "a@b.c") => new() { Email = email, Role = TestRole.User };

    private static string Sql(WriteCommand command, WriteContext ctx) => SqlWriter.Render(command.Statement, ctx.Dialect).Text;

    [Fact]
    public void Insert_stamps_audit_tenant_and_token_and_returns_the_identity()
    {
        var ctx = Pg();
        var user = NewUser();
        var command = Assert.Single(WritePlanner.Insert(Users, [user], ctx).ValueOrThrow());

        Assert.Equal(Now, user.CreatedAt);
        Assert.Equal(Now, user.UpdatedAt);
        Assert.Equal(7, user.TenantId);
        Assert.Equal(1, user.Version);

        var sql = Sql(command, ctx);
        Assert.StartsWith("INSERT INTO \"public\".\"users\" (\"Email\", \"DisplayName\", \"TenantId\", \"Version\", \"CreatedAt\"", sql);
        Assert.DoesNotContain("\"Id\",", sql);                                   // identity left to the server
        Assert.EndsWith("RETURNING \"Id\"", sql);
        Assert.Equal("Id", Assert.Single(command.ReturnedColumns).PropertyName);
        Assert.Equal(["table:users"], command.Tags);
    }

    [Fact]
    public void Without_returning_the_identity_comes_from_last_insert_id()
    {
        var ctx = MySql();
        var command = Assert.Single(WritePlanner.Insert(Users, [NewUser()], ctx).ValueOrThrow());
        Assert.False(command.Returns);
        Assert.Equal("Id", command.IdentityFromLastInsertId!.PropertyName);
        Assert.DoesNotContain("RETURNING", Sql(command, ctx));
    }

    [Fact]
    public void Explicit_identities_get_their_own_statement()
    {
        var ctx = Pg();
        var commands = WritePlanner.Insert(Users, [NewUser("1"), new TestUser { Id = 99, Email = "2" }], ctx).ValueOrThrow();
        Assert.Equal(2, commands.Count);
        Assert.Contains(commands, c => Sql(c, ctx).Contains("(\"Id\", \"Email\""));
        Assert.Contains(commands, c => c.Returns);
    }

    [Fact]
    public void Batches_respect_the_parameter_limit()
    {
        var ctx = Pg(limit: 40);                                                 // 12 columns per row → 3 rows per statement
        var users = Enumerable.Range(0, 7).Select(i => NewUser($"u{i}")).ToList();
        var commands = WritePlanner.Insert(Users, users, ctx).ValueOrThrow();
        Assert.Equal([3, 3, 1], commands.Select(c => c.Rows));
        Assert.Equal([0, 3, 6], commands.Select(c => c.FirstRow));
        Assert.All(commands, c => Assert.True(SqlWriter.Render(c.Statement, ctx.Dialect).Parameters.Count <= 40));
    }

    [Fact]
    public void Missing_tenant_is_an_error()
    {
        var result = WritePlanner.Insert(Users, [NewUser()], Pg(tenant: null));
        Assert.Equal(DataErrors.TenantMissingCode, result.Error!.Code);
    }

    [Fact]
    public void Update_checks_key_tenant_and_token_and_bumps_the_token_after_success()
    {
        var ctx = Pg();
        var user = new TestUser { Id = 5, Email = "x", TenantId = 7, Version = 3, CreatedAt = Now.AddDays(-1) };
        var command = WritePlanner.Update(Users, user, ctx).ValueOrThrow();

        var sql = Sql(command, ctx);
        Assert.StartsWith("UPDATE \"public\".\"users\" SET \"Email\" = @p0", sql);
        Assert.DoesNotContain("\"CreatedAt\" =", sql);
        Assert.DoesNotContain("\"TenantId\" =", sql.Split("WHERE")[0]);
        Assert.Contains("\"Version\" = @", sql.Split("WHERE")[0]);
        Assert.Matches("""WHERE "Id" = @p\d+ AND "TenantId" = @p\d+ AND "Version" = @p\d+$""", sql);
        Assert.Equal(RowCheck.ExactlyOne, command.Check);
        Assert.Equal(["table:users", "entity:users:5"], command.Tags);

        Assert.Equal(3, user.Version);
        command.OnSuccess!();
        Assert.Equal(4, user.Version);
        Assert.Equal(Now, user.UpdatedAt);
    }

    [Fact]
    public void Update_of_selected_properties_still_writes_audit_and_token()
    {
        var ctx = Pg();
        var user = new TestUser { Id = 5, TenantId = 7, Version = 1 };
        var sql = Sql(WritePlanner.Update(Users, user, ctx, [nameof(TestUser.DisplayName)]).ValueOrThrow(), ctx);
        Assert.Equal("UPDATE \"public\".\"users\" SET \"DisplayName\" = NULL, \"UpdatedAt\" = @p0, \"Version\" = @p1 " +
                     "WHERE \"Id\" = @p2 AND \"TenantId\" = @p3 AND \"Version\" = @p4", sql);
    }

    [Fact]
    public void Updating_another_tenants_row_is_refused()
    {
        var result = WritePlanner.Update(Users, new TestUser { Id = 1, TenantId = 8 }, Pg(tenant: 7));
        Assert.Equal(DataErrors.ValidationCode, result.Error!.Code);
    }

    [Fact]
    public void Delete_is_soft_by_default_and_hard_on_request()
    {
        var ctx = Pg();
        var user = new TestUser { Id = 5, TenantId = 7, Version = 2 };

        var soft = WritePlanner.Delete(Users, user, ctx).ValueOrThrow();
        Assert.Equal("UPDATE \"public\".\"users\" SET \"DeletedAt\" = @p0, \"UpdatedAt\" = @p1 WHERE \"Id\" = @p2 AND \"TenantId\" = @p3 AND \"Version\" = @p4 AND \"DeletedAt\" IS NULL",
            Sql(soft, ctx));
        soft.OnSuccess!();
        Assert.Equal(Now, user.DeletedAt);

        var hard = WritePlanner.Delete(Users, user, ctx, hard: true).ValueOrThrow();
        Assert.Equal("DELETE FROM \"public\".\"users\" WHERE \"Id\" = @p0 AND \"TenantId\" = @p1 AND \"Version\" = @p2", Sql(hard, ctx));

        var byKey = WritePlanner.DeleteByKey(Users.Map, EntityKey.Of(5), ctx, hard: true).ValueOrThrow();
        Assert.Equal(RowCheck.None, byKey.Check);
        Assert.Equal(DataErrors.ValidationCode, WritePlanner.DeleteByKey(Users.Map, EntityKey.Of(1, 2), ctx).Error!.Code);
    }

    [Fact]
    public void Upsert_overwrites_increments_and_ignores()
    {
        var ctx = Pg();
        var user = new TestUser { Id = 5, Email = "x" };
        var command = Assert.Single(WritePlanner.Upsert(Users, [user], ctx, new UpsertOptions { IncrementProperties = [nameof(TestUser.Score)] }).ValueOrThrow());
        var sql = Sql(command, ctx);
        Assert.Contains("ON CONFLICT (\"Id\") DO UPDATE SET ", sql);
        Assert.Contains("\"Score\" = \"users\".\"Score\" + EXCLUDED.\"Score\"", sql);
        Assert.Contains("\"Version\" = \"users\".\"Version\" + 1", sql);
        Assert.Contains("\"Email\" = EXCLUDED.\"Email\"", sql);
        Assert.DoesNotContain("\"CreatedAt\" = EXCLUDED", sql);
        Assert.DoesNotContain("\"TenantId\" = EXCLUDED", sql);

        var ignore = Assert.Single(WritePlanner.Upsert(Users, [user], ctx, new UpsertOptions { IgnoreOnConflict = true }).ValueOrThrow());
        Assert.EndsWith("DO NOTHING", Sql(ignore, ctx));

        // Generated identity: falls back to the unique composite index (TenantId, Email).
        var generated = Assert.Single(WritePlanner.Upsert(Users, [NewUser()], ctx).ValueOrThrow());
        Assert.Contains("ON CONFLICT (\"TenantId\", \"Email\")", Sql(generated, ctx));
    }

    [Fact]
    public void Sql_server_upserts_need_a_transaction_and_halve_the_batch()
    {
        var ctx = Pg(limit: 48) with { Dialect = new SqlServerSqlDialect(), Capabilities = new ProviderCapabilities { ParameterLimit = 48 } };
        var users = Enumerable.Range(0, 5).Select(i => new TestUser { Id = i + 1, Email = $"u{i}" }).ToList();
        var commands = WritePlanner.Upsert(Users, users, ctx).ValueOrThrow();
        Assert.All(commands, c => Assert.True(c.RequiresTransaction));
        Assert.Equal([2, 2, 1], commands.Select(c => c.Rows));                  // 12 columns, each bound twice: 48 / 24 = 2 rows
    }
}
