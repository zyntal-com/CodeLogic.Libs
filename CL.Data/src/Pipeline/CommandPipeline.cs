using CL.Data.Execution;
using CL.Data.Routing;
using CodeLogic.Core.Results;

namespace CL.Data.Pipeline;

/// <summary>Kind of command flowing through the pipeline.</summary>
public enum CommandKind
{
    /// <summary>Returns rows.</summary>
    Query,
    /// <summary>Returns an affected-row count.</summary>
    NonQuery,
    /// <summary>Returns one value.</summary>
    Scalar,
}

/// <summary>
/// A command on its way through the pipeline. Middleware can read it, change the SQL, pick the node and
/// attach data for later steps (<see cref="Items"/>).
/// </summary>
public sealed class CommandContext
{
    internal CommandContext(Database database, CommandSpec spec, CommandKind kind, CancellationToken ct)
    {
        Database = database;
        Spec = spec;
        Kind = kind;
        CancellationToken = ct;
    }

    /// <summary>The database the command runs on.</summary>
    public Database Database { get; }

    /// <summary>SQL and parameters. Interceptors may replace it (for example to add a comment).</summary>
    public CommandSpec Spec { get; set; }

    /// <summary>Kind of command.</summary>
    public CommandKind Kind { get; }

    /// <summary>Statement kind for telemetry: <c>select</c>, <c>insert</c>, <c>update</c>, <c>delete</c>, <c>upsert</c>, <c>ddl</c>, <c>raw</c>.</summary>
    public string Operation { get; init; } = "raw";

    /// <summary>Main table, when known.</summary>
    public string? Table { get; init; }

    /// <summary>True when the command changes data (routes to the primary, invalidates cache tags).</summary>
    public bool IsWrite { get; init; }

    /// <summary>True when the command runs inside a user transaction (no retries, no cache, primary only).</summary>
    public bool InTransaction { get; internal set; }

    /// <summary>Cache tags the command reads (queries) or invalidates (writes).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Per-query read preference override; null uses the connection setting.</summary>
    public ReadPreference? ReadFrom { get; init; }

    /// <summary>Node the command runs on. Set by routing; null means the primary.</summary>
    public DataNode? Node { get; set; }

    /// <summary>Cancellation for the whole command.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>A fresh context for the same command with another token (background cache refreshes).</summary>
    internal CommandContext Renew(CancellationToken ct) => new(Database, Spec, Kind, ct)
    {
        Operation = Operation,
        Table = Table,
        IsWrite = IsWrite,
        Tags = Tags,
        ReadFrom = ReadFrom,
    };

    /// <summary>Rows read or affected, set after execution.</summary>
    public long Rows { get; set; }

    /// <summary>Execution time, set after execution.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Free-form data shared between middleware for this command.</summary>
    public Dictionary<string, object?> Items => field ??= [];
}

/// <summary>The rest of the pipeline after a middleware. A struct, so calling down the chain allocates nothing.</summary>
/// <typeparam name="T">Result value type.</typeparam>
public readonly struct CommandNext<T>
{
    private readonly CommandPipeline _pipeline;
    private readonly int _index;
    private readonly Func<CommandContext, ValueTask<Result<T>>> _terminal;

    internal CommandNext(CommandPipeline pipeline, int index, Func<CommandContext, ValueTask<Result<T>>> terminal)
    {
        _pipeline = pipeline;
        _index = index;
        _terminal = terminal;
    }

    /// <summary>Runs the remaining middleware and then the command.</summary>
    public ValueTask<Result<T>> InvokeAsync(CommandContext context) => _pipeline.Run(context, _index, _terminal);
}

/// <summary>
/// One step in the command pipeline (telemetry, caching, routing, resilience, user interceptors). It either
/// calls <c>next</c> or returns a result itself (a cache hit, an open circuit).
/// </summary>
public interface ICommandMiddleware
{
    /// <summary>Handles <paramref name="context"/>.</summary>
    ValueTask<Result<T>> InvokeAsync<T>(CommandContext context, CommandNext<T> next);
}

/// <summary>An ordered chain of middleware ending in the command executor.</summary>
public sealed class CommandPipeline
{
    private readonly ICommandMiddleware[] _middleware;

    /// <summary>Creates a pipeline; <paramref name="middleware"/> runs outermost first.</summary>
    public CommandPipeline(IEnumerable<ICommandMiddleware> middleware) => _middleware = middleware.ToArray();

    /// <summary>A pipeline with no middleware.</summary>
    public static CommandPipeline Empty { get; } = new([]);

    /// <summary>Middleware in order.</summary>
    public IReadOnlyList<ICommandMiddleware> Middleware => _middleware;

    /// <summary>Runs <paramref name="context"/> through the middleware and then <paramref name="terminal"/>.</summary>
    public ValueTask<Result<T>> ExecuteAsync<T>(CommandContext context, Func<CommandContext, ValueTask<Result<T>>> terminal) =>
        Run(context, 0, terminal);

    internal ValueTask<Result<T>> Run<T>(CommandContext context, int index, Func<CommandContext, ValueTask<Result<T>>> terminal) =>
        index < _middleware.Length
            ? _middleware[index].InvokeAsync(context, new CommandNext<T>(this, index + 1, terminal))
            : terminal(context);
}
