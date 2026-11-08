using System.Diagnostics.Metrics;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.MySql;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using Data.Conformance;
using MySqlConnector;

namespace Data.MySql.Tests;

/// <summary>
/// Session state never leaks between pooled uses of a connection, whichever <see cref="MySqlConnectionReset"/> mode resets it,
/// and <see cref="MySqlConnectionReset.WhenDirty"/> resets only when needed. Every database here has a one-connection pool,
/// so each lease reuses the same server session.
/// </summary>
[Collection("mysql")]
public sealed class MySqlSessionHygieneTests(MySqlFixture fixture)
{
    private Database Open(MySqlConnectionReset mode = MySqlConnectionReset.WhenDirty, bool prepare = true)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var config = new DataConnectionConfig
        {
            Provider = "mysql", ConnectionString = fixture.Db.Config.ConnectionString, MinPoolSize = 0, MaxPoolSize = 1,
            PrepareStatements = prepare, MySql = new MySqlOptions { ConnectionReset = mode, AllowLoadLocalInfile = true },
        };
        return Database.Create("hygiene-" + Guid.NewGuid().ToString("N")[..8], MySqlDataProvider.Instance, config).ValueOrThrow();
    }

    private static async Task<T?> Scalar<T>(Database db, string sql) => (await db.SqlScalarAsync<T>(SqlText.Raw(sql))).ValueOrThrow();

    private static async Task Exec(Database db, string sql) => (await db.ExecuteSqlAsync(SqlText.Raw(sql))).ValueOrThrow();

    /// <summary>A separate unpooled connection, to look at the server from outside the pool.</summary>
    private async Task<T?> Outside<T>(string sql)
    {
        var builder = new MySqlConnectionStringBuilder(fixture.Db.Config.ConnectionString!) { Pooling = false };
        await using var connection = new MySqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull) return default;
        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Counts CL.Data's own session resets for one database.</summary>
    private sealed class ResetCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public ResetCounter(Database db)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "cl.data.session.resets") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "db.namespace" && Equals(tag.Value, db.Name)) Interlocked.Add(ref _count, value);
            });
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    [SkippableFact]
    public async Task Generated_queries_and_writes_reuse_the_session_without_a_reset()
    {
        await using var db = Open();
        using var resets = new ResetCounter(db);
        var session = await Scalar<long>(db, "SELECT CONNECTION_ID()");
        var repo = db.Repository<Product>();
        var product = (await repo.InsertAsync(new Product { Sku = "hyg-" + Guid.NewGuid().ToString("N")[..10], Name = "h", Price = 1, Stock = 1 })).ValueOrThrow();
        Assert.NotNull((await repo.GetByIdAsync(product.Id)).ValueOrThrow());
        Assert.Equal(1, (await db.SqlScalarAsync<int>($"SELECT COUNT(*) FROM conf_products WHERE Id = {product.Id}")).ValueOrThrow());
        (await repo.DeleteAsync(product)).ValueOrThrow();
        Assert.Equal(session, await Scalar<long>(db, "SELECT CONNECTION_ID()"));   // same pooled session throughout
        Assert.Equal(0, resets.Count);
    }

    [SkippableTheory]
    [InlineData(MySqlConnectionReset.WhenDirty)]
    [InlineData(MySqlConnectionReset.Always)]
    public async Task Raw_session_changes_do_not_reach_the_next_user(MySqlConnectionReset mode)
    {
        await using var db = Open(mode);
        var globalMode = await Scalar<string>(db, "SELECT @@GLOBAL.sql_mode");

        await Exec(db, "SET @leak = 42");
        Assert.Null(await Scalar<long?>(db, "SELECT @leak"));

        await Exec(db, "SET SESSION sql_mode = 'ANSI_QUOTES'");
        Assert.Equal(globalMode, await Scalar<string>(db, "SELECT @@SESSION.sql_mode"));

        await Exec(db, "SET autocommit = 0");
        Assert.Equal(1, await Scalar<int>(db, "SELECT @@autocommit"));

        await Exec(db, "CREATE TEMPORARY TABLE hyg_tmp (id INT)");
        Assert.True((await db.SqlScalarAsync<int>(SqlText.Raw("SELECT COUNT(*) FROM hyg_tmp"))).IsFailure);

        await Exec(db, "USE information_schema");
        Assert.Equal(new MySqlConnectionStringBuilder(db.Config.ConnectionString!).Database, await Scalar<string>(db, "SELECT DATABASE()"));

        (await db.WithConnectionAsync(async (connection, _, ct) =>
        {
            await using var command = new MySqlCommand("SET NAMES latin1", (MySqlConnection)connection);
            return await command.ExecuteNonQueryAsync(ct);
        })).ValueOrThrow();
        Assert.Equal("utf8mb4", await Scalar<string>(db, "SELECT @@character_set_client"));
    }

    [SkippableFact]
    public async Task Never_leaks_session_state_as_documented()
    {
        await using var db = Open(MySqlConnectionReset.Never);
        await Exec(db, "SET @leak = 42");
        Assert.Equal(42, await Scalar<long>(db, "SELECT @leak"));
    }

    [SkippableFact]
    public async Task A_raw_lock_is_released_before_the_connection_idles_in_the_pool()
    {
        await using var db = Open();
        var name = "hyg_" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(1, await Scalar<int>(db, $"SELECT GET_LOCK('{name}', 0)"));
        Assert.Null(await Outside<long?>($"SELECT IS_USED_LOCK('{name}')"));
    }

    [SkippableTheory]
    [InlineData("SELECT GET_LOCK('{0}', 0)")]
    [InlineData("SELECT `GET_LOCK`('{0}', 0)")]
    [InlineData("SELECT `get_lock` ('{0}', 0)")]
    [InlineData("SELECT 1--1 = 2 AND GET_LOCK('{0}', 0)")]                                // --1 is arithmetic, not a comment
    public async Task A_raw_lock_by_any_spelling_is_released(string template)
    {
        await using var db = Open();
        var name = "hyg_" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(1, await Scalar<int>(db, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, name)));
        Assert.Null(await Outside<long?>($"SELECT IS_USED_LOCK('{name}')"));
    }

    [SkippableFact]
    public async Task A_raw_transaction_is_rolled_back_before_the_next_user()
    {
        await using var db = Open();
        var tag = "hyg-" + Guid.NewGuid().ToString("N")[..10];
        await Exec(db, "START TRANSACTION");
        await Exec(db, $"INSERT INTO conf_tags (ProductId, Tag, Weight) VALUES (-1, '{tag}', 1)");   // autocommits on a clean session
        try
        {
            Assert.Equal(1, await Outside<int>($"SELECT COUNT(*) FROM conf_tags WHERE Tag = '{tag}'"));
        }
        finally
        {
            await Exec(db, $"DELETE FROM conf_tags WHERE Tag = '{tag}'");
        }
    }

    [SkippableFact]
    public async Task Advisory_locks_released_cleanly_need_no_reset()
    {
        await using var db = Open();
        using var resets = new ResetCounter(db);
        var name = "hyg_" + Guid.NewGuid().ToString("N")[..12];
        await using (var held = (await db.AcquireLockAsync(name, TimeSpan.FromSeconds(5))).ValueOrThrow())
            Assert.NotNull(await Outside<long?>($"SELECT IS_USED_LOCK('{name}')"));
        Assert.Null(await Outside<long?>($"SELECT IS_USED_LOCK('{name}')"));
        Assert.Equal(0, resets.Count);
    }

    [SkippableFact]
    public async Task Statement_errors_keep_the_session_and_interrupted_commands_reset_it()
    {
        await using var db = Open();
        using var resets = new ResetCounter(db);
        var repo = db.Repository<Product>();
        var sku = "hyg-" + Guid.NewGuid().ToString("N")[..10];
        var product = (await repo.InsertAsync(new Product { Sku = sku, Name = "h", Price = 1, Stock = 1 })).ValueOrThrow();
        try
        {
            var duplicate = await repo.InsertAsync(new Product { Sku = sku, Name = "h", Price = 1, Stock = 1 });
            Assert.True(duplicate.IsFailure);
            Assert.Equal(0, resets.Count);

            // A failure that is not a server statement error (here reading the result fails) may have interrupted the command
            // half-way: the session is reset, although the statement itself changes nothing.
            var unreadable = await db.SqlScalarAsync<DateTime>(SqlText.Raw("SELECT 'not a date'"));
            Assert.True(unreadable.IsFailure);
            Assert.Equal(1, resets.Count);
            Assert.Equal(1, await Scalar<int>(db, "SELECT 1"));   // the pool still works
        }
        finally
        {
            (await repo.DeleteAsync(product)).ValueOrThrow();
        }
    }

    [SkippableFact]
    public async Task Statements_prepared_inside_transactions_still_work_after_a_reset()
    {
        await using var db = Open(prepare: true);
        for (var i = 0; i < 3; i++)
        {
            await using var tx = (await db.BeginTransactionAsync()).ValueOrThrow();
            var value = (await tx.Db.WithConnectionAsync(async (connection, transaction, ct) =>
            {
                await using var command = new MySqlCommand("SELECT @a + 1", (MySqlConnection)connection, (MySqlTransaction?)transaction);
                command.Parameters.AddWithValue("@a", i);
                await command.PrepareAsync(ct);
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            })).ValueOrThrow();
            Assert.Equal(i + 1, value);
            (await tx.CommitAsync()).ThrowIfFailure();
        }
    }

    [SkippableFact]
    public async Task Statements_prepared_by_user_code_still_work_after_a_reset()
    {
        await using var db = Open(prepare: true);
        for (var i = 0; i < 3; i++)
        {
            var value = (await db.WithConnectionAsync(async (connection, _, ct) =>
            {
                await using var command = new MySqlCommand("SELECT @a + 1", (MySqlConnection)connection);
                command.Parameters.AddWithValue("@a", i);
                await command.PrepareAsync(ct);
                return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            })).ValueOrThrow();
            Assert.Equal(i + 1, value);
        }
    }
}
