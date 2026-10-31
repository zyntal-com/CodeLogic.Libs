using System.Collections.Concurrent;
using System.Data.Common;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CodeLogic.Core.Results;

namespace CL.Data.Telemetry;

/// <summary>A connection on which an explainer runs the statements it needs (the slow query's parameters are bound for it).</summary>
public sealed class ExplainSession
{
    private readonly DbConnection _connection;
    private readonly CommandSpec _spec;
    private readonly Action<DbCommand, CommandSpec> _bind;

    internal ExplainSession(DbConnection connection, CommandSpec spec, Action<DbCommand, CommandSpec> bind)
    {
        _connection = connection;
        _spec = spec;
        _bind = bind;
    }

    /// <summary>SQL of the slow query (with parameter placeholders).</summary>
    public string Sql => _spec.Sql;

    /// <summary>Runs <paramref name="sql"/> (with the slow query's parameters when <paramref name="withParameters"/>) and returns each row as text.</summary>
    public async Task<List<string>> ReadAsync(string sql, bool withParameters, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        if (withParameters) _bind(command, _spec with { Sql = sql });
        else command.CommandText = sql;
        List<string> lines = [];
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                lines.Add(string.Join(" | ", values));
            }
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));
        return lines;
    }

    /// <summary>The slow query's parameters as the driver binds them (types, sizes, values), for explainers that declare them.</summary>
    public IReadOnlyList<DbParameter> Parameters()
    {
        using var command = _connection.CreateCommand();
        _bind(command, _spec);
        var parameters = command.Parameters.Cast<DbParameter>().ToArray();
        command.Parameters.Clear();
        return parameters;
    }

    /// <summary>Runs a statement without parameters (session settings).</summary>
    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Captures the execution plan of a slow read (<c>CaptureExplainOnSlowQuery</c>). Each server provider supplies one.</summary>
public abstract class QueryExplainer
{
    /// <summary>Returns the plan of <see cref="ExplainSession.Sql"/> as text, without executing the query.</summary>
    public abstract Task<string?> ExplainAsync(ExplainSession session, CancellationToken ct);
}

/// <summary>An explainer that prefixes the query (<c>EXPLAIN</c>, <c>EXPLAIN QUERY PLAN</c>, ...) and joins the rows.</summary>
/// <param name="prefix">Text put before the query, including the trailing space.</param>
public sealed class PrefixExplainer(string prefix) : QueryExplainer
{
    /// <inheritdoc />
    public override async Task<string?> ExplainAsync(ExplainSession session, CancellationToken ct) =>
        string.Join('\n', await session.ReadAsync(prefix + session.Sql, withParameters: true, ct).ConfigureAwait(false));
}

/// <summary>
/// Per-database diagnostics run after every attempt: slow-query events (with the plan, when enabled), N+1 detection and
/// optional per-command events. Each check is an integer comparison when it is off.
/// </summary>
internal sealed class QueryDiagnostics(Database db)
{
    private readonly ConcurrentDictionary<string, Window> _shapes = new(StringComparer.Ordinal);

    private sealed class Window
    {
        public long Start;
        public int Count;
    }

    public void After<T>(CommandContext context, DataNode node, Result<T> result)
    {
        var config = db.Config;
        if (config.PublishQueryEvents)
            db.Publish(new QueryExecutedEvent(db.Name, db.Provider.Name, node.Name, db.Options.Time.GetUtcNow(),
                context.Operation, context.Table, context.Elapsed, context.Rows, result.IsSuccess));

        if (config.SlowQueryThresholdMs > 0 && context.Elapsed.TotalMilliseconds >= config.SlowQueryThresholdMs)
            Slow(context, node);

        if (config.NPlusOneThreshold > 0 && !context.IsWrite) Repeated(context, node, config.NPlusOneThreshold);
    }

    private void Slow(CommandContext context, DataNode node)
    {
        var spec = context.Spec;
        var elapsed = context.Elapsed;
        if (!db.Config.CaptureExplainOnSlowQuery || context.IsWrite || db.Provider.Explainer is null || context.InTransaction)
        {
            db.Publish(new SlowQueryEvent(db.Name, db.Provider.Name, node.Name, db.Options.Time.GetUtcNow(), spec.Sql, elapsed, null));
            return;
        }
        // The plan is captured on its own connection in the background: the caller never waits for it.
        _ = Task.Run(async () =>
        {
            var plan = await ExplainAsync(node, spec).ConfigureAwait(false);
            db.Publish(new SlowQueryEvent(db.Name, db.Provider.Name, node.Name, db.Options.Time.GetUtcNow(), spec.Sql, elapsed, plan));
        });
    }

    private async Task<string?> ExplainAsync(DataNode node, CommandSpec spec)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var connection = await node.DataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            var session = new ExplainSession(connection, spec, db.Executor.Bind);
            return await db.Provider.Explainer!.ExplainAsync(session, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"(plan not captured: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private void Repeated(CommandContext context, DataNode node, int threshold)
    {
        var now = db.Options.Time.GetUtcNow().UtcTicks;
        if (_shapes.Count > 10_000) _shapes.Clear();   // bounded: shapes are SQL texts, which are few in a normal app
        var window = _shapes.GetOrAdd(context.Spec.Sql, static _ => new Window());
        int count;
        lock (window)
        {
            if (now - window.Start > TimeSpan.TicksPerSecond)
            {
                window.Start = now;
                window.Count = 0;
            }
            count = ++window.Count;
        }
        if (count == threshold)   // once per window
            db.Publish(new NPlusOneDetectedEvent(db.Name, db.Provider.Name, node.Name, db.Options.Time.GetUtcNow(),
                context.Spec.Sql, count, TimeSpan.FromSeconds(1)));
    }
}
