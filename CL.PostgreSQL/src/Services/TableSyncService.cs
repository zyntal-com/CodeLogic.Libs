using System.Diagnostics;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Models;
using CodeLogic;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Synchronizes database table schema with entity class definitions.
/// Uses <see cref="SchemaAnalyzer"/> to detect and apply CREATE/ALTER TABLE statements.
/// </summary>
public sealed class TableSyncService
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly SchemaAnalyzer _analyzer;
    private readonly MigrationTracker _migrationTracker;
    private readonly BackupManager _backupManager;
    private readonly SchemaStateStore _stateStore;
    private readonly Func<string, PostgreSqlDatabaseConfig?>? _configLookup;
    private readonly string _appVersion;

    /// <param name="connectionManager">The connection manager to use for database access.</param>
    /// <param name="dataDirectory">Base data directory (for backup and migration storage).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="events">Optional event bus for publishing sync events.</param>
    /// <param name="configLookup">
    /// Optional delegate resolving per-connection config so the service can read
    /// <see cref="PostgreSqlDatabaseConfig.SchemaSyncLevel"/>. When null, sync operates at
    /// <see cref="SchemaSyncLevel.Safe"/>.
    /// </param>
    public TableSyncService(
        ConnectionManager connectionManager,
        string dataDirectory,
        ILogger? logger = null,
        IEventBus? events = null,
        Func<string, PostgreSqlDatabaseConfig?>? configLookup = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _events = events;
        _analyzer = new SchemaAnalyzer(logger);
        _migrationTracker = new MigrationTracker(connectionManager, logger);
        _backupManager = new BackupManager(connectionManager, dataDirectory, logger);
        _stateStore = new SchemaStateStore(connectionManager, logger);
        _configLookup = configLookup;
        _appVersion = CodeLogicEnvironment.AppVersion;
    }

    // Shared with DatabaseAdmin, which creates tables the same way a sync does.
    internal SchemaAnalyzer Analyzer => _analyzer;
    internal string AppVersion => _appVersion;
    internal SyncMode ResolveMode(string connectionId) =>
        _configLookup?.Invoke(connectionId)?.SyncMode ?? SyncMode.Production;

    private SchemaSyncLevel ResolveLevel(string connectionId) =>
        _configLookup?.Invoke(connectionId)?.EffectiveSyncLevel ?? SchemaSyncLevel.Safe;

    private static bool IsDestructive(string stmt) =>
        stmt.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase)
        || stmt.Contains("DROP INDEX", StringComparison.OrdinalIgnoreCase)
        // PostgreSQL drops foreign keys and UNIQUE constraints with ALTER TABLE ... DROP CONSTRAINT.
        || stmt.Contains("DROP CONSTRAINT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronizes the table schema for entity type <typeparamref name="T"/>.
    /// Creates the table if it doesn't exist, or alters it to add missing columns/indexes.
    /// </summary>
    /// <param name="createBackup">Whether to back up the current schema before making changes.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
        => SyncTableCoreAsync(typeof(T), createBackup, connectionId, ct);

    /// <summary>
    /// Non-generic core used by both the typed <see cref="SyncTableAsync{T}"/> and
    /// <see cref="SyncTablesAsync"/>. Avoids per-type <c>MakeGenericMethod.Invoke</c>
    /// reflection.
    /// </summary>
    internal async Task<Result<SyncResult>> SyncTableCoreAsync(
        Type entityType,
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default,
        bool lockHeld = false)
    {
        var tableName = SchemaAnalyzer.GetTableName(entityType);
        // An entity with no [Table(Schema = ...)] lands in the schema this connection
        // configured as its DefaultSchema, so the name has to be resolved per connection.
        var schemaName = SchemaAnalyzer.GetSchemaName(entityType, connectionId);
        // Tables are unique per schema, not per database, so the sentinel key must carry
        // the schema. Keying on the bare name let two same-named entities in different
        // schemas share one row and mask each other's CRC.
        var stateKey = $"{schemaName}.{tableName}";
        var sw = Stopwatch.StartNew();
        var operations = new List<string>();
        var errors = new List<string>();
        var cfg = _configLookup?.Invoke(connectionId);
        var level = cfg?.EffectiveSyncLevel ?? SchemaSyncLevel.Safe;
        var mode = cfg?.SyncMode ?? SyncMode.Production;

        SyncResult SkipResult(string? crc) => new()
        {
            Success = true,
            TableName = tableName,
            Operations = operations,
            Errors = errors,
            Duration = sw.Elapsed,
            Skipped = true,
            SchemaCrc = crc
        };

        try
        {
            if (level == SchemaSyncLevel.None)
            {
                operations.Add($"-- sync skipped (SchemaSyncLevel.None) for {PostgreSqlDialect.Qualify(schemaName, tableName)}");
                sw.Stop();
                return Result<SyncResult>.Success(SkipResult(null));
            }

            // ── CRC fast-path ── consult the sentinel before any pg_catalog diffing.
            var modelCrc = _analyzer.ComputeSchemaCrc(entityType, connectionId);
            var state = await _stateStore.GetStateAsync(stateKey, connectionId, ct).ConfigureAwait(false);

            // Skip only when the CRC matches, the row is Synced, AND the table really exists — a
            // single cheap existence check guards against the table being dropped out-of-band.
            async Task<bool> CanSkipAsync() =>
                state is not null
                && state.SchemaCrc == modelCrc
                && state.Status == SchemaSyncStatus.Synced
                && await TableExistsAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false);

            if (await CanSkipAsync().ConfigureAwait(false))
            {
                sw.Stop();
                _logger?.Debug($"[PostgreSQL] Table `{tableName}` unchanged (crc {modelCrc}) — skipped");
                return Result<SyncResult>.Success(SkipResult(modelCrc));
            }

            _logger?.Info($"[PostgreSQL] Syncing table `{tableName}` (mode: {mode}, level: {level}, crc {modelCrc})");

            // Work is needed — serialize across nodes unless the caller already holds the lock.
            SchemaSyncLock? ownLock = null;
            if (!lockHeld)
            {
                ownLock = await SchemaSyncLock.AcquireAsync(_connectionManager, connectionId, logger: _logger, ct: ct)
                    .ConfigureAwait(false);
                if (!ownLock.Acquired)
                {
                    await ownLock.DisposeAsync().ConfigureAwait(false);
                    sw.Stop();
                    _logger?.Warning($"[PostgreSQL] Skipped sync of `{tableName}` — another node holds the schema-sync lock.");
                    return Result<SyncResult>.Success(SkipResult(modelCrc));
                }
                // Re-read under the lock — a peer may have just reconciled this table.
                state = await _stateStore.GetStateAsync(stateKey, connectionId, ct).ConfigureAwait(false);
                if (await CanSkipAsync().ConfigureAwait(false))
                {
                    await ownLock.DisposeAsync().ConfigureAwait(false);
                    sw.Stop();
                    return Result<SyncResult>.Success(SkipResult(modelCrc));
                }
            }

            try
            {
                var tableExists = await TableExistsAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false);
                var status = SchemaSyncStatus.Synced;
                var driftPending = false;

                if (!tableExists)
                {
                    // CREATE TABLE
                    var createSql = _analyzer.GenerateCreateTable(entityType, connectionId);
                    await ExecuteSqlAsync(createSql, connectionId, ct).ConfigureAwait(false);
                    operations.Add($"CREATE TABLE {PostgreSqlDialect.Qualify(schemaName, tableName)}");
                    _logger?.Info($"[PostgreSQL] Created table `{tableName}`");
                }
                else
                {
                    // Backup before altering — always in the deliberate Migration mode; otherwise
                    // only when the caller asked. Developer's rolling drops skip the backup noise.
                    if (createBackup || mode == SyncMode.Migration)
                    {
                        var backupResult = await _backupManager.BackupTableSchemaAsync(tableName, schemaName, connectionId, ct)
                            .ConfigureAwait(false);
                        if (backupResult.IsFailure)
                            _logger?.Warning($"[PostgreSQL] Schema backup failed for `{tableName}`: {backupResult.Error?.Message}");
                    }

                    // ALTER TABLE as needed at the mode's level.
                    var alterStatements = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
                        await _analyzer.GenerateAlterStatementsAsync(entityType, conn, level, ct, connectionId).ConfigureAwait(false),
                        connectionId, ct).ConfigureAwait(false);

                    foreach (var stmt in alterStatements)
                    {
                        await ExecuteSqlAsync(stmt, connectionId, ct).ConfigureAwait(false);
                        operations.Add(stmt);
                        _logger?.Debug($"[PostgreSQL] Applied: {stmt}");
                    }

                    // Production (Safe) never drops — detect whether a destructive change is still
                    // owed so a later Migration pass knows to act even though the CRC now matches.
                    if (level < SchemaSyncLevel.Full)
                    {
                        var fullStatements = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
                            await _analyzer.GenerateAlterStatementsAsync(entityType, conn, SchemaSyncLevel.Full, ct, connectionId).ConfigureAwait(false),
                            connectionId, ct).ConfigureAwait(false);
                        if (fullStatements.Any(IsDestructive))
                        {
                            driftPending = true;
                            status = SchemaSyncStatus.DriftPending;
                            _logger?.Warning(
                                $"[PostgreSQL] Table {PostgreSqlDialect.Qualify(schemaName, tableName)}: destructive change(s) deferred under {mode} mode — run Migration mode to complete.");
                        }
                    }

                    if (operations.Count > 0)
                        _logger?.Info($"[PostgreSQL] Updated table `{tableName}` ({operations.Count} changes)");
                    else
                        _logger?.Debug($"[PostgreSQL] Table `{tableName}` is up to date");
                }

                // Record the new CRC + status — only after DDL succeeded. PostgreSQL auto-commits DDL,
                // so a half-applied table is intentionally left without an updated CRC and retried.
                await _stateStore.UpsertStateAsync(
                    stateKey, modelCrc, status, mode.ToString(), _appVersion,
                    _analyzer.GenerateCreateTable(entityType, connectionId), connectionId, ct).ConfigureAwait(false);

                sw.Stop();
                var syncResult = new SyncResult
                {
                    Success = true,
                    TableName = tableName,
                    Operations = operations,
                    Errors = errors,
                    Duration = sw.Elapsed,
                    SchemaCrc = modelCrc,
                    DriftPending = driftPending
                };

                if (_events is not null)
                {
                    await _events.PublishAsync(new TableSyncedEvent(
                        connectionId, schemaName, tableName, !tableExists, operations, sw.Elapsed))
                        .ConfigureAwait(false);
                }

                return Result<SyncResult>.Success(syncResult);
            }
            finally
            {
                if (ownLock is not null)
                    await ownLock.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error($"[PostgreSQL] Table sync failed for `{tableName}`: {ex.Message}", ex);
            errors.Add(ex.Message);

            return Result<SyncResult>.Failure(
                Error.Internal("postgresql.sync_failed", $"Table synchronization failed for {tableName}", ex.Message));
        }
    }

    /// <summary>
    /// Synchronizes multiple entity types as one pass. Brackets the whole pass in a single
    /// cross-node <see cref="SchemaSyncLock"/>, applies the CRC fast-path per table, and — in
    /// <see cref="SyncMode.Migration"/> — emits the "already current, switch back to Production"
    /// warning when nothing needed doing.
    /// </summary>
    /// <param name="entityTypes">Entity types to sync.</param>
    /// <param name="createBackup">Whether to back up schemas before altering.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<Dictionary<string, SyncResult>>> SyncTablesAsync(
        IEnumerable<Type> entityTypes,
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, SyncResult>(StringComparer.OrdinalIgnoreCase);
        var types = entityTypes as IReadOnlyList<Type> ?? entityTypes.ToList();
        var cfg = _configLookup?.Invoke(connectionId);
        var mode = cfg?.SyncMode ?? SyncMode.Production;
        var level = cfg?.EffectiveSyncLevel ?? SchemaSyncLevel.Safe;

        // Hold one lock for the whole pass (DDL serialized across nodes). Skip locking entirely
        // when sync is fully disabled — each table just returns a no-op skip.
        SchemaSyncLock? passLock = null;
        var lockHeld = false;
        if (level != SchemaSyncLevel.None)
        {
            passLock = await SchemaSyncLock.AcquireAsync(_connectionManager, connectionId, logger: _logger, ct: ct)
                .ConfigureAwait(false);
            lockHeld = passLock.Acquired;
            if (!lockHeld)
                _logger?.Warning("[PostgreSQL] Schema-sync pass could not obtain the lock — another node is syncing; unchanged tables will be skipped.");
        }

        try
        {
            var anyWork = false;
            foreach (var entityType in types)
            {
                var tableName = SchemaAnalyzer.GetTableName(entityType);
                try
                {
                    var result = await SyncTableCoreAsync(entityType, createBackup, connectionId, ct, lockHeld: lockHeld)
                        .ConfigureAwait(false);

                    var sr = result.IsSuccess
                        ? result.Value!
                        : new SyncResult
                        {
                            Success = false,
                            TableName = tableName,
                            Errors = [result.Error?.Message ?? "Unknown error"]
                        };
                    results[tableName] = sr;

                    if (sr.Success && (sr.Operations.Count > 0 || sr.DriftPending))
                        anyWork = true;
                }
                catch (Exception ex)
                {
                    _logger?.Error($"[PostgreSQL] Failed to sync `{tableName}`: {ex.Message}", ex);
                    results[tableName] = new SyncResult
                    {
                        Success = false,
                        TableName = tableName,
                        Errors = [ex.Message]
                    };
                }
            }

            // Migration mode self-disables: once everything matches, nag the operator to revert.
            if (mode == SyncMode.Migration && lockHeld && !anyWork)
            {
                _logger?.Warning(
                    "[PostgreSQL] Migration mode: schema already current — set SyncMode back to Production.");
            }

            return Result<Dictionary<string, SyncResult>>.Success(results);
        }
        finally
        {
            if (passLock is not null)
                await passLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Returns the <see cref="MigrationTracker"/> used by this service.</summary>
    public MigrationTracker GetMigrationTracker() => _migrationTracker;

    /// <summary>Returns the <see cref="SchemaStateStore"/> (the <c>__schema_state</c> sentinel).</summary>
    public SchemaStateStore GetSchemaStateStore() => _stateStore;

    /// <summary>Returns the <see cref="BackupManager"/> used by this service.</summary>
    public BackupManager GetBackupManager() => _backupManager;

    // ── Private helpers ────────────────────────────────────────────────────────

    internal async Task<bool> TableExistsAsync(
        string tableName,
        string schemaName,
        string connectionId,
        CancellationToken ct)
    {
        return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            // to_regclass is a single catalog lookup and, given a schema-qualified name,
            // is unambiguous regardless of search_path. The name is passed quoted, as the DDL
            // creates it: unquoted, to_regclass would fold a mixed-case name to lower case.
            cmd.CommandText = "SELECT to_regclass(@tbl) IS NOT NULL";
            cmd.Parameters.AddWithValue("@tbl", PostgreSqlDialect.Qualify(schemaName, tableName));
            var exists = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return exists is bool found && found;
        }, connectionId, ct).ConfigureAwait(false);
    }

    internal async Task ExecuteSqlAsync(string sql, string connectionId, CancellationToken ct)
    {
        await _connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct).ConfigureAwait(false);
    }
}
