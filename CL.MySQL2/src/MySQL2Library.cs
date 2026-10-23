using System.Diagnostics;
using CL.MySQL2.Configuration;
using CL.MySQL2.Core;
using CL.MySQL2.Events;
using CL.MySQL2.Localization;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;
using MySqlConnector;

namespace CL.MySQL2;

/// <summary>
/// <b>CL.MySQL2</b> — CodeLogic library providing MySQL database access with a fluent
/// LINQ query builder, automatic table synchronization, migrations, schema backups,
/// and multiple named database connections.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — Registers <see cref="DatabaseConfiguration"/> and <see cref="MySQL2Strings"/>.</description></item>
///   <item><description><b>Initialize</b> — Loads config, validates, creates <see cref="ConnectionManager"/> and <see cref="TableSyncService"/>. Tests the connection.</description></item>
///   <item><description><b>Start</b> — Runs a health check and logs server version.</description></item>
///   <item><description><b>Stop</b> — Closes connections and disposes resources.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MySQL2Library : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.MySQL2",
        Name = "MySQL2 Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "MySQL with typed LINQ, SQL aggregation, projection pushdown, compiled materializers, covering indexes, retention, and a working cache.",
        Author = "Media2A",
        Tags = ["mysql", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private DatabaseConfiguration? _config;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private MigrationRunner? _migrationRunner;
    private MySQL2Strings? _strings;
    private bool _isEnabled;

    // Last aggregate health reported, so HealthChangedEvent fires on a transition rather
    // than on every poll. Null until the first check.
    private bool? _lastHealthy;
    private RetentionWorker? _retentionWorker;
    private readonly HashSet<(Type Type, string ConnectionId)> _registeredEntities = new();

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<DatabaseConfiguration>("mysql");
        context.Configuration.Register<CacheConfiguration>("mysql.cache");
        context.Localization.Register<MySQL2Strings>();

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<DatabaseConfiguration>();
        _config = config;
        _strings = context.Localization.Get<MySQL2Strings>();

        var enabledDbs = config.Databases
            .Where(kvp => kvp.Value.Enabled)
            .ToList();

        if (enabledDbs.Count == 0)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} has no enabled databases — skipping initialization.");
            return;
        }

        _isEnabled = true;

        // Validate configuration
        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException(
                $"{Manifest.Name} configuration is invalid: {errors}");
        }

        // Create services
        _connectionManager = new ConnectionManager(context.Logger, context.Events);

        foreach (var kvp in enabledDbs)
        {
            _connectionManager.RegisterConfiguration(kvp.Value, kvp.Key);
        }

        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events,
            connectionId => config.Databases.TryGetValue(connectionId, out var cfg) ? cfg : null);

        foreach (var kvp in enabledDbs)
        {
            context.Logger.Info($"[MySQL2] Connecting '{kvp.Key}' to {kvp.Value.Host}:{kvp.Value.Port}/{kvp.Value.Database}");

            var connected = await _connectionManager.TestConnectionAsync(kvp.Key, default).ConfigureAwait(false);
            if (connected)
            {
                context.Logger.Info(string.Format(
                    _strings?.ConnectionTestSuccess ?? "Connection '{0}' test successful",
                    kvp.Key));
            }
            else
            {
                context.Logger.Warning(string.Format(
                    _strings?.ConnectionTestFailed ?? "Connection '{0}' test failed",
                    kvp.Key));
            }
        }

        _migrationRunner = new MigrationRunner(
            _connectionManager, _tableSyncService.GetMigrationTracker(), context.Logger);

        // Ensure the schema-state sentinel + migration history tables exist on each enabled DB.
        var stateStore = _tableSyncService.GetSchemaStateStore();
        var migrationTracker = _tableSyncService.GetMigrationTracker();
        foreach (var kvp in enabledDbs)
        {
            context.Logger.Info($"[MySQL2] [{kvp.Key}] Sync mode: {kvp.Value.SyncMode}");
            await stateStore.EnsureStateTableAsync(kvp.Key, default).ConfigureAwait(false);
            await migrationTracker.EnsureMigrationsTableAsync(kvp.Key, default).ConfigureAwait(false);
        }

        // Apply cache configuration so queries see the right knobs from the first call.
        var cacheConfig = context.Configuration.Get<CacheConfiguration>();
        QueryCache.Configure(
            enabled: cacheConfig.Enabled,
            maxEntries: cacheConfig.MaxEntries,
            timeQuantizeSeconds: cacheConfig.TimeQuantizeSeconds,
            defaultTtlSeconds: cacheConfig.DefaultTtlSeconds,
            publishEvents: cacheConfig.PublishEvents);

        // Per-database overrides of the global cache switch, and the N+1 detector threshold.
        foreach (var kvp in enabledDbs)
        {
            QueryCache.SetConnectionOverride(kvp.Key, kvp.Value.CacheEnabledOverride);
            QueryObservability.ConfigureN1Detection(kvp.Key, kvp.Value.N1DetectorThreshold);
        }

        // DDL/SQL generation is static and not connection-scoped, so it takes its knobs from
        // the Default database when there is one, else the first enabled database.
        var generationSource = enabledDbs.FirstOrDefault(kvp => kvp.Key == "Default").Value
                               ?? enabledDbs[0].Value;
        SqlGenerationOptions.Configure(
            generationSource.DefaultStringSize,
            generationSource.MaxInClauseValues,
            context.Logger);

        // Wire the smart-cache pool registry so its background timers log to the
        // app's logger and so it can be cleanly disposed on stop.
        SmartCachePoolRegistry.Configure(context.Logger);

        // Bind observability to CodeLogic's event bus so QueryExecutedEvent /
        // CacheHit / CacheMiss / SlowQuery land on the app's existing bus.
        QueryObservability.Configure(context.Events, context.Logger);

        context.Logger.Info(_strings?.LibraryInitialized ?? "MySQL2 library initialized");
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");

        if (_connectionManager is null || !_isEnabled)
        {
            context.Logger.Info($"{Manifest.Name} started (disabled)");
            return;
        }

        try
        {
            foreach (var connectionId in _connectionManager.GetConnectionIds())
            {
                var serverInfo = await _connectionManager.GetServerInfoAsync(connectionId).ConfigureAwait(false);
                context.Logger.Info($"[MySQL2] [{connectionId}] Server: {serverInfo.Version} ({serverInfo.Comment})");
                context.Logger.Info($"[MySQL2] [{connectionId}] Database: {serverInfo.Database} on {serverInfo.Host}");
            }
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[MySQL2] Could not retrieve server info: {ex.Message}");
        }

        // The retention worker exists from start onwards, but most applications sync their
        // schema (and therefore register their entities) AFTER CodeLogic.StartAsync(). Its
        // entry list is therefore live: RegisterEntity feeds it later arrivals and starts the
        // loop the first time a [RetainDays] entity shows up. Start() is idempotent.
        _retentionWorker = new RetentionWorker(
            _connectionManager, context.Logger, []);
        foreach (var (type, connectionId) in _registeredEntities)
            _retentionWorker.TryRegister(type, connectionId);
        if (_retentionWorker.HasWork) _retentionWorker.Start();

        context.Logger.Info(_strings?.LibraryStarted ?? "MySQL2 library started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        if (_retentionWorker is not null)
            await _retentionWorker.DisposeAsync().ConfigureAwait(false);
        _retentionWorker = null;

        // Stop every smart-cache pool's background timer.
        await SmartCachePoolRegistry.DisposeAllAsync().ConfigureAwait(false);

        // Drop the process-wide registrations installed at init so a restart (or another
        // library instance) does not inherit this one's per-connection settings.
        QueryObservability.ResetN1Detection();
        if (_config is not null)
            foreach (var id in _config.Databases.Keys)
                QueryCache.SetConnectionOverride(id, null);

        _migrationRunner = null;
        _tableSyncService = null;
        _connectionManager = null;
        _config = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "MySQL2 library stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>

    /// <summary>
    /// Publishes <see cref="Events.HealthChangedEvent"/> when the aggregate health state
    /// differs from the last one reported. Fire-and-forget: a subscriber must never be able
    /// to fail a health check.
    /// </summary>
    private async Task ReportHealthAsync(bool healthy, string message)
    {
        if (_lastHealthy == healthy) return;
        _lastHealthy = healthy;
        if (_context?.Events is null) return;
        try
        {
            await _context.Events.PublishAsync(
                new Events.HealthChangedEvent("Default", healthy, message, DateTime.UtcNow))
                .ConfigureAwait(false);
        }
        catch
        {
            // A broken subscriber must not turn a healthy library into an unhealthy one.
        }
    }

    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!_isEnabled)
            return HealthStatus.Healthy("MySQL2 library is disabled");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("MySQL2 library not initialized");

        try
        {
            var connectionIds = _connectionManager.GetConnectionIds().ToList();
            var failedConnections = new List<string>();

            foreach (var connectionId in connectionIds)
            {
                var ok = await _connectionManager.TestConnectionAsync(connectionId).ConfigureAwait(false);
                if (!ok)
                    failedConnections.Add(connectionId);
            }

            var config = _context?.Configuration.Get<DatabaseConfiguration>();

            var data = new Dictionary<string, object>
            {
                ["totalDatabases"] = connectionIds.Count,
                ["failedDatabases"] = failedConnections.Count,
                ["connections"] = connectionIds.ToDictionary(
                    id => id,
                    id =>
                    {
                        var dbConfig = config?.Databases.TryGetValue(id, out var cfg) == true ? cfg : null;
                        return new Dictionary<string, object?>
                        {
                            ["host"] = dbConfig?.Host ?? "?",
                            ["database"] = dbConfig?.Database ?? "?",
                            ["openConnections"] = _connectionManager.GetOpenConnectionCount(id)
                        };
                    })
            };

            if (failedConnections.Count == 0)
            {
                await ReportHealthAsync(true, "all connections operational").ConfigureAwait(false);
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Healthy,
                    Message = _strings?.HealthCheckPassed ?? "Health check passed",
                    Data = data
                };
            }

            if (failedConnections.Count < connectionIds.Count)
            {
                await ReportHealthAsync(false, $"failed connections: {string.Join(", ", failedConnections)}").ConfigureAwait(false);
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Degraded,
                    Message = string.Format(
                        _strings?.HealthCheckFailed ?? "Health check failed: {0}",
                        string.Join(", ", failedConnections)),
                    Data = data
                };
            }

            await ReportHealthAsync(false, $"failed connections: {string.Join(", ", failedConnections)}").ConfigureAwait(false);
            return new HealthStatus
            {
                Status = HealthStatusLevel.Unhealthy,
                Message = string.Format(
                    _strings?.HealthCheckFailed ?? "Health check failed: {0}",
                    string.Join(", ", failedConnections)),
                Data = data
            };
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"[MySQL2] Health check exception: {ex.Message}", ex);
            return HealthStatus.FromException(ex);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the <see cref="ConnectionManager"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public ConnectionManager ConnectionManager =>
        _connectionManager ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="TableSyncService"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public TableSyncService TableSync =>
        _tableSyncService ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="MigrationTracker"/>.</summary>
    public MigrationTracker MigrationTracker =>
        _tableSyncService?.GetMigrationTracker() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="BackupManager"/>.</summary>
    public BackupManager BackupManager =>
        _tableSyncService?.GetBackupManager() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="SchemaStateStore"/> (the <c>__schema_state</c> sentinel).</summary>
    public SchemaStateStore SchemaState =>
        _tableSyncService?.GetSchemaStateStore() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="MigrationRunner"/> for explicit/imperative migrations.</summary>
    public MigrationRunner Migrations =>
        _migrationRunner ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>
    /// Creates a <see cref="Repository{T}"/> for the given entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="connectionId">The connection ID to use. Default: "Default".</param>
    public Repository<T> GetRepository<T>(string connectionId = "Default") where T : class, new()
    {
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        var dbConfig = config?.Databases.TryGetValue(connectionId, out var cfg) == true ? cfg : null;
        return new Repository<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            dbConfig?.SlowQueryThresholdMs ?? 1000,
            dbConfig?.MaxBatchInsertSize ?? 500,
            dbConfig?.DistinguishDuplicateKeyError ?? false);
    }

    /// <summary>
    /// Creates a fluent <see cref="QueryBuilder{T}"/> for the given entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="connectionId">The connection ID to use. Default: "Default".</param>
    public QueryBuilder<T> Query<T>(string connectionId = "Default") where T : class, new()
    {
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        return new QueryBuilder<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            config?.Databases.TryGetValue(connectionId, out var dbConfig) == true
                ? dbConfig.SlowQueryThresholdMs
                : 1000);
    }

    /// <summary>
    /// Creates a <see cref="Repository{T}"/> that runs inside an existing transaction.
    /// Every operation is enlisted on the scope's connection, so it commits or rolls back
    /// with it rather than on its own connection.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="transactionScope">The scope from <see cref="BeginTransactionAsync"/>.</param>
    public Repository<T> GetRepository<T>(TransactionScope transactionScope) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(transactionScope);
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        var dbConfig = config?.Databases.TryGetValue(transactionScope.ConnectionId, out var cfg) == true ? cfg : null;
        return new Repository<T>(
            ConnectionManager,
            _context?.Logger,
            transactionScope,
            dbConfig?.SlowQueryThresholdMs ?? 1000,
            dbConfig?.MaxBatchInsertSize ?? 500,
            dbConfig?.DistinguishDuplicateKeyError ?? false);
    }

    /// <summary>
    /// Creates a fluent <see cref="QueryBuilder{T}"/> that runs inside an existing transaction.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="transactionScope">The scope from <see cref="BeginTransactionAsync"/>.</param>
    public QueryBuilder<T> Query<T>(TransactionScope transactionScope) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(transactionScope);
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        return new QueryBuilder<T>(
            ConnectionManager,
            _context?.Logger,
            transactionScope,
            config?.Databases.TryGetValue(transactionScope.ConnectionId, out var dbConfig) == true
                ? dbConfig.SlowQueryThresholdMs
                : 1000);
    }

    /// <summary>
    /// The event bus this library publishes on. Internal: exposed so tests can assert the
    /// observability events actually fire, without widening the public surface.
    /// </summary>
    internal CodeLogic.Core.Events.IEventBus? Events => _context?.Events;

    // ── Raw SQL escape hatch ─────────────────────────────────────────────────

    /// <summary>
    /// Runs a raw SQL query and materializes each row into <typeparamref name="T"/> using the
    /// same compiled materializer as the typed query builder. Use named parameters
    /// (<c>@p</c>) and pass values via <paramref name="parameters"/> — never interpolate
    /// user input into <paramref name="sql"/>. Flows through observability and the transient
    /// retry policy; results are not cached.
    /// </summary>
    public Task<Result<List<T>>> SqlQueryAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class, new()
        => ExecuteRawAsync(sql, parameters, connectionId, "mysql.query_failed", ct, async cmd =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var items = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
            return items;
        });

    /// <summary>
    /// Runs a raw non-query statement (INSERT/UPDATE/DELETE/DDL) and returns the affected
    /// row count. Same parameterization and retry/observability rules as
    /// <see cref="SqlQueryAsync{T}"/>.
    /// </summary>
    public Task<Result<int>> ExecuteSqlAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string connectionId = "Default",
        CancellationToken ct = default)
        => ExecuteRawAsync(sql, parameters, connectionId, "mysql.execute_failed", ct,
            cmd => cmd.ExecuteNonQueryAsync(ct));

    /// <summary>
    /// Runs a raw query returning a single scalar value (the first column of the first row),
    /// converted to <typeparamref name="T"/>. Returns <c>default</c> when there are no rows.
    /// </summary>
    public Task<Result<T?>> SqlScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string connectionId = "Default",
        CancellationToken ct = default)
        => ExecuteRawAsync<T?>(sql, parameters, connectionId, "mysql.query_failed", ct, async cmd =>
        {
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null || raw is DBNull) return default;
            return (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T))!;
        });

    private async Task<Result<TOut>> ExecuteRawAsync<TOut>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        string connectionId,
        string errorCode,
        CancellationToken ct,
        Func<MySqlCommand, Task<TOut>> run)
    {
        try
        {
            var threshold = _context?.Configuration.Get<DatabaseConfiguration>()
                .Databases.TryGetValue(connectionId, out var dbCfg) == true ? dbCfg.SlowQueryThresholdMs : 1000;
            var sw = Stopwatch.StartNew();

            var result = await ConnectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                ConnectionManager.ApplyCommandTimeout(cmd, connectionId);
                cmd.CommandText = sql;
                if (parameters is not null)
                    foreach (var kv in parameters)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await run(cmd).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            QueryObservability.RecordExecuted(connectionId, sql, sw.ElapsedMilliseconds, rowCount: -1, cacheHit: false);
            if (sw.ElapsedMilliseconds >= threshold)
                QueryObservability.RecordSlow(ConnectionManager, connectionId, sql, sw.ElapsedMilliseconds, parameters);

            return Result<TOut>.Success(result);
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"[MySQL2] Raw SQL failed: {ex.Message} — {sql}", ex);
            return Result<TOut>.Failure(Error.FromException(ex, errorCode));
        }
    }

    /// <summary>
    /// Begins a new database transaction and returns a <see cref="TransactionScope"/>.
    /// </summary>
    public async Task<TransactionScope> BeginTransactionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await ConnectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new TransactionScope(connectionId, conn, tx, _context?.Logger);
    }

    /// <summary>
    /// Syncs the table schema for the specified entity type. The type is also registered
    /// with the library so that entity-level workers (e.g. retention purge) pick it up.
    /// </summary>
    public Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default") where T : class
    {
        RegisterEntity(typeof(T), connectionId);
        return TableSync.SyncTableAsync<T>(createBackup, connectionId);
    }

    /// <summary>
    /// Records an entity type as registered with the library and, when it carries
    /// <see cref="RetainDaysAttribute"/>, hands it to the retention worker and starts the
    /// worker if it is not already running. Registration happening after
    /// <c>CodeLogic.StartAsync()</c> is the normal case, so the worker cannot snapshot its
    /// entry list at construction time.
    /// </summary>
    private void RegisterEntity(Type entityType, string connectionId)
    {
        _registeredEntities.Add((entityType, connectionId));
        var worker = _retentionWorker;
        if (worker is null) return;            // pre-start: OnStartAsync picks it up
        if (worker.TryRegister(entityType, connectionId)) worker.Start();
    }

    /// <summary>
    /// Runs one retention pass immediately over every registered <see cref="RetainDaysAttribute"/>
    /// entity and returns the number of rows removed. The background loop waits five minutes
    /// after start and then runs daily; this is the operator-triggered equivalent.
    /// </summary>
    public Task<int> RunRetentionOnceAsync(CancellationToken ct = default) =>
        _retentionWorker?.RunOnceAsync(ct) ?? Task.FromResult(0);

    /// <summary>
    /// Syncs an entire set of entity types as one pass under a single cross-node lock, honoring
    /// the configured <see cref="SyncMode"/>. This is the recommended entry point for application
    /// startup: it applies the CRC fast-path per table and, in <see cref="SyncMode.Migration"/>,
    /// logs the "already current — switch back to Production" warning once nothing is left to do.
    /// </summary>
    /// <param name="entities">The entity types whose tables to reconcile.</param>
    public Task<Result<Dictionary<string, SyncResult>>> SyncSchemaAsync(params Type[] entities)
        => SyncSchemaAsync(entities, createBackup: true, connectionId: "Default");

    /// <summary>
    /// Syncs a set of entity types as one pass. See <see cref="SyncSchemaAsync(Type[])"/>.
    /// </summary>
    /// <param name="entities">The entity types whose tables to reconcile.</param>
    /// <param name="createBackup">Whether to back up schemas before altering existing tables.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<Result<Dictionary<string, SyncResult>>> SyncSchemaAsync(
        IEnumerable<Type> entities,
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var types = entities as IReadOnlyList<Type> ?? entities.ToList();
        foreach (var t in types)
            RegisterEntity(t, connectionId);
        return TableSync.SyncTablesAsync(types, createBackup, connectionId, ct);
    }

    /// <summary>
    /// Overrides the configured <see cref="SyncMode"/> for a connection at runtime, without editing
    /// the config file or restarting. Useful to flip <see cref="SyncMode.Migration"/> back to
    /// <see cref="SyncMode.Production"/> once a one-shot migration pass has completed.
    /// </summary>
    public void SetSyncMode(SyncMode mode, string connectionId = "Default")
    {
        if (_config?.Databases.TryGetValue(connectionId, out var cfg) == true)
        {
            cfg.SyncMode = mode;
            _context?.Logger.Info($"[MySQL2] [{connectionId}] Sync mode set to {mode} at runtime.");
        }
    }

    // ── Imperative migrations ────────────────────────────────────────────────

    /// <summary>Registers a single <see cref="IMigration"/> with the runner.</summary>
    public MySQL2Library RegisterMigration(IMigration migration)
    {
        Migrations.Register(migration);
        return this;
    }

    /// <summary>Registers every concrete <see cref="IMigration"/> in the given assembly.</summary>
    public MySQL2Library RegisterMigrationsFrom(System.Reflection.Assembly assembly)
    {
        Migrations.RegisterFrom(assembly);
        return this;
    }

    /// <summary>
    /// Applies all pending imperative migrations (caller-driven; not auto-run on start). See
    /// <see cref="MigrationRunner.MigrateAsync"/>.
    /// </summary>
    public Task<Result<MigrationRunResult>> MigrateAsync(
        string connectionId = "Default", CancellationToken ct = default)
        => Migrations.MigrateAsync(connectionId, ct);

    /// <summary>Returns the pending migration plan without applying anything.</summary>
    public Task<IReadOnlyList<MigrationPlanItem>> GetPendingMigrationsAsync(
        string connectionId = "Default", CancellationToken ct = default)
        => Migrations.GetPendingAsync(connectionId, ct);

    /// <summary>
    /// Rolls back applied migrations newer than <paramref name="target"/>, newest-first. See
    /// <see cref="MigrationRunner.RollbackAsync"/>.
    /// </summary>
    public Task<Result<MigrationRunResult>> RollbackAsync(
        MigrationVersion target, string connectionId = "Default", CancellationToken ct = default)
        => Migrations.RollbackAsync(target, connectionId, ct);

    /// <summary>
    /// Restores a table's schema from a <see cref="BackupManager"/> snapshot (drops and recreates
    /// the table from captured DDL) and clears its <c>__schema_state</c> row so the next sync pass
    /// reconciles it from scratch. Operator-driven and destructive — only DDL was backed up, so the
    /// table's rows are lost. When <paramref name="backupFile"/> is null the latest backup is used.
    /// </summary>
    public async Task<Result<bool>> RestoreSchemaAsync(
        string tableName,
        string? backupFile = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var result = await BackupManager.RestoreTableSchemaAsync(tableName, backupFile, connectionId, ct)
            .ConfigureAwait(false);
        if (result.IsSuccess)
            await SchemaState.RemoveStateAsync(tableName, connectionId, ct).ConfigureAwait(false);
        return result;
    }

    // ── Smart cache pools ────────────────────────────────────────────────────

    /// <summary>
    /// Registers a named <see cref="SmartCachePool"/>. Queries opt into the
    /// pool via <c>.SmartCache(name)</c>; the pool's background timer keeps
    /// every registered query's cache entry warm.
    /// <para>
    /// Idempotent — calling twice with the same name returns the existing
    /// pool unchanged (refresh interval is NOT updated on re-register).
    /// </para>
    /// </summary>
    /// <param name="name">Pool name (case-insensitive) referenced by <c>.SmartCache</c>.</param>
    /// <param name="refreshEvery">How often the pool re-runs every registered query.</param>
    /// <param name="maxIdleFires">
    /// Drop a registered entry after this many consecutive refresh ticks with
    /// no read. Default 10 — at a 30-second refresh interval, an unread entry
    /// is dropped after ~5 minutes, bounding cardinality on parameterized queries.
    /// </param>
    /// <param name="warmUp">
    /// Optional warm-up callback. When supplied, the pool runs it once as a
    /// fire-and-forget task right after registration so the cache is hot
    /// before the first user request hits it. Inside the callback, just call
    /// the queries that should be warm (with their normal
    /// <c>.SmartCache(name)</c> decoration) — they auto-register with the
    /// pool as usual. Exceptions are caught and logged; the pool stays
    /// lazy if warm-up fails.
    /// </param>
    public SmartCachePool RegisterCachePool(
        string name,
        TimeSpan refreshEvery,
        int maxIdleFires = 10,
        Func<Task>? warmUp = null) =>
        SmartCachePoolRegistry.Register(name, refreshEvery, maxIdleFires, warmUp);

    /// <summary>Triggers an out-of-schedule refresh for the named pool.</summary>
    public Task RefreshCachePoolAsync(string name, CancellationToken ct = default) =>
        SmartCachePoolRegistry.RefreshNowAsync(name, ct);

    /// <summary>Diagnostic snapshot of every registered smart-cache pool.</summary>
    public IReadOnlyList<SmartCachePoolStats> GetCachePoolStats() =>
        SmartCachePoolRegistry.GetStats();

    /// <summary>
    /// Diagnostic snapshot of the underlying <see cref="QueryCache"/>:
    /// total entries, per-table breakdown, table-version counters. Use
    /// from the admin UI to spot orphan accumulation or a hot table.
    /// </summary>
    public QueryCacheStats GetCacheStats() => QueryCache.GetStats();

    /// <summary>
    /// Tests the MySQL connection for the given connection ID.
    /// </summary>
    public async Task<Result<bool>> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var ok = await ConnectionManager.TestConnectionAsync(connectionId, ct).ConfigureAwait(false);
            return Result<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure(Error.FromException(ex, "mysql.connection_failed"));
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _tableSyncService = null;
        _connectionManager = null;
    }
}
