using System.Data.Common;
using CL.Data.Errors;
using CL.Data.Execution;
using CL.Data.Mapping;
using CL.Data.Pipeline;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>The result of one query in a <see cref="DataBatch"/>; available after the batch ran.</summary>
/// <typeparam name="T">Result type.</typeparam>
public sealed class BatchResult<T>
{
    private T? _value;
    private bool _set;

    /// <summary>The query's result. Throws when the batch has not run successfully.</summary>
    public T Value => _set ? _value! : throw new InvalidOperationException("The batch has not run (or failed).");

    internal void Set(T value)
    {
        _value = value;
        _set = true;
    }
}

/// <summary>
/// Several queries sent to the database in one round trip. Add queries, run <see cref="ExecuteAsync"/>, then read each
/// <see cref="BatchResult{T}.Value"/>. Uses <see cref="DbBatch"/> where the driver supports it, otherwise one
/// multi-statement command; either way one round trip and one connection.
/// </summary>
/// <example>
/// <code>
/// var batch = db.Batch();
/// var recent = batch.Add(db.Query&lt;Order&gt;().OrderByDescending(o =&gt; o.Id).Take(10));
/// var total = batch.Count(db.Query&lt;Order&gt;());
/// (await batch.ExecuteAsync(ct)).OnFailure(...);
/// Show(recent.Value, total.Value);
/// </code>
/// </example>
public sealed class DataBatch
{
    private readonly Database _db;
    private readonly List<Item> _items = [];
    private Error? _planError;

    internal DataBatch(Database db) => _db = db;

    /// <summary>Number of queries in the batch.</summary>
    public int Size => _items.Count;

    /// <summary>Adds a query returning its rows.</summary>
    public BatchResult<List<T>> Add<T>(QueryBuilder<T> query)
    {
        var result = new BatchResult<List<T>>();
        var plan = query.PlanSelect();
        if (plan.IsFailure) return Fail(result, plan.Error!);
        var (select, accessor, _) = plan.Value!;
        _items.Add(new Item(select, null, async (reader, ct) =>
            result.Set(await RowReader.ReadAllAsync(reader, accessor, _db.NamingPolicy, ct).ConfigureAwait(false))));
        return result;
    }

    /// <summary>Adds a count of a query's rows.</summary>
    public BatchResult<long> Count<T>(QueryBuilder<T> query)
    {
        var result = new BatchResult<long>();
        var plan = query.PlanSelect();
        if (plan.IsFailure) return Fail(result, plan.Error!);
        var count = plan.Value!.Select with
        {
            Projection = [new ProjectionItem(new AggregateExpr(SqlAggregate.Count, null))], OrderBy = [], Limit = null, Offset = null,
        };
        _items.Add(new Item(count, null, async (reader, ct) =>
            result.Set(await reader.ReadAsync(ct).ConfigureAwait(false) ? ValueReader<long>.Read(reader, 0) : 0)));
        return result;
    }

    /// <summary>
    /// Adds a raw query (scalars or mapped rows, like <c>SqlQueryAsync</c>). Its <see cref="SqlIntent"/> is worked out from the
    /// text (<see cref="SqlIntent.Auto"/>); one write makes the whole batch a write (primary, no retry once sent, cache dropped).
    /// </summary>
    public BatchResult<List<T>> Sql<T>(SqlText sql) => Sql<T>(sql, SqlIntent.Auto);

    /// <summary>Adds a raw query with an explicit <paramref name="intent"/> (see <see cref="Sql{T}(SqlText)"/>).</summary>
    public BatchResult<List<T>> Sql<T>(SqlText sql, SqlIntent intent)
    {
        var result = new BatchResult<List<T>>();
        var accessor = RowReader.AccessorFor<T>();
        if (accessor.IsFailure) return Fail(result, accessor.Error!);
        _items.Add(new Item(null, sql, async (reader, ct) =>
            result.Set(await RowReader.ReadAllAsync(reader, accessor.Value, _db.NamingPolicy, ct).ConfigureAwait(false)))
        {
            Intent = intent,
        });
        return result;
    }

    /// <summary>Runs every query in one round trip. The first planning error (for example a translation failure) is returned without running anything.</summary>
    public async ValueTask<Result> ExecuteAsync(CancellationToken ct = default)
    {
        if (_planError is not null) return _planError;
        if (_items.Count == 0) return Result.Success();

        // Each statement rendered alone (for DbBatch) and once more with globally numbered placeholders (one command text:
        // what providers without DbBatch run, and what telemetry sees).
        var dialect = _db.Provider.Dialect;
        List<RenderedSql> separate = [];
        List<string> parts = [];
        List<SqlParameterValue> parameters = [];
        bool isWrite = false, invalidates = false, primary = false;
        try
        {
            foreach (var item in _items)
            {
                if (item.Statement is not null)
                {
                    separate.Add(SqlWriter.Render(item.Statement, dialect));
                    parts.Add(SqlWriter.Render(item.Statement, dialect, parameters.Count).Text);
                }
                else
                {
                    var raw = item.Raw!.Value;
                    var text = raw.Render(dialect);
                    separate.Add(new RenderedSql(text, raw.Parameters));
                    parts.Add(raw.Render(dialect, parameters.Count));
                    isWrite |= SqlClassifier.IsWrite(item.Intent, text);
                    invalidates |= SqlClassifier.Invalidates(item.Intent, text);
                    primary |= item.Intent == SqlIntent.ReadPrimary;
                }
                parameters.AddRange(separate[^1].Parameters);
            }
        }
        catch (SqlRenderException ex)
        {
            return DataErrors.Unsupported(ex.Message);
        }
        var combined = string.Join(";\n", parts);

        var context = new CommandContext(_db, new CommandSpec(combined, parameters), CommandKind.Query, ct)
        {
            Operation = "batch",
            IsWrite = isWrite,
            ReadFrom = primary ? CL.Data.Routing.ReadPreference.Primary : null,
        };
        var result = await _db.RunAsync(context, (Items: _items, Separate: separate, Db: _db),
            static async (cmd, state, token) =>
            {
                var connection = cmd.Connection!;
                if (connection.CanCreateBatch && state.Db.Provider.Capabilities.DbBatch)
                {
                    await using var batch = connection.CreateBatch();
                    batch.Transaction = cmd.Transaction;
                    batch.Timeout = cmd.CommandTimeout;
                    foreach (var rendered in state.Separate)
                    {
                        var command = batch.CreateBatchCommand();
                        command.CommandText = rendered.Text;
                        for (var i = 0; i < rendered.Parameters.Count; i++)
                            command.Parameters.Add(rendered.Parameters[i].Create(state.Db.Provider.Types, state.Db.Provider.Dialect.ParameterName(i)));
                        batch.BatchCommands.Add(command);
                    }
                    await using var reader = await batch.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await ReadAllAsync(reader, state.Items, token).ConfigureAwait(false);
                }
                else
                {
                    await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await ReadAllAsync(reader, state.Items, token).ConfigureAwait(false);
                }
                return true;
            }).ConfigureAwait(false);
        if (invalidates) _db.InvalidateCache(["*"]);   // raw SQL may change any table
        return result.ToResult();
    }

    private static async ValueTask ReadAllAsync(DbDataReader reader, List<Item> items, CancellationToken ct)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0 && !await reader.NextResultAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException($"The batch returned {i} result set(s); {items.Count} were expected.");
            await items[i].Read(reader, ct).ConfigureAwait(false);
        }
    }

    private BatchResult<T> Fail<T>(BatchResult<T> result, Error error)
    {
        _planError ??= error;
        return result;
    }

    private sealed record Item(SqlStatement? Statement, SqlText? Raw, Func<DbDataReader, CancellationToken, ValueTask> Read)
    {
        public SqlIntent Intent { get; init; }
    }
}
