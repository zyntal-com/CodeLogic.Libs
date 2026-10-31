using System.Collections.Concurrent;
using System.Diagnostics;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Localization;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;
using Npgsql;

namespace CL.PostgreSQL;

/// <summary>
/// <b>CL.PostgreSQL</b> — CodeLogic library providing PostgreSQL database access with a fluent
/// LINQ query builder, automatic table synchronization, migrations, schema backups,
/// and multiple named database connections.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — Registers <see cref="DatabaseConfiguration"/> and <see cref="PostgreSQLStrings"/>.</description></item>
///   <item><description><b>Initialize</b> — Loads config, validates, creates <see cref="ConnectionManager"/> and <see cref="TableSyncService"/>. Tests the connection.</description></item>
///   <item><description><b>Start</b> — Runs a health check and logs server version.</description></item>
///   <item><description><b>Stop</b> — Closes connections and disposes resources.</description></item>
/// </list>
/// Config changes made while running (admin UI, reload, file watcher) are applied live per
/// database — see <see cref="OnConfigChangedAsync"/>.
/// </para>
/// </summary>
public sealed class PostgreSQLLibrary : ILibrary, IConfigReloadable
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.PostgreSQL",
        Name = "PostgreSQL Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "PostgreSQL with typed LINQ, SQL aggregation, projection pushdown, compiled materializers, covering indexes, retention, and a working cache.",
        Author = "Zyntal.com",
        Tags = ["postgresql", "postgres", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private DatabaseConfiguration? _config;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private DatabaseAdmin? _admin;
    private MigrationRunner? _migrationRunner;
    private PostgreSQLStrings? _strings;
    private bool _isEnabled;

    // Last aggregate health reported, so HealthChangedEvent fires on a transition rather
    // than on every poll. Null until the first check.
    private bool? _lastHealthy;
    private RetentionWorker? _retentionWorker;
    private readonly HashSet<(Type Type, string ConnectionId)> _registeredEntities = [];
    private readonly ConcurrentDictionary<string, SyncMode> _syncModeOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _registrationLock = new();

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<DatabaseConfiguration>("postgresql");
        context.Configuration.Register<CacheConfiguration>("postgresql.cache");
        context.Localization.Register<PostgreSQLStrings>();

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
        _strings = context.Localization.Get<PostgreSQLStrings>();

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

        // Look the config up through _config (not the local) so live config changes are seen.
        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events,
            connectionId => _config?.Databases.TryGetValue(connectionId, out var cfg) == true ? cfg : null);
        _admin = new DatabaseAdmin(_connectionManager, _tableSyncService, context.Logger);

        // Opt-in first-run setup: create the database before anything connects to it. The
        // check and the CREATE run on the connection's maintenance database.
        foreach (var kvp in enabledDbs.Where(k => k.Value.CreateDatabaseIfMissing))
        {
            var created = await _admin.CreateDatabaseAsync(kvp.Value.Database, connectionId: kvp.Key).ConfigureAwait(false);
            if (created.IsFailure)
                context.Logger.Warning($"[PostgreSQL] [{kvp.Key}] Could not create database \"{kvp.Value.Database}\": {created.Error?.Message}");
            else if (created.Value)
                context.Logger.Info($"[PostgreSQL] [{kvp.Key}] Created missing database \"{kvp.Value.Database}\"");
        }

        foreach (var kvp in enabledDbs)
        {
            context.Logger.Info($"[PostgreSQL] Connecting '{kvp.Key}' to {kvp.Value.Host}:{kvp.Value.Port}/{kvp.Value.Database}");

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
            context.Logger.Info($"[PostgreSQL] [{kvp.Key}] Sync mode: {kvp.Value.SyncMode}");
            await stateStore.EnsureStateTableAsync(kvp.Key, default).ConfigureAwait(false);
            await migrationTracker.EnsureMigrationsTableAsync(kvp.Key, default).ConfigureAwait(false);
        }

        // Apply cache configuration so queries see the right knobs from the first call.
        ApplyCacheConfiguration(context.Configuration.Get<CacheConfiguration>());

        // Wire the smart-cache pool registry so its background timers log to the
        // app's logger and so it can be cleanly disposed on stop.
        SmartCachePoolRegistry.Configure(context.Logger);

        // Bind observability to CodeLogic's event bus so QueryExecutedEvent /
        // CacheHit / CacheMiss / SlowQuery land on the app's existing bus.
        QueryObservability.Configure(context.Events, context.Logger);

        context.Logger.Info(_strings?.LibraryInitialized ?? "PostgreSQL library initialized");
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
                context.Logger.Info($"[PostgreSQL] [{connectionId}] Server: {serverInfo.Version} ({serverInfo.Comment})");
                context.Logger.Info($"[PostgreSQL] [{connectionId}] Database: {serverInfo.Database} on {serverInfo.Host}");
            }
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[PostgreSQL] Could not retrieve server info: {ex.Message}");
        }

        // Create the retention worker and hand it whatever is already registered. The
        // documented flows register entities AFTER StartAsync, so the worker's entry list
        // stays live and RegisterForRetention starts it (idempotently) the first time a
        // [RetainDays] entity shows up.
        RetentionWorker worker;
        lock (_registrationLock)
        {
            worker = new RetentionWorker(_connectionManager, context.Logger, []);
            foreach (var (type, connectionId) in _registeredEntities)
                worker.Register(type, connectionId);
            _retentionWorker = worker;
        }
        if (worker.HasWork) worker.Start();

        context.Logger.Info(_strings?.LibraryStarted ?? "PostgreSQL library started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        if (_retentionWorker is not null)
            await _retentionWorker.DisposeAsync().ConfigureAwait(false);
        _retentionWorker = null;
        lock (_registrationLock) _registeredEntities.Clear();

        // Drop the per-connection runtime knobs and the N+1 bookkeeping: both are
        // process-wide statics, and leaving them behind would leak a stopped library's
        // configuration into the next one started in the same process.
        Core.PostgreSqlRuntimeOptions.Reset();
        N1Detector.Reset();

        // Stop every smart-cache pool's background timer.
        await SmartCachePoolRegistry.DisposeAllAsync().ConfigureAwait(false);

        _migrationRunner = null;
        _tableSyncService = null;
        _admin = null;
        _connectionManager = null;
        _config = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "PostgreSQL library stopped");
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
            return HealthStatus.Healthy("PostgreSQL library is disabled");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("PostgreSQL library not initialized");

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
            _context?.Logger.Error($"[PostgreSQL] Health check exception: {ex.Message}", ex);
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

    /// <summary>
    /// Returns the <see cref="DatabaseAdmin"/>: create / drop / check databases and create /
    /// drop / truncate / check entity tables.
    /// </summary>
    public DatabaseAdmin Admin =>
        _admin ?? throw new InvalidOperationException(
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

    /// <summary>
    /// The loaded configuration instance. Internal: lets tests assert that a configured
    /// value actually reaches the object it is supposed to drive.
    /// </summary>
    internal DatabaseConfiguration? LoadedConfiguration => _config;

    /// <summary>
    /// The retention worker, once started. Internal: lets tests confirm that an entity
    /// registered after <c>StartAsync</c> is picked up, and drive a purge deterministically
    /// without waiting out the worker's 5-minute initial delay.
    /// </summary>
    internal RetentionWorker? Retention => _retentionWorker;

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
        => ExecuteRawAsync(sql, parameters, connectionId, "postgresql.query_failed", ct, async cmd =>
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
        => ExecuteRawAsync(sql, parameters, connectionId, "postgresql.execute_failed", ct,
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
        => ExecuteRawAsync<T?>(sql, parameters, connectionId, "postgresql.query_failed", ct, async cmd =>
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
        Func<NpgsqlCommand, Task<TOut>> run)
    {
        try
        {
            var threshold = _context?.Configuration.Get<DatabaseConfiguration>()
                .Databases.TryGetValue(connectionId, out var dbCfg) == true ? dbCfg.SlowQueryThresholdMs : 1000;
            var sw = Stopwatch.StartNew();

            var result = await ConnectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(connectionId);
                cmd.CommandText = sql;
                if (parameters is not null)
                    foreach (var kv in parameters)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await run(cmd).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            QueryObservability.RecordExecuted(connectionId, sql, sw.ElapsedMilliseconds, rowCount: -1, cacheHit: false);
            if (sw.ElapsedMilliseconds >= threshold)
                SlowQueryExplain.Record(
                    ConnectionManager, connectionId, sql, parameters, sw.ElapsedMilliseconds,
                    insideTransaction: false);

            return Result<TOut>.Success(result);
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"[PostgreSQL] Raw SQL failed: {ex.Message} — {sql}", ex);
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

    /// <summary>Creates a database on the connection's server. See <see cref="DatabaseAdmin.CreateDatabaseAsync"/>.</summary>
    public Task<Result<bool>> CreateDatabaseAsync(
        string name, string? encoding = null, string? collation = null, bool ifNotExists = true,
        string connectionId = "Default", CancellationToken ct = default) =>
        Admin.CreateDatabaseAsync(name, encoding, collation, ifNotExists, connectionId, ct);

    /// <summary>Drops a database on the connection's server. See <see cref="DatabaseAdmin.DropDatabaseAsync"/>.</summary>
    public Task<Result<bool>> DropDatabaseAsync(
        string name, bool ifExists = true, string connectionId = "Default", CancellationToken ct = default) =>
        Admin.DropDatabaseAsync(name, ifExists, connectionId, ct);

    /// <summary>Returns whether a database exists on the connection's server.</summary>
    public Task<Result<bool>> DatabaseExistsAsync(
        string name, string connectionId = "Default", CancellationToken ct = default) =>
        Admin.DatabaseExistsAsync(name, connectionId, ct);

    /// <summary>Creates the table for <typeparamref name="T"/> without altering an existing one. See <see cref="DatabaseAdmin.CreateTableAsync{T}"/>.</summary>
    public Task<Result<bool>> CreateTableAsync<T>(
        bool ifNotExists = true, string connectionId = "Default", CancellationToken ct = default) where T : class
    {
        RegisterEntity(typeof(T), connectionId);
        return Admin.CreateTableAsync<T>(ifNotExists, connectionId, ct);
    }

    /// <summary>Drops the table for <typeparamref name="T"/>. See <see cref="DatabaseAdmin.DropTableAsync{T}"/>.</summary>
    public Task<Result<bool>> DropTableAsync<T>(
        bool ifExists = true, bool cascade = false, string connectionId = "Default", CancellationToken ct = default)
        where T : class =>
        Admin.DropTableAsync<T>(ifExists, cascade, connectionId, ct);

    /// <summary>Removes every row from the table for <typeparamref name="T"/>. See <see cref="DatabaseAdmin.TruncateTableAsync{T}"/>.</summary>
    public Task<Result<bool>> TruncateTableAsync<T>(
        bool restartIdentity = true, bool cascade = false, string connectionId = "Default", CancellationToken ct = default)
        where T : class =>
        Admin.TruncateTableAsync<T>(restartIdentity, cascade, connectionId, ct);

    /// <summary>Returns whether the table for <typeparamref name="T"/> exists.</summary>
    public Task<Result<bool>> TableExistsAsync<T>(
        string connectionId = "Default", CancellationToken ct = default) where T : class =>
        Admin.TableExistsAsync<T>(connectionId, ct);

    /// <summary>
    /// Returns whether table <paramref name="tableName"/> (bare, in the connection's default
    /// schema, or <c>schema.table</c>) exists.
    /// </summary>
    public Task<Result<bool>> TableExistsAsync(
        string tableName, string connectionId = "Default", CancellationToken ct = default) =>
        Admin.TableExistsAsync(tableName, connectionId, ct);

    /// <summary>
    /// Records an entity type as owned by this library and, when it carries
    /// <see cref="RetainDaysAttribute"/>, hands it to the retention worker — starting the
    /// worker if this is the first entity with a retention policy. <c>Start()</c> is
    /// idempotent, so calling this on every sync is free once the loop is running.
    /// </summary>
    private void RegisterEntity(Type entityType, string connectionId)
    {
        RetentionWorker? worker;
        lock (_registrationLock)
        {
            _registeredEntities.Add((entityType, connectionId));
            worker = _retentionWorker;
        }

        if (worker is null) return;          // not started yet; OnStartAsync picks it up
        if (worker.Register(entityType, connectionId)) worker.Start();
    }

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
    /// <remarks>
    /// The runtime value survives live config changes to the connection, unless the change
    /// itself sets a different <see cref="PostgreSqlDatabaseConfig.SyncMode"/> — then the config wins.
    /// </remarks>
    public void SetSyncMode(SyncMode mode, string connectionId = "Default")
    {
        if (_config?.Databases.TryGetValue(connectionId, out var cfg) == true)
        {
            cfg.SyncMode = mode;
            _syncModeOverrides[connectionId] = mode;
            _context?.Logger.Info($"[PostgreSQL] [{connectionId}] Sync mode set to {mode} at runtime.");
        }
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart, touching only the databases that changed:
    /// a database whose connection settings changed is reconnected (its idle pool cleared,
    /// connection tested, schema-state tables ensured); one whose other settings changed is
    /// updated in place; added databases connect, removed or disabled ones disconnect, and
    /// untouched databases keep their connections. Throws when a database cannot be reached —
    /// the change stays saved and the remaining databases are still applied.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        switch (change.NewValue)
        {
            case DatabaseConfiguration databases:
                await ApplyDatabaseChangesAsync(context, change.GetOld<DatabaseConfiguration>(), databases, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case CacheConfiguration cache:
                ApplyCacheConfiguration(cache);
                context.Logger.Info("[PostgreSQL] Cache configuration applied");
                break;
        }
    }

    private async Task ApplyDatabaseChangesAsync(
        LibraryContext context,
        DatabaseConfiguration? previous,
        DatabaseConfiguration current,
        CancellationToken ct)
    {
        var diff = ConfigDiff.Keyed(EnabledDatabases(previous), EnabledDatabases(current), StringComparer.OrdinalIgnoreCase);

        ReapplySyncModeOverrides(previous, current);
        _config = current;

        if (!diff.HasChanges) return;

        var connections = _connectionManager;
        if (connections is null || _tableSyncService is null)
        {
            if (diff.Added.Count > 0)
                throw new InvalidOperationException(
                    $"{Manifest.Name} started with no enabled databases; restart to enable " +
                    $"'{string.Join("', '", diff.Added.Keys)}'.");
            return;
        }

        var unreachable = new List<string>();

        foreach (var id in diff.Removed.Keys)
        {
            await connections.UnregisterConfigurationAsync(id).ConfigureAwait(false);
            context.Logger.Info($"[PostgreSQL] [{id}] Removed — connection closed");
        }

        // RegisterConfiguration also refreshes the per-connection runtime knobs
        // (schema, timeouts, cache override, N+1 threshold, ...).
        foreach (var (id, databaseChange) in diff.Changed)
        {
            var reconnect = await connections.ReplaceConfigurationAsync(databaseChange.New, id).ConfigureAwait(false);
            if (!reconnect)
                context.Logger.Info($"[PostgreSQL] [{id}] Settings updated in place");
            else if (!await ConnectAsync(context, id, databaseChange.New, ct).ConfigureAwait(false))
                unreachable.Add(id);
        }

        foreach (var (id, database) in diff.Added)
        {
            connections.RegisterConfiguration(database, id);
            if (!await ConnectAsync(context, id, database, ct).ConfigureAwait(false))
                unreachable.Add(id);
        }

        if (unreachable.Count > 0)
            throw new InvalidOperationException(
                $"Could not connect to database(s) '{string.Join("', '", unreachable)}' with the new settings.");
    }

    /// <summary>Tests a (re)connected database and ensures its schema-state and migration tables.</summary>
    private async Task<bool> ConnectAsync(LibraryContext context, string id, PostgreSqlDatabaseConfig database, CancellationToken ct)
    {
        context.Logger.Info($"[PostgreSQL] Connecting '{id}' to {database.Host}:{database.Port}/{database.Database}");
        if (!await _connectionManager!.TestConnectionAsync(id, ct).ConfigureAwait(false))
        {
            context.Logger.Warning(string.Format(_strings?.ConnectionTestFailed ?? "Connection '{0}' test failed", id));
            return false;
        }

        await _tableSyncService!.GetSchemaStateStore().EnsureStateTableAsync(id, ct).ConfigureAwait(false);
        await _tableSyncService.GetMigrationTracker().EnsureMigrationsTableAsync(id, ct).ConfigureAwait(false);
        context.Logger.Info(string.Format(_strings?.ConnectionTestSuccess ?? "Connection '{0}' test successful", id));
        return true;
    }

    private void ReapplySyncModeOverrides(DatabaseConfiguration? previous, DatabaseConfiguration current)
    {
        foreach (var (id, mode) in _syncModeOverrides)
        {
            var configuredBefore = previous?.Databases.TryGetValue(id, out var before) == true ? before.SyncMode : (SyncMode?)null;
            if (current.Databases.TryGetValue(id, out var now) && now.SyncMode == configuredBefore)
                now.SyncMode = mode;
            else
                _syncModeOverrides.TryRemove(id, out _);
        }
    }

    private static Dictionary<string, PostgreSqlDatabaseConfig> EnabledDatabases(DatabaseConfiguration? config) =>
        config?.Databases.Where(d => d.Value.Enabled).ToDictionary(d => d.Key, d => d.Value)
        ?? [];

    private static void ApplyCacheConfiguration(CacheConfiguration cache) =>
        QueryCache.Configure(
            enabled: cache.Enabled,
            maxEntries: cache.MaxEntries,
            timeQuantizeSeconds: cache.TimeQuantizeSeconds,
            publishEvents: cache.PublishEvents,
            defaultTtlSeconds: cache.DefaultTtlSeconds);

    // ── Imperative migrations ────────────────────────────────────────────────

    /// <summary>Registers a single <see cref="IMigration"/> with the runner.</summary>
    public PostgreSQLLibrary RegisterMigration(IMigration migration)
    {
        Migrations.Register(migration);
        return this;
    }

    /// <summary>Registers every concrete <see cref="IMigration"/> in the given assembly.</summary>
    public PostgreSQLLibrary RegisterMigrationsFrom(System.Reflection.Assembly assembly)
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
        string? schemaName = null,
        string? backupFile = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        // null means "the schema this connection maps unqualified entities to", which is
        // its configured DefaultSchema — not the hard-coded "public".
        schemaName ??= Core.PostgreSqlRuntimeOptions.DefaultSchemaFor(connectionId);
        var result = await BackupManager.RestoreTableSchemaAsync(tableName, schemaName, backupFile, connectionId, ct)
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
    /// The pool's own timer keeps running; only the idle entry is retired.
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
    /// Tests the PostgreSQL connection for the given connection ID.
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
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.connection_failed"));
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _tableSyncService = null;
        _admin = null;
        _connectionManager = null;
    }
}
