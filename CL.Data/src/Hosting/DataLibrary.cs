using System.Collections.Concurrent;
using CL.Data.Caching;
using CL.Data.Configuration;
using CL.Data.Errors;
using CL.Data.Events;
using CL.Data.Execution;
using CL.Data.Routing;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Data.Hosting;

/// <summary>
/// <b>CL.Data</b> as a CodeLogic library: owns the <c>data</c> and <c>data.cache</c> config sections and one
/// <see cref="Database"/> per enabled connection. Load it together with the provider libraries
/// (<c>CL.Data.MySql</c>, <c>.PostgreSql</c>, <c>.SqlServer</c>, <c>.Sqlite</c>), which register their providers during Configure.
/// <list type="number">
/// <item><b>Configure</b>: registers the config sections.</item>
/// <item><b>Initialize</b>: creates the shared result cache and every database. A connection that cannot be created (missing
/// provider, invalid settings) is logged, reported by <see cref="HealthCheckAsync"/>, and returned as an error by <see cref="Db"/>;
/// the other connections still work.</item>
/// <item><b>Start</b>: warms the connection pools (<c>MinPoolSize</c>), starts replica probes, warm-query refresh and the cache
/// invalidation bus on one <see cref="DataScheduler"/>.</item>
/// <item><b>Stop</b>: stops the jobs and closes every pool.</item>
/// </list>
/// CL.Data events are published on the CodeLogic event bus under their concrete types. Config changes apply live: only
/// added, removed or changed connections are touched; a <c>data.cache</c> change rebuilds the cache and the databases.
/// </summary>
public sealed class DataLibrary : ILibrary, IConfigReloadable
{
    private readonly ConcurrentDictionary<string, Database> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Error> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _jobs = [];
    private readonly ConcurrentDictionary<string, DataCache> _ownCaches = new(StringComparer.OrdinalIgnoreCase);
    private LibraryContext? _context;
    private DataConfig _config = new();
    private DataCacheConfig _cacheConfig = new();
    private DataCache? _cache;
    private DataScheduler? _scheduler;
    private ICacheInvalidationBus? _bus;
    private bool _started;

    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Data",
        Name = "CL.Data",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Unified database framework: MySQL, PostgreSQL, SQL Server and SQLite through one API.",
        Author = "Zyntal.com",
        Tags = ["database", "orm", "cl.data"],
    };

    /// <summary>Clock for the databases, cache and scheduler (tests set a fake one before Initialize).</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The shared result cache (null when <c>data.cache.enabled</c> is false).</summary>
    public DataCache? Cache => _cache;

    /// <summary>The scheduler that runs CL.Data's background jobs.</summary>
    public DataScheduler? Scheduler => _scheduler;

    /// <summary>Names of the connections that are up.</summary>
    public IReadOnlyCollection<string> Connections => _databases.Keys.ToArray();

    /// <summary>
    /// The database for connection <paramref name="name"/> (null = <c>DefaultConnection</c>). Fails with
    /// <c>data.connection_unknown</c>, or with the error that kept the connection from being created.
    /// </summary>
    public Result<Database> Db(string? name = null)
    {
        name ??= _config.DefaultConnection;
        if (_databases.TryGetValue(name, out var db)) return db;
        return _failed.TryGetValue(name, out var error) ? error : DataErrors.ConnectionUnknown(name);
    }

    // ── Configure ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Configuration.Register<DataConfig>("data");
        context.Configuration.Register<DataCacheConfig>("data.cache");
        context.Localization.Register<CL.Data.Localization.DataStrings>();
        return Task.CompletedTask;
    }

    // ── Initialize ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        _config = context.Configuration.Get<DataConfig>();
        _cacheConfig = context.Configuration.Get<DataCacheConfig>();
        _scheduler = new DataScheduler(Time);
        _scheduler.JobCompleted += run =>
        {
            if (run.Error is { } error) context.Logger.Warning($"CL.Data job '{run.Name}' failed: {error.Message}");
        };
        BuildCache();
        foreach (var (name, connection) in _config.EnabledConnections) Open(name, connection);
        await EnsureDatabasesAsync(_databases.Keys).ConfigureAwait(false);
        if (_databases.Count == 0 && _failed.IsEmpty) context.Logger.Warning(Strings.NoConnections);
        else context.Logger.Info(Format(Strings.Initialized, _databases.Count));
    }

    /// <summary>Creates the database of each named connection that sets <c>CreateDatabaseIfMissing</c>; a failure takes that connection down.</summary>
    private async Task EnsureDatabasesAsync(IEnumerable<string> names)
    {
        foreach (var name in names.ToArray())
        {
            if (!_databases.TryGetValue(name, out var db) || !db.Config.CreateDatabaseIfMissing) continue;
            var ensured = await db.Admin.EnsureDatabaseAsync().ConfigureAwait(false);
            if (ensured.IsSuccess)
            {
                if (ensured.Value) _context?.Logger.Info($"CL.Data created database for connection '{name}'");
                continue;
            }
            _failed[name] = ensured.Error!;
            _databases.TryRemove(name, out _);
            await db.DisposeAsync().ConfigureAwait(false);
            _context?.Logger.Error($"CL.Data could not create the database of '{name}': {ensured.Error!.Message}");
        }
    }

    private static readonly CL.Data.Localization.DataStrings DefaultStrings = new();

    /// <summary>Localized messages (the defaults until CodeLogic has loaded the translations).</summary>
    private CL.Data.Localization.DataStrings Strings
    {
        get
        {
            try { return _context?.Localization.Get<CL.Data.Localization.DataStrings>() ?? DefaultStrings; }
            catch { return DefaultStrings; }
        }
    }

    private static string Format(string template, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);

    private void BuildCache() => _cache = _cacheConfig.Enabled ? new DataCache(_cacheConfig, Time) : null;

    private void Open(string name, DataConnectionConfig connection)
    {
        // A connection's CacheEnabled overrides data.cache.enabled: false opts it out of the shared cache, true opts it in
        // even when the cache is off for everyone else (it then gets a cache of its own with the data.cache limits).
        var cache = connection.CacheEnabled switch
        {
            false => null,
            true => _cache ?? (_ownCaches[name] = new DataCache(_cacheConfig, Time)),
            null => _cache,
        };
        var created = Database.Create(name, connection, new DatabaseOptions { Time = Time, Cache = cache, Events = Forward });
        if (created.IsFailure)
        {
            _failed[name] = created.Error!;
            _context?.Logger.Error(created.Error!.Code == DataErrors.ProviderMissingCode
                ? Format(Strings.ProviderMissing, name, connection.Provider)
                : $"CL.Data connection '{name}' could not be created: {created.Error.Message} {created.Error.Details}");
            return;
        }
        _failed.TryRemove(name, out _);
        _databases[name] = created.Value!;
    }

    /// <summary>Publishes a CL.Data event on the CodeLogic bus under its concrete type (the bus dispatches on the static type).</summary>
    private void Forward(DataEvent e)
    {
        if (_context?.Events is not { } bus) return;
        switch (e)
        {
            case QueryExecutedEvent x: bus.Publish(x); break;
            case SlowQueryEvent x: bus.Publish(x); _context.Logger.Warning(Format(Strings.SlowQuery, x.ConnectionId, (long)x.Duration.TotalMilliseconds, x.Sql)); break;
            case NPlusOneDetectedEvent x: bus.Publish(x); _context.Logger.Warning(Format(Strings.NPlusOne, x.ConnectionId, x.Count, x.QueryShape)); break;
            case CacheHitEvent x: bus.Publish(x); break;
            case CacheMissEvent x: bus.Publish(x); break;
            case SchemaSyncedEvent x:
                bus.Publish(x);
                if (x.Created) _context.Logger.Info(Format(Strings.TableCreated, x.Table));
                else if (x.ChangesApplied > 0) _context.Logger.Info(Format(Strings.TableAltered, x.Table, x.ChangesApplied));
                break;
            case SchemaDriftDetectedEvent x: bus.Publish(x); _context.Logger.Warning(Format(Strings.SchemaDrift, x.Table, string.Join("; ", x.PendingChanges))); break;
            case MigrationAppliedEvent x:
                bus.Publish(x);
                _context.Logger.Info(Format(x.RolledBack ? Strings.MigrationRolledBack : Strings.MigrationApplied, x.Version, x.Name));
                break;
            case SeedAppliedEvent x: bus.Publish(x); break;
            case RetentionPurgedEvent x: bus.Publish(x); _context.Logger.Info(Format(Strings.RetentionPurged, x.Rows, x.Table)); break;
            case BulkCopyCompletedEvent x: bus.Publish(x); break;
            case NodeHealthChangedEvent x:
                bus.Publish(x);
                _context.Logger.Warning(Format(Strings.NodeHealthChanged, x.ConnectionId, x.Node, x.Healthy ? "healthy" : "unhealthy", x.Message ?? ""));
                break;
            case CircuitStateChangedEvent x: bus.Publish(x); _context.Logger.Warning($"Circuit of '{x.ConnectionId}/{x.Node}' is now {x.State}"); break;
        }
    }

    // ── Start ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        await StartJobsAsync().ConfigureAwait(false);
        _started = true;
    }

    private async Task StartJobsAsync()
    {
        if (_scheduler is null) return;
        await Task.WhenAll(_databases.Values.Select(WarmAsync)).ConfigureAwait(false);
        foreach (var db in _databases.Values)
        {
            await db.ProbeReplicasAsync().ConfigureAwait(false);
            if (db.StartReplicaProbes(_scheduler) is { } probes) _jobs.Add(probes);
        }
        if (_cache is not null)
        {
            _cache.StartRefreshing(_scheduler);
            _bus = CreateBus();
            if (_bus is not null) _cache.UseBus(_bus);
        }
    }

    /// <summary>Opens <c>MinPoolSize</c> connections per node so the first requests do not pay for connecting.</summary>
    private async Task WarmAsync(Database db)
    {
        var count = Math.Min(db.Config.MinPoolSize, 32);
        if (count <= 0) return;
        foreach (var node in db.Replicas.Prepend(db.Primary))
        {
            try
            {
                var connections = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => node.DataSource.OpenConnectionAsync().AsTask())).ConfigureAwait(false);
                foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context?.Logger.Warning($"CL.Data could not warm '{db.Name}/{node.Name}': {ex.Message}");
            }
        }
    }

    private ICacheInvalidationBus? CreateBus()
    {
        switch (_cacheConfig.InvalidationBus)
        {
            case CacheInvalidationBus.Events when _context is not null:
                return new EventBusCacheBus(_context.Events);
            case CacheInvalidationBus.Database:
                var host = Db(_cacheConfig.BusConnection);
                if (host.IsFailure)
                {
                    _context?.Logger.Error($"CL.Data cache bus connection is not available: {host.Error!.Message}");
                    return null;
                }
                var bus = new DatabaseCacheBus(host.Value!);
                bus.Start(_scheduler!, TimeSpan.FromMilliseconds(_cacheConfig.BusPollIntervalMs));
                return bus;
            default:
                return null;
        }
    }

    // ── Live config changes ─────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart. <c>data</c>: removed and changed connections are closed (after in-flight
    /// commands had time to finish) and changed and added ones opened; untouched connections keep their pools.
    /// <c>data.cache</c>: the cache, the databases and the background jobs are rebuilt.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        _context = context;
        switch (change.NewValue)
        {
            case DataConfig current:
            {
                var diff = ConfigDiff.Keyed(Enabled(_config), Enabled(current), StringComparer.OrdinalIgnoreCase);
                _config = current;
                foreach (var name in diff.Removed.Keys.Concat(diff.Changed.Keys)) Retire(name);
                foreach (var (name, connection) in diff.Added.Concat(diff.Changed.Select(c => KeyValuePair.Create(c.Key, c.Value.New))))
                {
                    Open(name, connection);
                    await EnsureDatabasesAsync([name]).ConfigureAwait(false);
                    if (diff.Changed.ContainsKey(name)) context.Logger.Info(Format(Strings.ConnectionReloaded, name));
                    if (_started && _databases.TryGetValue(name, out var db))
                    {
                        await WarmAsync(db).ConfigureAwait(false);
                        if (db.StartReplicaProbes(_scheduler!) is { } probes) _jobs.Add(probes);
                    }
                }
                context.Logger.Info($"CL.Data connections: {diff.Added.Count} added, {diff.Changed.Count} changed, {diff.Removed.Count} removed");
                break;
            }
            case DataCacheConfig cache:
            {
                _cacheConfig = cache;
                await StopJobsAsync().ConfigureAwait(false);
                foreach (var name in _databases.Keys.ToArray()) Retire(name);
                _cache?.Dispose();
                BuildCache();
                foreach (var (name, connection) in _config.EnabledConnections) Open(name, connection);
                if (_started) await StartJobsAsync().ConfigureAwait(false);
                context.Logger.Info("CL.Data cache settings changed: cache and connections rebuilt");
                break;
            }
        }
    }

    private static Dictionary<string, DataConnectionConfig> Enabled(DataConfig config) =>
        config.EnabledConnections.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Removes a connection now and closes its pools once in-flight commands had time to finish.</summary>
    private void Retire(string name)
    {
        _failed.TryRemove(name, out _);
        _lastHealth.TryRemove(name, out _);
        _ownCaches.TryRemove(name, out var ownCache);
        if (!_databases.TryRemove(name, out var db)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(db.Config.CommandTimeoutSeconds, 30)), Time).ConfigureAwait(false);
            await db.DisposeAsync().ConfigureAwait(false);
            ownCache?.Dispose();
        });
    }

    // ── Stop, health ────────────────────────────────────────────────────────

    private async Task StopJobsAsync()
    {
        foreach (var job in _jobs) job.Dispose();
        _jobs.Clear();
        if (_bus is not null) await _bus.DisposeAsync().ConfigureAwait(false);
        _bus = null;
    }

    /// <inheritdoc />
    public async Task OnStopAsync()
    {
        _started = false;
        await StopJobsAsync().ConfigureAwait(false);
        if (_scheduler is not null) await _scheduler.DisposeAsync().ConfigureAwait(false);
        _scheduler = null;
        foreach (var db in _databases.Values) await db.DisposeAsync().ConfigureAwait(false);
        _databases.Clear();
        _cache?.Dispose();
        _cache = null;
        foreach (var own in _ownCaches.Values) own.Dispose();
        _ownCaches.Clear();
        _context?.Logger.Info(Strings.Stopped);
    }

    /// <summary>
    /// Pings every node of every connection. Unhealthy when a primary is unreachable; degraded when a replica is down or
    /// lagging, or a connection failed to start. Publishes <see cref="NodeHealthChangedEvent"/> when a node's state changes.
    /// </summary>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!_config.Enabled) return HealthStatus.Healthy("CL.Data is disabled");
        List<string> down = [];
        List<string> degraded = [.. _failed.Keys.Select(n => $"{n}: not started")];
        foreach (var db in _databases.Values)
        {
            foreach (var node in await db.CheckHealthAsync().ConfigureAwait(false))
            {
                var usable = node.IsUsable(db.Config.MaxReplicaLagMs);
                var key = $"{db.Name}/{node.Node}";
                if (_lastHealth.TryGetValue(key, out var before) && before != usable)
                    Forward(new NodeHealthChangedEvent(db.Name, db.Provider.Name, node.Node, Time.GetUtcNow(), usable,
                        usable ? null : node.Error ?? (node.LagMs is { } lag ? $"lag {lag} ms" : node.Circuit.ToString())));
                _lastHealth[key] = usable;
                if (usable) continue;
                (node.IsPrimary ? down : degraded).Add($"{key}: {node.Error ?? node.Circuit.ToString()}");
            }
        }
        if (down.Count > 0) return HealthStatus.Unhealthy("Primary unreachable: " + string.Join("; ", down));
        if (degraded.Count > 0) return HealthStatus.Degraded(string.Join("; ", degraded));
        return HealthStatus.Healthy($"{_databases.Count} connection(s) healthy");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var job in _jobs) job.Dispose();
        _cache?.Dispose();
    }
}

/// <summary>A cache invalidation published on the CodeLogic event bus (<see cref="CacheInvalidationBus.Events"/>).</summary>
/// <param name="Origin">The publishing bus instance; a node ignores its own invalidations.</param>
/// <param name="Tags">Invalidated tags.</param>
public sealed record CacheInvalidatedEvent(string Origin, IReadOnlyList<string> Tags) : IEvent;

/// <summary>Cache invalidations over the CodeLogic event bus: in-process, or across nodes when the host bridges the bus.</summary>
public sealed class EventBusCacheBus(IEventBus events) : ICacheInvalidationBus
{
    private readonly string _origin = Guid.NewGuid().ToString("N");
    private IEventSubscription? _subscription;

    /// <inheritdoc />
    public ValueTask PublishAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        events.Publish(new CacheInvalidatedEvent(_origin, tags));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void Subscribe(Action<IReadOnlyList<string>> onRemote)
    {
        _subscription?.Dispose();
        _subscription = events.Subscribe<CacheInvalidatedEvent>(e => { if (e.Origin != _origin) onRemote(e.Tags); });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }
}
