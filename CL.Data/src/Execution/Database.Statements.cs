using System.Data;
using System.Data.Common;
using System.Diagnostics;
using CL.Data.Caching;
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

    /// <summary>A query over <typeparamref name="T"/>.</summary>
    public QueryBuilder<T> Query<T>() => new(this);

    /// <summary>
    /// Runs <paramref name="work"/> with a raw driver connection: the transaction's connection inside a transaction view,
    /// otherwise a pooled connection of the primary, closed afterwards. An escape hatch for driver features CL.Data does not
    /// wrap; nothing runs through the command pipeline, and the result cache is not invalidated
    /// (call <c>Options.Cache?.Invalidate</c> after writing). Exceptions become CL.Data errors.
    /// </summary>
    public async ValueTask<Result<T>> WithConnectionAsync<T>(Func<DbConnection, DbTransaction?, CancellationToken, ValueTask<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        DbConnection? owned = null;
        try
        {
            if (_transaction is { } tx) return await work(tx.Handles.Connection, tx.Handles.Transaction, ct).ConfigureAwait(false);
            owned = await Primary.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            return await work(owned, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Executor.Map(ex, ct);
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Bulk inserts for <typeparamref name="T"/> through the provider's native bulk path.</summary>
    public CL.Data.Bulk.DataBulk<T> Bulk<T>() => new(this);

    /// <summary>A batch of queries sent in one round trip.</summary>
    public DataBatch Batch() => new(this);

    // ── Transactions ────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a transaction on the primary. Run work through <see cref="DataTransaction.Db"/> (or its
    /// <c>Repository</c>/<c>Query</c>), then commit; disposing without committing rolls back.
    /// </summary>
    public ValueTask<Result<DataTransaction>> BeginTransactionAsync(System.Data.IsolationLevel? isolation = null, CancellationToken ct = default)
    {
        if (_transaction is not null)
            return ValueTask.FromResult<Result<DataTransaction>>(
                DataErrors.Validation("This database is already inside a transaction; use savepoints for nested work."));
        return DataTransaction.BeginAsync(this, isolation, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction: commits when it succeeds, rolls back when it fails or throws, and
    /// retries the whole unit (up to <paramref name="maxAttempts"/>) after a deadlock or lock timeout. Inside an existing
    /// transaction the work joins it instead.
    /// </summary>
    public async ValueTask<Result<TResult>> InTransactionAsync<TResult>(Func<DataTransaction, CancellationToken, ValueTask<Result<TResult>>> work,
        System.Data.IsolationLevel? isolation = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_transaction is not null) return await work(_transaction, ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            var begun = await DataTransaction.BeginAsync(this, isolation, ct).ConfigureAwait(false);
            if (begun.IsFailure) return begun.Error!;

            await using var transaction = begun.Value!;
            Result<TResult> result;
            try
            {
                result = await work(transaction, ct).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Error? failure;
            if (result.IsSuccess)
            {
                var committed = await transaction.CommitAsync(ct).ConfigureAwait(false);
                if (committed.IsSuccess) return result;
                failure = committed.Error!;
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                failure = result.Error!;
            }

            if (attempt >= maxAttempts || !IsRetryable(failure)) return failure;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Config.RetryBaseDelayMs) * attempt), Options.Time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="work"/> in a transaction (see the generic overload).</summary>
    public async ValueTask<Result> InTransactionAsync(Func<DataTransaction, CancellationToken, ValueTask<Result>> work,
        System.Data.IsolationLevel? isolation = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var result = await InTransactionAsync<bool>(async (tx, token) =>
        {
            var inner = await work(tx, token).ConfigureAwait(false);
            return inner.IsSuccess ? Result<bool>.Success(true) : inner.Error!;
        }, isolation, maxAttempts, ct).ConfigureAwait(false);
        return result.ToResult();
    }

    private static bool IsRetryable(Error error) =>
        error.Code is DataErrors.DeadlockCode or DataErrors.LockTimeoutCode;

    // ── Statements ──────────────────────────────────────────────────────────

    /// <summary>Renders <paramref name="statement"/>, or <c>data.unsupported</c> when the dialect cannot express it.</summary>
    internal Result<RenderedSql> Render(SqlStatement statement)
    {
        try
        {
            var rendered = SqlWriter.Render(statement, Provider.Dialect);
            // A clear error instead of the driver's (SQL Server stops at 2100 parameters): split the IN list or use a join.
            var limit = Provider.Capabilities.ParameterLimit;
            return rendered.Parameters.Count > limit ? DataErrors.ParameterLimit(rendered.Parameters.Count, limit) : rendered;
        }
        catch (SqlRenderException ex)
        {
            return DataErrors.Unsupported(ex.Message);
        }
    }

    /// <summary>Runs a SELECT and materializes every row; with <paramref name="cache"/>, through the result cache.</summary>
    internal async ValueTask<Result<List<T>>> QueryAsync<T>(SqlStatement statement, IEntityAccessor<T>? accessor, string? table,
        IReadOnlyList<string> tags, CancellationToken ct, CachePolicy? cache = null)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) return rendered.Error!;
        return await QuerySpecAsync(new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), statement is not SelectStatement,
            accessor, table, tags, ct, cache, statement).ConfigureAwait(false);
    }

    /// <summary>Runs a pre-rendered query and materializes every row (compiled queries).</summary>
    internal async ValueTask<Result<List<T>>> QuerySpecAsync<T>(CommandSpec spec, bool isWrite, IEntityAccessor<T>? accessor, string? table,
        IReadOnlyList<string> tags, CancellationToken ct, CachePolicy? cache = null, SqlStatement? tablesFrom = null)
    {
        var context = new CommandContext(this, spec, CommandKind.Query, ct)
        {
            Operation = isWrite ? "update" : "select",
            Table = table,
            Tags = tags,
            IsWrite = isWrite,
        };
        var policy = NamingPolicy;
        var result = await ReadAsync(context, isWrite ? null : cache, tablesFrom, table,
            (reader, token) => RowReader.ReadAllAsync(reader, accessor, policy, token), static list => list.Count).ConfigureAwait(false);
        if (isWrite) InvalidateCache(tags);
        return result;
    }

    /// <summary>Runs a SELECT and builds each row with <paramref name="materialize"/> (projections, joins, groups).</summary>
    internal async ValueTask<Result<List<TResult>>> QueryProjectedAsync<TResult>(SelectStatement statement, Func<DbDataReader, TResult> materialize,
        string? table, IReadOnlyList<string> tags, CancellationToken ct, CachePolicy? cache = null)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) return rendered.Error!;
        var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.Query, ct)
        {
            Operation = "select",
            Table = table,
            Tags = tags,
        };
        return await ReadAsync(context, cache, statement, table,
            async (reader, token) =>
            {
                List<TResult> rows = [];
                while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(materialize(reader));
                return rows;
            },
            static list => list.Count).ConfigureAwait(false);
    }

    /// <summary>Runs a statement that returns one value (aggregates, EXISTS).</summary>
    internal async ValueTask<Result<T?>> ScalarAsync<T>(SqlStatement statement, string? table, IReadOnlyList<string> tags, CancellationToken ct,
        CachePolicy? cache = null)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) return rendered.Error!;
        return await ScalarSpecAsync<T>(new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), table, tags, ct,
            cache, statement).ConfigureAwait(false);
    }

    /// <summary>Runs a pre-rendered statement that returns one value (compiled queries).</summary>
    internal async ValueTask<Result<T?>> ScalarSpecAsync<T>(CommandSpec spec, string? table, IReadOnlyList<string> tags, CancellationToken ct,
        CachePolicy? cache = null, SqlStatement? tablesFrom = null)
    {
        var context = new CommandContext(this, spec, CommandKind.Scalar, ct)
        {
            Operation = "select",
            Table = table,
            Tags = tags,
        };
        return await ReadAsync(context, cache, tablesFrom, table,
            static async (reader, token) => await reader.ReadAsync(token).ConfigureAwait(false) && !reader.IsDBNull(0) ? ValueReader<T>.Read(reader, 0) : default,
            null).ConfigureAwait(false);
    }

    // ── Result cache ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a reading command and builds its result with <paramref name="read"/>. With a cache policy (outside transactions,
    /// when <see cref="DatabaseOptions.Cache"/> is set) the result set is buffered once and replayed through
    /// <paramref name="read"/> on every hit, so callers always get fresh objects.
    /// </summary>
    /// <remarks>
    /// The cache tags (the tables a statement reads, from <paramref name="tablesFrom"/>, else <paramref name="table"/>) are only
    /// worked out when a result is loaded and stored; a hit never walks the statement.
    /// </remarks>
    private async ValueTask<Result<TResult>> ReadAsync<TResult>(CommandContext context, CachePolicy? cache, SqlStatement? tablesFrom, string? table,
        Func<DbDataReader, CancellationToken, ValueTask<TResult>> read, Func<TResult, long>? rows)
    {
        if (cache is null || Options.Cache is not { } store || _transaction is not null)
        {
            return await RunAsync(context, read,
                static async (cmd, build, token) =>
                {
                    await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    return await build(reader, token).ConfigureAwait(false);
                }, rows).ConfigureAwait(false);
        }

        var loaded = await store.GetOrLoadAsync(CacheKey(context), cache, async token =>
        {
            // Background refreshes run later with their own token; the caller's request may be long gone.
            var run = token == context.CancellationToken ? context : context.Renew(token);
            var buffered = await RunAsync(run, 0,
                static async (cmd, _, token) =>
                {
                    await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    return await BufferedResultSet.CaptureAsync(reader, token).ConfigureAwait(false);
                }, static set => set.Rows.Length).ConfigureAwait(false);
            if (buffered.IsFailure) return buffered.Error!;
            var tables = tablesFrom is not null ? SqlTables.Of(tablesFrom) : table is null ? [] : [table];
            return Result<(object, long, IReadOnlyList<string>)>.Success((buffered.Value!, buffered.Value!.Size, CacheTags(tables)));
        }, context.CancellationToken, store.Config.PublishEvents ? hit => Publish(hit
            ? new CL.Data.Events.CacheHitEvent(Name, Provider.Name, Options.Time.GetUtcNow(), context.Table)
            : new CL.Data.Events.CacheMissEvent(Name, Provider.Name, Options.Time.GetUtcNow(), context.Table)) : null).ConfigureAwait(false);
        if (loaded.IsFailure) return loaded.Error!;

        await using var replay = ((BufferedResultSet)loaded.Value!).CreateReader();
        return Result<TResult>.Success(await read(replay, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>The cache key of a command: connection, kind, SQL and parameter values.</summary>
    private UInt128 CacheKey(CommandContext context)
    {
        var sb = new System.Text.StringBuilder(context.Spec.Sql.Length + 64);
        sb.Append(Name).Append('\u0001').Append((int)context.Kind).Append('\u0001').Append(context.Spec.Sql);
        foreach (var parameter in context.Spec.Parameters)
        {
            sb.Append('\u0001');
            AppendKeyValue(sb, parameter.BoxedValue, 0);
        }
        return CL.Common.Hashing.FastHash.XxHash128(sb.ToString().AsSpan());
    }

    /// <summary>
    /// Writes <paramref name="value"/> into a cache key. Arrays and other sequences (PostgreSQL <c>= ANY(@p)</c>, table-valued
    /// parameters) are written element by element, so two calls with different elements never share a cached result.
    /// </summary>
    internal static void AppendKeyValue(System.Text.StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null or DBNull: sb.Append('\u0002'); break;
            case byte[] bytes: sb.Append("b:").Append(Convert.ToBase64String(bytes)); break;
            case string text: sb.Append("s:").Append(text.Length).Append(':').Append(text); break;
            case DateTimeOffset dto: sb.Append("o:").Append(dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); break;
            case DateTime dt: sb.Append("d:").Append(dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); break;
            case IFormattable f: sb.Append(value.GetType().Name).Append(':').Append(f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)); break;
            case System.Collections.IEnumerable items when depth < 4:
                sb.Append(value.GetType().Name).Append('[');
                foreach (var item in items)
                {
                    AppendKeyValue(sb, item, depth + 1);
                    sb.Append('\u0003');
                }
                sb.Append(']');
                break;
            default: sb.Append(value.GetType().FullName).Append(':').Append(value); break;
        }
    }

    /// <summary>Tags of a cached result: one per table read, plus the connection-wide tag.</summary>
    private string[] CacheTags(IReadOnlyList<string> tables)
    {
        var tags = new string[tables.Count + 1];
        for (var i = 0; i < tables.Count; i++) tags[i] = Name + "|" + DataTags.Table(tables[i]);
        tags[^1] = Name + "|*";
        return tags;
    }

    /// <summary>
    /// Drops cached results tagged with any of <paramref name="tags"/> (<c>table:...</c>, <c>entity:...</c>, or <c>*</c> for the whole
    /// connection). Inside a transaction the invalidation waits for commit.
    /// </summary>
    internal void InvalidateCache(IReadOnlyList<string> tags)
    {
        if (Options.Cache is null || tags.Count == 0) return;
        var prefixed = new string[tags.Count];
        for (var i = 0; i < tags.Count; i++) prefixed[i] = Name + "|" + tags[i];
        if (_transaction is { } transaction) transaction.DeferInvalidation(prefixed);
        else Options.Cache.Invalidate(prefixed);
    }

    /// <summary>
    /// Runs planned writes in order and returns the total affected rows. Generated values (RETURNING/OUTPUT or the
    /// last-insert-id) are written back to <paramref name="entities"/>; row checks turn a missing row into
    /// <c>data.concurrency</c> or <c>data.not_found</c>; success hooks run after each statement succeeds.
    /// </summary>
    internal async ValueTask<Result<int>> ExecuteWritesAsync<T>(IReadOnlyList<WriteCommand> commands, IReadOnlyList<T> entities,
        IEntityAccessor<T> accessor, CancellationToken ct)
    {
        try
        {
            return await ExecuteWritesCoreAsync(commands, entities, accessor, ct).ConfigureAwait(false);
        }
        finally
        {
            // Even a partly failed batch may have changed rows (outside a transaction): drop what it touched.
            InvalidateCache([.. commands.SelectMany(c => c.Tags).Distinct()]);
        }
    }

    /// <summary>Runs a <c>SELECT 1 ... LIMIT 1</c> on the primary (inside the current transaction, if any).</summary>
    private async ValueTask<Result<bool>> RowExistsAsync(SqlStatement check, string table, CancellationToken ct)
    {
        var rendered = Render(check);
        if (rendered.IsFailure) return rendered.Error!;
        var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.Query, ct)
        {
            Operation = "select",
            Table = table,
            IsWrite = true,   // read your own write: always the primary
        };
        return await RunAsync(context, 0, static async (cmd, _, token) =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async ValueTask<Result<int>> ExecuteWritesCoreAsync<T>(IReadOnlyList<WriteCommand> commands, IReadOnlyList<T> entities,
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
                    if (c.Check == RowCheck.Empty)
                    {
                        await using var guard = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        return await guard.ReadAsync(token).ConfigureAwait(false) ? 1 : 0;
                    }
                    if (c.Returns)
                    {
                        await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        var row = 0;
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var entity = s.Entities[c.EntityIndex(row++)];
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
                        for (var i = 0; i < c.Rows; i++) s.Accessor.SetValue(s.Entities[c.EntityIndex(i)], identity.Ordinal, start + i);
                    }
                    return affected;
                },
                static affected => affected,
                command.RequiresTransaction ? IsolationLevel.Serializable : null).ConfigureAwait(false);

            if (result.IsFailure) return result.Error!;
            if (command.Check == RowCheck.Empty)
            {
                if (result.Value != 0) return DataErrors.Conflict($"A row in '{command.Table}' with the same key belongs to another tenant.");
                continue;
            }
            if (command.Check == RowCheck.ExactlyOne && result.Value == 0 && command.MatchCheck is { } match
                && Provider.Capabilities.AffectedRowsCountChangesOnly)
            {
                // The provider counts changed rows only: the row may have matched with nothing to change.
                var matched = await RowExistsAsync(match, command.Table, ct).ConfigureAwait(false);
                if (matched.IsFailure) return matched.Error!;
                if (matched.Value) result = 1;
            }
            if (command.Check == RowCheck.ExactlyOne && result.Value != 1)
                return accessor.Map.ConcurrencyToken is not null
                    ? DataErrors.Concurrency(command.Table)
                    : DataErrors.NotFound($"The row in '{command.Table}' no longer exists.");

            command.OnSuccess?.Invoke();
            total += result.Value;
        }
        return total;
    }

    /// <summary>
    /// Streams the rows of a SELECT, holding one connection while the caller enumerates. Opening the stream runs through the
    /// pipeline like every other read: interceptors, telemetry, routing (read preference, replicas, stickiness) and
    /// resilience (retry, breaker, timeout) all apply. Only the open and the first execute can be retried; a failure while
    /// rows are being read is thrown, since rows may already have been handed out.
    /// </summary>
    internal async IAsyncEnumerable<T> StreamAsync<T>(SelectStatement statement, IEntityAccessor<T> accessor, string? table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var rendered = Render(statement);
        if (rendered.IsFailure) throw new DataQueryException(rendered.Error!);
        var context = new CommandContext(this, new CommandSpec(rendered.Value!.Text, rendered.Value.Parameters), CommandKind.Query, ct)
        {
            Operation = "select",
            Table = table,
            Tags = table is null ? [] : [DataTags.Table(table)],
        };

        var opened = await OpenStreamAsync(context, accessor.Map).ConfigureAwait(false);
        if (opened.IsFailure) throw new DataQueryException(opened.Error!);
        await using var stream = opened.Value!;
        while (true)
        {
            T row;
            try
            {
                if (!await stream.Reader.ReadAsync(ct).ConfigureAwait(false)) yield break;
                row = accessor.Materialize(stream.Reader, stream.Ordinals);
            }
            catch (Exception ex)
            {
                throw new DataQueryException(Executor.Map(ex, ct));
            }
            yield return row;
        }
    }

    /// <summary>An open reader and what it needs; disposing it closes the reader, the command and an owned connection.</summary>
    private sealed class OpenStream(DbConnection? connection, DbCommand command, DbDataReader reader, int[] ordinals) : IAsyncDisposable
    {
        public DbDataReader Reader { get; } = reader;
        public int[] Ordinals { get; } = ordinals;

        public async ValueTask DisposeAsync()
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
            await command.DisposeAsync().ConfigureAwait(false);
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ValueTask<Result<OpenStream>> OpenStreamAsync(CommandContext context, EntityMap map)
    {
        if (_transaction is not null) context.InTransaction = true;
        if (!CL.Data.Telemetry.DataTelemetry.Active) return OpenStreamPipelineAsync(context, map);
        var started = Stopwatch.GetTimestamp();
        CL.Data.Telemetry.DataTelemetry.StartSpan(context);
        return CL.Data.Telemetry.DataTelemetry.ObserveAsync(context, OpenStreamPipelineAsync(context, map), started);
    }

    private ValueTask<Result<OpenStream>> OpenStreamPipelineAsync(CommandContext context, EntityMap map) =>
        _pipeline.ExecuteAsync(context, async ctx =>
        {
            var started = Stopwatch.GetTimestamp();
            var node = ctx.Node ?? Primary;
            var result = await OpenOnNodeAsync(node, ctx, map).ConfigureAwait(false);
            ctx.Elapsed = Stopwatch.GetElapsedTime(started);
            _diagnostics.After(ctx, node, result);
            return result;
        });

    private async ValueTask<Result<OpenStream>> OpenOnNodeAsync(DataNode node, CommandContext ctx, EntityMap map)
    {
        var ct = ctx.CancellationToken;
        DbConnection? owned = null;
        DbCommand? command = null;
        DbDataReader? reader = null;
        var opening = false;
        try
        {
            if (_transaction is { } tx)
            {
                command = tx.Handles.Connection.CreateCommand();
                command.Transaction = tx.Handles.Transaction;
            }
            else
            {
                opening = true;
                owned = await node.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                opening = false;
                command = owned.CreateCommand();
            }
            _executor.Prepare(command, ctx.Spec);
            reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return new OpenStream(owned, command, reader, RowReader.Ordinals(reader, map, NamingPolicy));
        }
        catch (Exception ex)
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            if (command is not null) await command.DisposeAsync().ConfigureAwait(false);
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            return _executor.Fail(ctx, ex, opening, ct);
        }
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
            return Executor.Fail(ctx, ex, opening: true, ctx.CancellationToken);
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
                var result = await Executor.ExecuteAsync(node, ctx.Spec, state, body, connection, transaction, ctx.CancellationToken, ctx).ConfigureAwait(false);
                try
                {
                    if (result.IsSuccess) await transaction.CommitAsync(ctx.CancellationToken).ConfigureAwait(false);
                    else await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A failed commit leaves the outcome unknown: report it, but never retry it.
                    var error = Executor.Map(ex, ctx.CancellationToken);
                    ctx.FailedTransient = false;
                    return error;
                }
                return result;
            }
        }
    }
}
