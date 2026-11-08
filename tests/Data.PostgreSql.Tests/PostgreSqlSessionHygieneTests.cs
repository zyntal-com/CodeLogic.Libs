using System.Diagnostics.Metrics;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.PostgreSql;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using Data.Conformance;
using Npgsql;

namespace Data.PostgreSql.Tests;

/// <summary>
/// Session state never leaks between pooled uses of a connection with <see cref="ConnectionResetMode.WhenDirty"/> (no Npgsql
/// reset on close), and the reset runs only when needed. Every database here has a one-connection pool, so each lease reuses
/// the same server session.
/// </summary>
[Collection("postgresql")]
public sealed class PostgreSqlSessionHygieneTests(PostgreSqlFixture fixture)
{
    private Database Open(ConnectionResetMode mode = ConnectionResetMode.WhenDirty)
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var config = new DataConnectionConfig
        {
            Provider = "postgresql", ConnectionString = fixture.Db.Config.ConnectionString, MinPoolSize = 0, MaxPoolSize = 1,
            PostgreSql = new PostgreSqlOptions { ConnectionReset = mode },
        };
        return Database.Create("pghyg-" + Guid.NewGuid().ToString("N")[..8], PostgreSqlDataProvider.Instance, config).ValueOrThrow();
    }

    private static async Task<T?> Scalar<T>(Database db, string sql) => (await db.SqlScalarAsync<T>(SqlText.Raw(sql))).ValueOrThrow();

    private static async Task Exec(Database db, string sql) => (await db.ExecuteSqlAsync(SqlText.Raw(sql))).ValueOrThrow();

    private async Task<T?> Outside<T>(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.Db.Config.ConnectionString!) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

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
        var backend = await Scalar<int>(db, "SELECT pg_backend_pid()");
        var repo = db.Repository<Product>();
        var product = (await repo.InsertAsync(new Product { Sku = "pghyg-" + Guid.NewGuid().ToString("N")[..10], Name = "h", Price = 1, Stock = 1 })).ValueOrThrow();
        for (var i = 0; i < 5; i++) Assert.NotNull((await repo.GetByIdAsync(product.Id)).ValueOrThrow());   // auto-prepared after 3 uses
        (await repo.DeleteAsync(product)).ValueOrThrow();
        Assert.Equal(backend, await Scalar<int>(db, "SELECT pg_backend_pid()"));
        Assert.Equal(0, resets.Count);
    }

    [SkippableTheory]
    [InlineData(ConnectionResetMode.WhenDirty)]
    [InlineData(ConnectionResetMode.Always)]
    public async Task Raw_session_changes_do_not_reach_the_next_user(ConnectionResetMode mode)
    {
        await using var db = Open(mode);
        var path = await Scalar<string>(db, "SHOW search_path");

        await Exec(db, "SET search_path TO pg_catalog");
        Assert.Equal(path, await Scalar<string>(db, "SHOW search_path"));

        await Exec(db, "SELECT set_config('application_name', 'leak', false)");
        Assert.NotEqual("leak", await Scalar<string>(db, "SHOW application_name"));

        await Exec(db, "CREATE TEMP TABLE pghyg_tmp (id int)");
        Assert.True((await db.SqlScalarAsync<int>(SqlText.Raw("SELECT count(*) FROM pghyg_tmp"))).IsFailure);

        await Exec(db, "LISTEN pghyg_channel");
        Assert.Equal(0, await Scalar<long>(db, "SELECT count(*) FROM pg_listening_channels()"));
    }

    [SkippableFact]
    public async Task A_raw_advisory_lock_is_released_before_the_connection_idles_in_the_pool()
    {
        await using var db = Open();
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        Assert.True(await Scalar<bool>(db, $"SELECT pg_try_advisory_lock({key})"));
        Assert.Equal(0, await Outside<long>($"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND objid = {key & 0xFFFFFFFF}"));
    }

    [SkippableFact]
    public async Task A_raw_transaction_is_rolled_back_before_the_next_user()
    {
        await using var db = Open();
        var tag = "pghyg-" + Guid.NewGuid().ToString("N")[..10];
        await Exec(db, "BEGIN");
        await Exec(db, $"INSERT INTO conf_tags (\"ProductId\", \"Tag\", \"Weight\") VALUES (-1, '{tag}', 1)");   // runs on a clean session
        try
        {
            Assert.Equal(1, await Outside<long>($"SELECT count(*) FROM conf_tags WHERE \"Tag\" = '{tag}'"));
        }
        finally
        {
            await Exec(db, $"DELETE FROM conf_tags WHERE \"Tag\" = '{tag}'");
        }
    }

    [SkippableFact]
    public async Task Auto_prepared_statements_keep_working_after_a_reset()
    {
        await using var db = Open();
        var repo = db.Repository<Product>();
        var product = (await repo.InsertAsync(new Product { Sku = "pghyg-" + Guid.NewGuid().ToString("N")[..10], Name = "h", Price = 1, Stock = 1 })).ValueOrThrow();
        try
        {
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 5; i++) Assert.NotNull((await repo.GetByIdAsync(product.Id)).ValueOrThrow());
                await Exec(db, "SET work_mem = '8MB'");   // dirty: the session is reset before the next use
            }
        }
        finally
        {
            (await repo.DeleteAsync(product)).ValueOrThrow();
        }
    }

    [SkippableFact]
    public async Task Listeners_stop_listening_when_disposed()
    {
        await using var db = Open();
        var listener = (await db.Postgres.ListenAsync("pghyg_listen", _ => ValueTask.CompletedTask)).ValueOrThrow();
        await listener.DisposeAsync();
        Assert.Equal(0, await Scalar<long>(db, "SELECT count(*) FROM pg_listening_channels()"));
    }

    [SkippableFact]
    public async Task Never_leaks_session_state_as_documented()
    {
        await using var db = Open(ConnectionResetMode.Never);
        await Exec(db, "SELECT set_config('application_name', 'leak', false)");
        Assert.Equal("leak", await Scalar<string>(db, "SHOW application_name"));
    }
}
