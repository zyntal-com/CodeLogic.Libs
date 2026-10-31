using System.Data.Common;
using System.Globalization;
using CL.Data.Errors;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>
/// One connection to the primary, held open for work that needs the same session throughout: advisory locks,
/// session settings (SQLite <c>PRAGMA foreign_keys</c>) and schema changes. Commands run directly on the
/// connection, outside the command pipeline.
/// </summary>
internal sealed class DataSession : IAsyncDisposable
{
    private readonly Database _db;
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;

    private DataSession(Database db, DbConnection connection)
    {
        _db = db;
        _connection = connection;
    }

    /// <summary>Opens a session on the primary.</summary>
    public static async ValueTask<Result<DataSession>> OpenAsync(Database db, CancellationToken ct)
    {
        try
        {
            var connection = await db.Primary.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return new DataSession(db, connection);
        }
        catch (Exception ex)
        {
            return db.Executor.Map(ex, ct);
        }
    }

    /// <summary>Runs a statement.</summary>
    public ValueTask<Result<int>> ExecuteAsync(SqlText sql, CancellationToken ct) =>
        _db.Executor.ExecuteAsync(_db.Primary, Spec(sql), 0,
            static async (cmd, _, token) => await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false),
            _connection, _transaction, ct);

    /// <summary>Runs a query and returns the first value as a number (booleans as 0/1; null as 0).</summary>
    public ValueTask<Result<long>> NumberAsync(SqlText sql, CancellationToken ct) =>
        _db.Executor.ExecuteAsync(_db.Primary, Spec(sql), 0,
            static async (cmd, _, token) => await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) switch
            {
                null or DBNull => 0L,
                bool b => b ? 1L : 0L,
                var v => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            },
            _connection, _transaction, ct);

    /// <summary>Runs a query and builds each row with <paramref name="read"/>.</summary>
    public ValueTask<Result<List<T>>> ReadAsync<T>(SqlText sql, Func<DbDataReader, T> read, CancellationToken ct) =>
        _db.Executor.ExecuteAsync(_db.Primary, Spec(sql), read,
            static async (cmd, build, token) =>
            {
                List<T> rows = [];
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(build(reader));
                return rows;
            },
            _connection, _transaction, ct);

    /// <summary>Starts a transaction on the session.</summary>
    public async ValueTask<Result> BeginAsync(CancellationToken ct)
    {
        try
        {
            _transaction = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return _db.Executor.Map(ex, ct);
        }
    }

    /// <summary>Commits the session's transaction.</summary>
    public async ValueTask<Result> CommitAsync(CancellationToken ct)
    {
        if (_transaction is not { } tx) return Result.Success();
        try
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return _db.Executor.Map(ex, ct);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    /// <summary>Rolls the session's transaction back (errors are ignored: the failure that caused it matters more).</summary>
    public async ValueTask RollbackAsync()
    {
        if (_transaction is not { } tx) return;
        try { await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* the connection may already be broken */ }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    private CommandSpec Spec(SqlText sql) => new(sql.Render(_db.Provider.Dialect), sql.Parameters);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await RollbackAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
