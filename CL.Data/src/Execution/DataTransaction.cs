using System.Data;
using System.Data.Common;
using CL.Data.Errors;
using CL.Data.Query;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>
/// A database transaction on one connection of the primary. Everything done through <see cref="Db"/> (repositories,
/// queries, raw SQL) runs inside it. Disposing without <see cref="CommitAsync"/> rolls back. A transaction is not
/// thread-safe: run its commands one at a time.
/// </summary>
public sealed class DataTransaction : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly DbTransaction _transaction;
    private readonly Database _owner;
    private readonly IReadOnlyList<string> _teardown;
    private List<string>? _deferredInvalidations;
    private bool _completed;

    private DataTransaction(Database owner, DbConnection connection, DbTransaction transaction, IReadOnlyList<string> teardown)
    {
        _owner = owner;
        _connection = connection;
        _transaction = transaction;
        _teardown = teardown;
        Db = owner.BoundTo(this);
    }

    /// <summary>The database bound to this transaction.</summary>
    public Database Db { get; }

    /// <summary>True after a commit or rollback.</summary>
    public bool IsCompleted => _completed;

    /// <summary>A repository inside the transaction.</summary>
    public Repository<T> Repository<T>() => Db.Repository<T>();

    /// <summary>A query inside the transaction.</summary>
    public QueryBuilder<T> Query<T>() => Db.Query<T>();

    /// <summary>
    /// Opens a connection and begins a transaction. <paramref name="setup"/> runs on the connection before
    /// <c>BEGIN</c> and <paramref name="teardown"/> after the transaction ends (session settings such as SQLite
    /// <c>PRAGMA foreign_keys</c>, which cannot change inside a transaction).
    /// </summary>
    internal static async ValueTask<Result<DataTransaction>> BeginAsync(Database owner, IsolationLevel? isolation, CancellationToken ct,
        IReadOnlyList<string>? setup = null, IReadOnlyList<string>? teardown = null)
    {
        DbConnection? connection = null;
        try
        {
            connection = await owner.Primary.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            foreach (var statement in setup ?? [])
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            var transaction = isolation is { } level
                ? await connection.BeginTransactionAsync(level, ct).ConfigureAwait(false)
                : await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            return new DataTransaction(owner, connection, transaction, teardown ?? []);
        }
        catch (Exception ex)
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            return owner.Executor.Map(ex, ct);
        }
    }

    /// <summary>Commits.</summary>
    public async ValueTask<Result> CommitAsync(CancellationToken ct = default)
    {
        if (_completed) return DataErrors.Validation("The transaction is already completed.");
        try
        {
            await _transaction.CommitAsync(ct).ConfigureAwait(false);
            _completed = true;
            if (_deferredInvalidations is { Count: > 0 } tags) _owner.Options.Cache?.Invalidate(tags.Distinct().ToArray());
            return Result.Success();
        }
        catch (Exception ex)
        {
            return _owner.Executor.Map(ex, ct);
        }
    }

    /// <summary>Queues cache invalidations until the transaction commits (a rollback changed nothing).</summary>
    internal void DeferInvalidation(IEnumerable<string> tags) => (_deferredInvalidations ??= []).AddRange(tags);

    /// <summary>Rolls back.</summary>
    public async ValueTask<Result> RollbackAsync(CancellationToken ct = default)
    {
        if (_completed) return Result.Success();
        try
        {
            await _transaction.RollbackAsync(ct).ConfigureAwait(false);
            _completed = true;
            return Result.Success();
        }
        catch (Exception ex)
        {
            return _owner.Executor.Map(ex, ct);
        }
    }

    /// <summary>Creates a savepoint you can roll back to without ending the transaction.</summary>
    public ValueTask<Result> SavepointAsync(string name, CancellationToken ct = default) =>
        SavepointCoreAsync(name, static (t, n, token) => t.SaveAsync(n, token), "SAVEPOINT ", ct);

    /// <summary>Rolls back to a savepoint; the transaction stays open.</summary>
    public ValueTask<Result> RollbackToSavepointAsync(string name, CancellationToken ct = default) =>
        SavepointCoreAsync(name, static (t, n, token) => t.RollbackAsync(n, token), "ROLLBACK TO SAVEPOINT ", ct);

    /// <summary>Releases a savepoint (a no-op where the provider has no release).</summary>
    public ValueTask<Result> ReleaseSavepointAsync(string name, CancellationToken ct = default) =>
        SavepointCoreAsync(name, static async (t, n, token) =>
        {
            try { await t.ReleaseAsync(n, token).ConfigureAwait(false); }
            catch (NotSupportedException) { }
        }, null, ct);

    private async ValueTask<Result> SavepointCoreAsync(string name, Func<DbTransaction, string, CancellationToken, Task> action,
        string? fallbackSql, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_completed) return DataErrors.Validation("The transaction is already completed.");
        if (!_owner.Provider.Capabilities.Savepoints)
            return DataErrors.Unsupported($"Provider '{_owner.Provider.Name}' does not support savepoints.");
        try
        {
            try
            {
                await action(_transaction, name, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException) when (fallbackSql is not null)
            {
                // The driver has no savepoint API; use the standard statements.
                await using var command = _connection.CreateCommand();
                command.Transaction = _transaction;
                command.CommandText = fallbackSql + _owner.Provider.Dialect.QuoteIdentifier(name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return _owner.Executor.Map(ex, ct);
        }
    }

    /// <summary>Runs a command on the transaction's connection.</summary>
    internal ValueTask<Result<TResult>> ExecuteAsync<TState, TResult>(CommandSpec spec, TState state,
        Func<DbCommand, TState, CancellationToken, ValueTask<TResult>> body, CancellationToken ct)
    {
        if (_completed)
            return ValueTask.FromResult<Result<TResult>>(DataErrors.Validation("The transaction is already completed."));
        return _owner.Executor.ExecuteAsync(_owner.Primary, spec, state, body, _connection, _transaction, ct);
    }

    /// <summary>The connection and transaction, for streaming inside the transaction.</summary>
    internal (DbConnection Connection, DbTransaction Transaction) Handles => (_connection, _transaction);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try { await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* the connection may already be broken; disposing it ends the transaction */ }
            _completed = true;
        }
        await _transaction.DisposeAsync().ConfigureAwait(false);
        foreach (var statement in _teardown)
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* a broken connection is discarded by the pool anyway */ }
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
