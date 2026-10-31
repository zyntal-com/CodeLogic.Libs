using System.Data;
using System.Data.Common;
using CL.Data.Errors;
using CL.Data.Providers;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

/// <summary>A command ready to run: SQL text and its parameters.</summary>
/// <param name="Sql">SQL text with dialect placeholders.</param>
/// <param name="Parameters">Parameter values, in placeholder order (<c>p0</c>, <c>p1</c>, ...).</param>
/// <param name="Timeout">Command timeout; null uses the connection default.</param>
public readonly record struct CommandSpec(string Sql, IReadOnlyList<SqlParameterValue> Parameters, TimeSpan? Timeout = null);

/// <summary>
/// Runs commands on a node: opens a pooled connection (or uses the caller's), builds the command, binds
/// parameters through the provider's type mapper and maps every failure to a CL.Data error. It never throws
/// for database or cancellation failures.
/// </summary>
internal sealed class CommandExecutor(DataProvider provider, int defaultTimeoutSeconds)
{
    public DataProvider Provider { get; } = provider;

    /// <summary>
    /// Runs <paramref name="body"/> with an open command. When <paramref name="connection"/> is given the command
    /// runs on it (and in <paramref name="transaction"/>); otherwise a pooled connection is opened and closed.
    /// </summary>
    public async ValueTask<Result<TResult>> ExecuteAsync<TState, TResult>(
        DataNode node, CommandSpec spec, TState state,
        Func<DbCommand, TState, CancellationToken, ValueTask<TResult>> body,
        DbConnection? connection, DbTransaction? transaction, CancellationToken ct)
    {
        DbConnection? owned = null;
        try
        {
            if (connection is null)
            {
                owned = await node.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                connection = owned;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Prepare(command, spec);
            return Result<TResult>.Success(await body(command, state, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Map(ex, ct);
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Maps an exception to a CL.Data error (cancellation by <paramref name="ct"/> → <c>data.cancelled</c>).</summary>
    public Error Map(Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && ct.IsCancellationRequested) return DataErrors.Cancelled();
        try
        {
            return Provider.Errors.Classify(exception).Error;
        }
        catch
        {
            // A classifier bug must not hide the original failure.
            return DataErrors.FromException(exception);
        }
    }

    /// <summary>True when the provider classifies <paramref name="exception"/> as transient.</summary>
    public bool IsTransient(Exception exception)
    {
        try { return Provider.Errors.Classify(exception).IsTransient; }
        catch { return false; }
    }

    private void Prepare(DbCommand command, CommandSpec spec)
    {
        command.CommandText = spec.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = spec.Timeout is { } t ? Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)) : defaultTimeoutSeconds;
        var dialect = Provider.Dialect;
        var types = Provider.Types;
        for (var i = 0; i < spec.Parameters.Count; i++)
            command.Parameters.Add(spec.Parameters[i].Create(types, dialect.ParameterName(i)));
    }
}
