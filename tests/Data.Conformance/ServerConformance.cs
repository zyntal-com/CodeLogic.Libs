using System.Data.Common;
using CodeLogic.Core.Results;

namespace Data.Conformance;

/// <summary>Server version and the raw-connection escape hatch.</summary>
public abstract class ServerConformance(ProviderFixture fixture) : ConformanceSuite(fixture)
{
    [SkippableFact]
    public async Task Server_version_is_reported()
    {
        RequireProvider();
        var version = (await Db.Admin.ServerVersionAsync()).ValueOrThrow();
        Assert.Matches(@"^\d+\.\d+", version);
    }

    [SkippableFact]
    public async Task Raw_connections_join_transactions_and_map_errors()
    {
        RequireProvider();
        static async ValueTask<long> CountAsync(DbConnection connection, DbTransaction? transaction, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM conf_products";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(0L, (await Db.WithConnectionAsync(CountAsync)).ValueOrThrow());
        var tx = (await Db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx)
        {
            (await tx.Repository<Product>().InsertAsync(new Product { Sku = "RAW-1", Name = "raw" })).ValueOrThrow();
            Assert.Equal(1L, (await tx.Db.WithConnectionAsync(CountAsync)).ValueOrThrow());   // sees the transaction's row
        }

        var failed = await Db.WithConnectionAsync<int>(static async (c, _, ct) =>
        {
            await using var command = c.CreateCommand();
            command.CommandText = "SELECT * FROM no_such_table_here";
            return await command.ExecuteNonQueryAsync(ct);
        });
        Assert.True(failed.IsFailure);
        Assert.StartsWith("data.", failed.Error!.Code);
    }
}
