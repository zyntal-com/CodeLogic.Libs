using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using CL.Data.Configuration;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CL.Data.Telemetry;
using CodeLogic.Core.Results;

namespace Data.Sqlite.Tests;

/// <summary>Spans, metrics and diagnostics events. Listeners are global, so everything is filtered by this test's database name.</summary>
public sealed class TelemetryTests : IAsyncLifetime
{
    private readonly string _name = "tele-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"cl-data-tele-{Guid.NewGuid():N}.db");
    private readonly ConcurrentQueue<DataEvent> _events = new();
    private readonly List<Database> _created = [];

    private Database Create(Action<DataConnectionConfig>? configure = null)
    {
        var config = new DataConnectionConfig { Provider = "sqlite", Database = _file, SlowQueryThresholdMs = 0 };
        configure?.Invoke(config);
        var db = Database.Create(_name, SqliteDataProvider.Instance, config, new DatabaseOptions { Events = _events.Enqueue }).ValueOrThrow();
        _created.Add(db);
        return db;
    }

    public async Task InitializeAsync()
    {
        var db = Create();
        (await db.ExecuteSqlAsync($"CREATE TABLE tele (id INTEGER PRIMARY KEY, v TEXT NOT NULL)")).ValueOrThrow();
        (await db.ExecuteSqlAsync($"INSERT INTO tele (v) VALUES ({"a"}), ({"b"}), ({"c"})")).ValueOrThrow();
    }

    public async Task DisposeAsync()
    {
        foreach (var db in _created) await db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
    }

    private bool Mine(Activity activity) => Equals(activity.GetTagItem("db.namespace"), _name);

    private bool Mine(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
            if (tag.Key == "db.namespace" && Equals(tag.Value, _name)) return true;
        return false;
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task Commands_produce_client_spans_with_database_conventions()
    {
        ConcurrentQueue<Activity> spans = new();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DataTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (Mine(a)) spans.Enqueue(a); },
        };
        ActivitySource.AddActivityListener(listener);
        var db = Create();

        var secret = "value-never-in-span";
        Assert.Equal(0L, (await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM tele WHERE v = {secret}")).ValueOrThrow());
        Assert.Null(Activity.Current);                                       // the span never leaks into the caller
        Assert.True((await db.SqlScalarAsync<long>($"SELECT COUNT(*) FROM missing_table")).IsFailure);

        var ok = spans.First(s => s.Status != ActivityStatusCode.Error);
        Assert.Equal(ActivityKind.Client, ok.Kind);
        Assert.Equal("sqlite", ok.GetTagItem("db.system.name"));
        Assert.Equal("raw", ok.GetTagItem("db.operation.name"));
        Assert.DoesNotContain(secret, (string)ok.GetTagItem("db.query.text")!);
        Assert.Equal("primary", ok.GetTagItem("cl.data.node"));

        var failed = spans.First(s => s.Status == ActivityStatusCode.Error);
        Assert.Equal("data.provider_error", failed.GetTagItem("error.type"));
    }

    [Fact]
    public async Task Metrics_record_duration_rows_retries_and_circuits()
    {
        ConcurrentQueue<(string Instrument, double Value)> measured = new();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == DataTelemetry.Name) l.EnableMeasurementEvents(instrument); },
        };
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => { if (Mine(tags)) measured.Enqueue((i.Name, v)); });
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => { if (Mine(tags)) measured.Enqueue((i.Name, v)); });
        listener.Start();

        var db = Create();
        Assert.Equal(3, (await db.SqlQueryAsync<string>($"SELECT v FROM tele")).ValueOrThrow().Count);
        Assert.Contains(measured, m => m.Instrument == "db.client.operation.duration" && m.Value > 0);
        Assert.Contains(measured, m => m.Instrument == "cl.data.rows" && m.Value == 3);

        // Retries and circuits, through the fault-injecting provider under this test's name.
        var provider = new FaultyProvider();
        await using var faulty = Database.Create(_name, provider, new DataConnectionConfig
        {
            Provider = "faulty", Host = "localhost", Database = _file, RetryCount = 1, RetryBaseDelayMs = 0, CircuitBreakerThreshold = 1,
        }, new DatabaseOptions { Events = _events.Enqueue }).ValueOrThrow();
        provider.Source.OpenFaults = int.MaxValue;
        Assert.True((await faulty.SqlScalarAsync<long>($"SELECT 1")).IsFailure);
        Assert.Contains(measured, m => m.Instrument == "cl.data.retries");
        Assert.Contains(measured, m => m.Instrument == "cl.data.circuit.opened");
        Assert.Contains(_events, e => e is CircuitStateChangedEvent { State: "Open" });

        listener.RecordObservableInstruments();                              // replica lag gauge: no replicas, no values
        Assert.DoesNotContain(measured, m => m.Instrument == "cl.data.replica.lag");
    }

    [Fact]
    public async Task Slow_reads_publish_their_plan()
    {
        var db = Create(c => { c.SlowQueryThresholdMs = 1; c.CaptureExplainOnSlowQuery = true; });
        var slow = SqlText.Raw("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300000) SELECT COUNT(*) FROM n");
        Assert.Equal(300000L, (await db.SqlScalarAsync<long>(slow)).ValueOrThrow());

        await Eventually(() => _events.OfType<SlowQueryEvent>().Any(e => e.Plan is not null), "the slow-query event");
        var e = _events.OfType<SlowQueryEvent>().First(e => e.Plan is not null);
        Assert.True(e.Duration.TotalMilliseconds >= 1);
        Assert.DoesNotContain("plan not captured", e.Plan);
        Assert.False(string.IsNullOrWhiteSpace(e.Plan));
    }

    [Fact]
    public async Task Repeated_query_shapes_are_reported_once_per_window()
    {
        var db = Create(c => c.NPlusOneThreshold = 5);
        for (var id = 1; id <= 8; id++) (await db.SqlScalarAsync<string>($"SELECT v FROM tele WHERE id = {id}")).ValueOrThrow();
        var reported = _events.OfType<NPlusOneDetectedEvent>().ToList();
        Assert.Single(reported);
        Assert.Equal(5, reported[0].Count);
        Assert.Contains("WHERE id =", reported[0].QueryShape);
    }

    [Fact]
    public async Task Query_events_are_opt_in()
    {
        var quiet = Create();
        (await quiet.SqlScalarAsync<long>($"SELECT COUNT(*) FROM tele")).ValueOrThrow();
        Assert.DoesNotContain(_events, e => e is QueryExecutedEvent);

        var loud = Create(c => c.PublishQueryEvents = true);
        Assert.Equal(3, (await loud.SqlQueryAsync<string>($"SELECT v FROM tele")).ValueOrThrow().Count);
        var executed = Assert.Single(_events.OfType<QueryExecutedEvent>());
        Assert.Equal((3L, true, "primary"), (executed.Rows, executed.Succeeded, executed.Node));
    }
}
