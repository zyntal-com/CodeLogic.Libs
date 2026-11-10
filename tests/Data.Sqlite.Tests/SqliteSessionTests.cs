using System.Diagnostics.Metrics;
using CL.Data.Configuration;
using CL.Data.Execution;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CodeLogic.Core.Results;
using Data.Conformance;
using Microsoft.Data.Sqlite;

namespace Data.Sqlite.Tests;

/// <summary>
/// The connection pragmas (<c>foreign_keys</c>, <c>synchronous</c>, <c>busy_timeout</c>) are applied once per pooled native
/// connection, not on every open, and still never leak: whatever may have changed them makes the next open apply them again.
/// The tests read a pragma through its table-valued function (<c>SELECT * FROM pragma_x</c>), a plain query that does not
/// itself count as changing the session.
/// </summary>
public sealed class SqliteSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cl-data-session-{Guid.NewGuid():N}.db");

    private Database Open(SqliteOptions? options = null) =>
        Database.Create("session-" + Guid.NewGuid().ToString("N")[..8], SqliteDataProvider.Instance,
            new DataConnectionConfig { Provider = "sqlite", Database = _path, Sqlite = options ?? new SqliteOptions() }).ValueOrThrow();

    private static async Task<long> Pragma(Database db, string name) =>
        (await db.SqlScalarAsync<long>(SqlText.Raw($"SELECT * FROM pragma_{name}"))).ValueOrThrow();

    private static async Task Exec(Database db, string sql) => (await db.ExecuteSqlAsync(SqlText.Raw(sql))).ValueOrThrow();

    /// <summary>Counts the connections of one database that were marked to get their pragmas again.</summary>
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

    [Fact]
    public async Task Pragmas_hold_across_pooled_uses_without_being_applied_again()
    {
        await using var db = Open(new SqliteOptions { ForeignKeys = true, BusyTimeoutMs = 4321, Synchronous = SqliteSynchronous.Full });
        using var resets = new ResetCounter(db);
        (await db.Schema.SyncAsync<Product>()).ValueOrThrow();
        var before = resets.Count;   // schema sync switches foreign keys off and on again: its connection is refreshed

        var repo = db.Repository<Product>();
        var row = (await repo.InsertAsync(new Product { Sku = "S-1", Name = "one", Price = 1, Stock = 1 })).ValueOrThrow();
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull((await repo.GetByIdAsync(row.Id)).ValueOrThrow());
            Assert.Equal(1, (await db.Query<Product>().Where(p => p.Stock == 1).CountAsync()).ValueOrThrow());
            Assert.Equal(1, await Pragma(db, "foreign_keys"));
            Assert.Equal(4321, await Pragma(db, "busy_timeout"));
            Assert.Equal(2, await Pragma(db, "synchronous"));   // FULL
        }
        row.Name = "renamed";
        (await repo.UpdateAsync(row)).ValueOrThrow();

        Assert.Equal(before, resets.Count);   // generated statements and plain queries never mark a connection
    }

    [Fact]
    public async Task A_changed_pragma_never_reaches_the_next_command()
    {
        await using var db = Open(new SqliteOptions { ForeignKeys = true, BusyTimeoutMs = 4321 });
        using var resets = new ResetCounter(db);

        await Exec(db, "PRAGMA foreign_keys=OFF");
        Assert.Equal(1, await Pragma(db, "foreign_keys"));
        await Exec(db, "PRAGMA busy_timeout=1");
        Assert.Equal(4321, await Pragma(db, "busy_timeout"));
        Assert.Equal(2, resets.Count);

        // User code with the raw connection.
        (await db.WithConnectionAsync(async (connection, _, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=2";
            return await command.ExecuteNonQueryAsync(token);
        })).ValueOrThrow();
        Assert.Equal(1, await Pragma(db, "foreign_keys"));
        Assert.Equal(4321, await Pragma(db, "busy_timeout"));
        Assert.Equal(3, resets.Count);

        // Inside a transaction the change lasts until the transaction's connection goes back to the pool.
        var tx = (await db.BeginTransactionAsync()).ValueOrThrow();
        await using (tx)
        {
            (await tx.Db.ExecuteSqlAsync(SqlText.Raw("PRAGMA busy_timeout=3"))).ValueOrThrow();
            (await tx.CommitAsync()).ThrowIfFailure();
        }
        Assert.Equal(4321, await Pragma(db, "busy_timeout"));
    }

    [Fact]
    public async Task Databases_sharing_a_file_keep_their_own_pragmas()
    {
        // Same connection string, so both use the driver's one pool and take turns on the same native connection.
        await using var strict = Open(new SqliteOptions { ForeignKeys = true, BusyTimeoutMs = 1111 });
        await using var loose = Open(new SqliteOptions { ForeignKeys = false, BusyTimeoutMs = 2222 });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1111, await Pragma(strict, "busy_timeout"));
            Assert.Equal(1, await Pragma(strict, "foreign_keys"));
            Assert.Equal(2222, await Pragma(loose, "busy_timeout"));
            Assert.Equal(0, await Pragma(loose, "foreign_keys"));
        }
    }

    [Fact]
    public async Task Unpooled_connections_get_their_pragmas_on_every_open()
    {
        await using var db = Database.Create("session-unpooled", SqliteDataProvider.Instance, new DataConnectionConfig
        {
            Provider = "sqlite", Database = _path, Pooling = false, Sqlite = new SqliteOptions { BusyTimeoutMs = 3333 },
        }).ValueOrThrow();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(3333, await Pragma(db, "busy_timeout"));
            Assert.Equal(1, await Pragma(db, "foreign_keys"));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); }
            catch (IOException) { /* another test's pool still holds it; the temp folder is cleaned by the system */ }
        }
    }
}
