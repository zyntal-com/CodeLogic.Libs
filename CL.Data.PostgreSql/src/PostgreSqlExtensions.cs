using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Sql;
using CodeLogic.Core.Results;
using Npgsql;

namespace CL.Data.PostgreSql;

/// <summary>PostgreSQL-only operations on a CL.Data database: <c>db.Postgres</c>.</summary>
public static class PostgreSqlDatabaseExtensions
{
    extension(Database db)
    {
        /// <summary>PostgreSQL-only operations; each fails with <c>data.unsupported</c> on another provider.</summary>
        public PostgresFeatures Postgres => new(db);
    }
}

/// <summary>A notification received on a channel (<c>NOTIFY</c>).</summary>
/// <param name="Channel">Channel name.</param>
/// <param name="Payload">Payload text (empty when none was sent).</param>
/// <param name="ProcessId">Backend process that sent it.</param>
public sealed record PostgresNotification(string Channel, string Payload, int ProcessId);

/// <summary>PostgreSQL-only operations (<see cref="PostgreSqlDatabaseExtensions"/>).</summary>
public readonly struct PostgresFeatures(Database db)
{
    /// <summary>Sends <paramref name="payload"/> to every session listening on <paramref name="channel"/> (<c>pg_notify</c>).</summary>
    public async ValueTask<Result> NotifyAsync(string channel, string? payload = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        if (db.Provider is not PostgreSqlDataProvider) return Unsupported("NOTIFY");
        var sent = await db.SqlReadAsync($"SELECT pg_notify({channel}, {payload ?? ""})", static _ => 0, SqlIntent.SideEffect, ct).ConfigureAwait(false);
        return sent.IsFailure ? sent.Error! : Result.Success();
    }

    /// <summary>
    /// Listens on <paramref name="channel"/> on a dedicated connection of the primary and calls <paramref name="handler"/> for
    /// every notification until the returned listener is disposed. Handler exceptions are swallowed so one bad message does
    /// not stop the listener; a lost connection ends it (dispose and listen again).
    /// </summary>
    public async ValueTask<Result<IAsyncDisposable>> ListenAsync(string channel, Func<PostgresNotification, ValueTask> handler, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        if (db.Provider is not PostgreSqlDataProvider) return Unsupported("LISTEN");
        NpgsqlConnection? connection = null;
        try
        {
            connection = (NpgsqlConnection)await db.Primary.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var command = new NpgsqlCommand($"LISTEN {db.Provider.Dialect.QuoteIdentifier(channel)}", connection))
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new Listener(connection, handler);
        }
        catch (Exception ex)
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            return db.Provider.Errors.Classify(ex).Error;
        }
    }

    /// <summary>Reclaims space and refreshes planner statistics for <typeparamref name="T"/>'s table (<c>VACUUM (ANALYZE)</c>).</summary>
    public async ValueTask<Result> VacuumAnalyzeAsync<T>(CancellationToken ct = default)
    {
        if (db.Provider is not PostgreSqlDataProvider) return Unsupported("VACUUM");
        if (db.Transaction is not null) return DataErrors.Unsupported("VACUUM cannot run inside a transaction.");
        var table = db.Admin.QuotedTable<T>();
        if (table.IsFailure) return table.Error!;
        var done = await db.SqlReadAsync(SqlText.Raw($"VACUUM (ANALYZE) {table.Value}"), static _ => 0, SqlIntent.SideEffect, ct).ConfigureAwait(false);
        return done.IsFailure ? done.Error! : Result.Success();
    }

    private Error Unsupported(string what) => DataErrors.Unsupported($"{what} needs PostgreSQL; this connection uses {db.Provider.Name}.");

    private sealed class Listener : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Listener(NpgsqlConnection connection, Func<PostgresNotification, ValueTask> handler)
        {
            _connection = connection;
            connection.Notification += (_, e) =>
            {
                try { handler(new PostgresNotification(e.Channel, e.Payload, e.PID)).AsTask().GetAwaiter().GetResult(); }
                catch { /* a failing handler must not stop the listener */ }
            };
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested) await connection.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* connection lost: the listener ends */ }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try { await _loop.ConfigureAwait(false); } catch { /* already reported as ended */ }
            await _connection.DisposeAsync().ConfigureAwait(false);
            _stop.Dispose();
        }
    }
}
