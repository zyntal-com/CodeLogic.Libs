using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CL.Data.Execution;
using CL.Data.Pipeline;
using CodeLogic.Core.Results;

namespace CL.Data.Telemetry;

/// <summary>
/// CL.Data's OpenTelemetry sources, following the database semantic conventions. Subscribe with
/// <c>AddSource("CL.Data")</c> / <c>AddMeter("CL.Data")</c>. Nothing is measured or allocated while no listener is attached.
/// <list type="bullet">
/// <item>Spans: one client span per logical command (retries included), named <c>{operation} {table}</c>, with
/// <c>db.system.name</c>, <c>db.namespace</c>, <c>db.operation.name</c>, <c>db.collection.name</c>, <c>db.query.text</c>
/// (parameterized SQL, never values), <c>server.address</c>, <c>db.response.returned_rows</c>, <c>error.type</c>.</item>
/// <item><c>db.client.operation.duration</c> (s), <c>cl.data.rows</c>, <c>cl.data.retries</c>, <c>cl.data.cache.hits</c>,
/// <c>cl.data.cache.misses</c>, <c>cl.data.circuit.opened</c>, and the observable <c>cl.data.replica.lag</c> (ms).</item>
/// </list>
/// Connection-pool metrics come from the drivers themselves (MySqlConnector, Npgsql, SqlClient publish their own meters).
/// </summary>
public static class DataTelemetry
{
    /// <summary>Name of the activity source and the meter.</summary>
    public const string Name = "CL.Data";

    private static readonly string? Version = typeof(DataTelemetry).Assembly.GetName().Version?.ToString();

    /// <summary>The activity source for command spans.</summary>
    public static ActivitySource Source { get; } = new(Name, Version);

    /// <summary>The meter for CL.Data metrics.</summary>
    public static Meter Meter { get; } = new(Name, Version);

    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("db.client.operation.duration", "s",
        "Duration of database client operations.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10] });

    internal static readonly Counter<long> Rows = Meter.CreateCounter<long>("cl.data.rows", "{row}", "Rows read or affected.");
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("cl.data.retries", "{retry}", "Commands retried after a transient failure.");
    internal static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("cl.data.cache.hits", "{hit}", "Reads answered from the result cache.");
    internal static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("cl.data.cache.misses", "{miss}", "Cached reads that went to the database.");
    internal static readonly Counter<long> CircuitOpened = Meter.CreateCounter<long>("cl.data.circuit.opened", "{event}", "Node circuits that opened.");

    private static readonly ConcurrentDictionary<Database, byte> Live = new();

    static DataTelemetry() =>
        Meter.CreateObservableGauge("cl.data.replica.lag", ObserveLag, "ms", "Replication lag of replicas from the last probe.");

    /// <summary>True when a span or the duration metric would be recorded.</summary>
    internal static bool Active => Source.HasListeners() || Duration.Enabled || Rows.Enabled;

    internal static void Track(Database db) => Live.TryAdd(db, 0);
    internal static void Untrack(Database db) => Live.TryRemove(db, out _);

    private static IEnumerable<Measurement<long>> ObserveLag()
    {
        foreach (var db in Live.Keys)
            foreach (var node in db.Replicas)
                if (node.LagMs is { } lag)
                    yield return new Measurement<long>(lag, new("db.namespace", db.Name), new("cl.data.node", node.Name));
    }

    /// <summary><c>db.system.name</c> for a provider.</summary>
    internal static string SystemName(string provider) => provider switch
    {
        "sqlserver" => "microsoft.sql_server",
        _ => provider,
    };

    /// <summary>Runs <paramref name="run"/> inside a span and records the duration and row metrics.</summary>
    internal static async ValueTask<Result<T>> ObserveAsync<T>(CommandContext context, ValueTask<Result<T>> run, long started)
    {
        // The span starts before the pipeline runs (the caller passes the started task's start time): Activity.Current
        // flows into interceptors and the driver, which may add child spans.
        var activity = context.Items.TryGetValue(ActivityKey, out var a) ? a as Activity : null;
        Result<T> result;
        try
        {
            result = await run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.Dispose();
            throw;
        }

        var db = context.Database;
        var system = SystemName(db.Provider.Name);
        var error = result.IsFailure ? result.Error!.Code : null;
        if (Duration.Enabled)
        {
            TagList tags = new()
            {
                { "db.system.name", system },
                { "db.namespace", db.Name },
                { "db.operation.name", context.Operation },
            };
            if (context.Table is not null) tags.Add("db.collection.name", context.Table);
            if (error is not null) tags.Add("error.type", error);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
        if (Rows.Enabled && result.IsSuccess && context.Rows > 0)
            Rows.Add(context.Rows, new KeyValuePair<string, object?>("db.namespace", db.Name), new("db.operation.name", context.Operation));

        if (activity is not null)
        {
            var node = context.Node ?? db.Primary;
            activity.SetTag("server.address", node.Endpoint.Host);
            if (node.Endpoint.Port is { } port) activity.SetTag("server.port", port);
            activity.SetTag("cl.data.node", node.Name);
            if (context.Attempt > 1) activity.SetTag("cl.data.attempts", context.Attempt);
            if (result.IsSuccess) activity.SetTag("db.response.returned_rows", context.Rows);
            else
            {
                activity.SetTag("error.type", error);
                activity.SetStatus(ActivityStatusCode.Error, result.Error!.Message);
            }
            activity.Dispose();
        }
        return result;
    }

    internal const string ActivityKey = "cl.data.activity";

    /// <summary>Starts the span for <paramref name="context"/> when someone listens; stored in <see cref="CommandContext.Items"/>.</summary>
    internal static void StartSpan(CommandContext context)
    {
        if (!Source.HasListeners()) return;
        var db = context.Database;
        var name = context.Table is null ? context.Operation : $"{context.Operation} {context.Table}";
        var activity = Source.StartActivity(name, ActivityKind.Client);
        if (activity is null) return;
        if (activity.IsAllDataRequested)
        {
            activity.SetTag("db.system.name", SystemName(db.Provider.Name));
            activity.SetTag("db.namespace", db.Name);
            activity.SetTag("db.operation.name", context.Operation);
            if (context.Table is not null) activity.SetTag("db.collection.name", context.Table);
            activity.SetTag("db.query.text", context.Spec.Sql);
        }
        context.Items[ActivityKey] = activity;
    }
}
