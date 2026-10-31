using System.Data;
using System.Data.Common;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Pipeline;
using CL.Data.Query;
using CL.Data.Sql;
using CL.Data.Writes;
using CodeLogic.Core.Results;

namespace CL.Data.Execution;

public sealed partial class Database
{
    /// <summary>The current tenant (from <see cref="DatabaseOptions.TenantAccessor"/>), or null.</summary>
    public object? CurrentTenant => Options.TenantAccessor?.CurrentTenant;

    /// <summary>The current user (from <see cref="DatabaseOptions.UserAccessor"/>), or null.</summary>
    public string? CurrentUser => Options.UserAccessor?.CurrentUser;

    /// <summary>Context for planning writes on this connection.</summary>
    internal WriteContext WriteContext => new()
    {
        Dialect = Provider.Dialect,
        Capabilities = Provider.Capabilities,
        Policy = NamingPolicy,
        DefaultSchema = Provider.GetDefaultSchema(Config),
        MaxBatchSize = Config.MaxBatchSize,
        Time = Options.Time,
        User = CurrentUser,
        Tenant = CurrentTenant,
    };

    /// <summary>A repository for <typeparamref name="T"/>: key lookups and entity writes.</summary>
    public Repository<T> Repository<T>() => new(this);

    // ── Statements ──────────────────────────────────────────────────────────

    /// <summary>Renders <paramref name="statement"/>, or <c>data.unsupported</c> when the dialect cannot express it.</summary>
    internal Result<RenderedSql> Render(SqlStatement statement)
    {
        try
        {
            return SqlWriter.Render(statement, Provider.Dialect);
        }
        catch (SqlRenderException ex)
        {
            return DataErrors.Unsupported(ex.Message);
        }
    }

    /// <summary>Runs a SELECT and materializes every row.</summary>
    internal async ValueTask<Result<List<T>>> QueryAsync<T>(SelectStatement statement, IEntityAccessor<T>? accessor, string? table,
        IReadOnlyList<string> tags, CancellationToken ct)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) return rendered.Error!;
        var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.Query, ct)
        {
            Operation = "select",
            Table = table,
            Tags = tags,
        };
        return await RunAsync(context, (Accessor: accessor, Policy: NamingPolicy),
            static async (cmd, state, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await RowReader.ReadAllAsync(reader, state.Accessor, state.Policy, token).ConfigureAwait(false);
            },
            static list => list.Count).ConfigureAwait(false);
    }

    /// <summary>Runs a statement that returns one value (aggregates, EXISTS).</summary>
    internal async ValueTask<Result<T?>> ScalarAsync<T>(SqlStatement statement, string? table, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) return rendered.Error!;
        var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.Scalar, ct)
        {
            Operation = "select",
            Table = table,
            Tags = tags,
        };
        return await RunAsync(context, 0,
            static async (cmd, _, token) =>
            {
                await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                return await reader.ReadAsync(token).ConfigureAwait(false) && !reader.IsDBNull(0) ? ValueReader<T>.Read(reader, 0) : default;
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs planned writes in order and returns the total affected rows. Generated values (RETURNING/OUTPUT or the
    /// last-insert-id) are written back to <paramref name="entities"/>; row checks turn a missing row into
    /// <c>data.concurrency</c> or <c>data.not_found</c>; success hooks run after each statement succeeds.
    /// </summary>
    internal async ValueTask<Result<int>> ExecuteWritesAsync<T>(IReadOnlyList<WriteCommand> commands, IReadOnlyList<T> entities,
        IEntityAccessor<T> accessor, CancellationToken ct)
    {
        var total = 0;
        foreach (var command in commands)
        {
            var rendered = Render(command.Statement);
            if (rendered.IsFailure) return rendered.Error!;

            var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.NonQuery, ct)
            {
                Operation = command.Kind.ToString().ToLowerInvariant(),
                Table = command.Table,
                IsWrite = true,
                Tags = command.Tags,
            };

            var state = (Command: command, Entities: entities, Accessor: accessor, Provider);
            var result = await RunAsync(context, state, static async (cmd, s, token) =>
                {
                    var c = s.Command;
                    if (c.Returns)
                    {
                        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        var row = 0;
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var entity = s.Entities[c.FirstRow + row++];
                            for (var i = 0; i < c.ReturnedColumns.Count; i++)
                                if (!reader.IsDBNull(i)) s.Accessor.SetValue(entity, c.ReturnedColumns[i].Ordinal, reader.GetValue(i));
                        }
                        // A statement with RETURNING affected the rows it returned.
                        return row;
                    }

                    var affected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    if (c.IdentityFromLastInsertId is { } identity && s.Provider.ReadLastInsertId(cmd) is { } first)
                    {
                        // A multi-row INSERT gets consecutive identities starting at the first one.
                        var start = Convert.ToInt64(first, System.Globalization.CultureInfo.InvariantCulture);
                        for (var i = 0; i < c.Rows; i++) s.Accessor.SetValue(s.Entities[c.FirstRow + i], identity.Ordinal, start + i);
                    }
                    return affected;
                },
                static affected => affected,
                command.RequiresTransaction ? IsolationLevel.Serializable : null).ConfigureAwait(false);

            if (result.IsFailure) return result.Error!;
            if (command.Check == RowCheck.ExactlyOne && result.Value != 1)
                return accessor.Map.ConcurrencyToken is not null
                    ? DataErrors.Concurrency(command.Table)
                    : DataErrors.NotFound($"The row in '{command.Table}' no longer exists.");

            command.OnSuccess?.Invoke();
            total += result.Value;
        }
        return total;
    }

    private async ValueTask<Result<TResult>> ExecuteInTransactionAsync<TState, TResult>(DataNode node, CommandContext ctx, TState state,
        Func<DbCommand, TState, CancellationToken, ValueTask<TResult>> body, IsolationLevel isolation)
    {
        DbConnection connection;
        try
        {
            connection = await node.DataSource.OpenConnectionAsync(ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Executor.Map(ex, ctx.CancellationToken);
        }

        await using (connection.ConfigureAwait(false))
        {
            DbTransaction transaction;
            try
            {
                transaction = await connection.BeginTransactionAsync(isolation, ctx.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Executor.Map(ex, ctx.CancellationToken);
            }

            await using (transaction.ConfigureAwait(false))
            {
                var result = await Executor.ExecuteAsync(node, ctx.Spec, state, body, connection, transaction, ctx.CancellationToken).ConfigureAwait(false);
                try
                {
                    if (result.IsSuccess) await transaction.CommitAsync(ctx.CancellationToken).ConfigureAwait(false);
                    else await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return Executor.Map(ex, ctx.CancellationToken);
                }
                return result;
            }
        }
    }
}
