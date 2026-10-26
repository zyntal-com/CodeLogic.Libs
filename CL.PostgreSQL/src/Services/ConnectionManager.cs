using System.Collections.Concurrent;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Manages PostgreSQL database connections — registration, pooling, health checking,
/// and transaction orchestration for multiple named connection IDs.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    // Per-connection-id configuration storage
    // Concurrent: RegisterConfiguration can run while other threads resolve a
    // connection, and a plain Dictionary is not safe under that mix.
    private readonly ConcurrentDictionary<string, PostgreSqlDatabaseConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    // Per-connection-id open connection counter
    private readonly ConcurrentDictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);

    // Per-physical-connection owning id, indexed by reference-identity hash of the
    // NpgsqlConnection instance. Lets CloseConnectionAsync resolve the id in O(1) without
    // comparing connection strings (which is both slow and wrong when two configs share
    // credentials).
    private readonly ConcurrentDictionary<NpgsqlConnection, string> _connectionOwners = new();

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a ConnectionManager.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="events">Optional event bus for publishing connection events.</param>
    public ConnectionManager(
        ILogger? logger = null,
        IEventBus? events = null)
    {
        _logger = logger;
        _events = events;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers (or replaces) a connection configuration under a given ID.
    /// <para>
    /// Also publishes the connection's runtime knobs (default schema, query timeout,
    /// default string size, N+1 threshold, EXPLAIN capture, cache override, backup
    /// directory) to <see cref="Core.PostgreSqlRuntimeOptions"/>, which is where the
    /// static query/metadata layers read them from. Every registration path — startup
    /// and runtime alike — goes through here, so a connection registered later is
    /// honoured identically.
    /// </para>
    /// </summary>
    public void RegisterConfiguration(PostgreSqlDatabaseConfig config, string connectionId = "Default")
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[connectionId] = config;
        Core.PostgreSqlRuntimeOptions.Register(connectionId, config);
        _logger?.Debug($"[PostgreSQL] Configuration registered for '{connectionId}' → {config.Host}:{config.Port}/{config.Database}");
    }

    /// <summary>
    /// Replaces the configuration registered under <paramref name="connectionId"/> (including its
    /// runtime knobs). When the connection string changed, idle pooled connections opened with the
    /// old settings are cleared so new work reconnects; connections already handed out finish
    /// normally. Other connection IDs are not touched.
    /// </summary>
    /// <returns><see langword="true"/> when the connection string changed (a reconnect is needed).</returns>
    public Task<bool> ReplaceConfigurationAsync(PostgreSqlDatabaseConfig config, string connectionId = "Default")
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs.TryGetValue(connectionId, out var previous);
        RegisterConfiguration(config, connectionId);

        var reconnect = previous is null || previous.BuildConnectionString() != config.BuildConnectionString();
        if (reconnect && previous is not null)
            ClearPool(previous);
        return Task.FromResult(reconnect);
    }

    /// <summary>
    /// Removes the configuration registered under <paramref name="connectionId"/>, its runtime
    /// knobs, and its idle pooled connections. Connections already handed out finish normally.
    /// </summary>
    /// <returns><see langword="false"/> when nothing was registered under that ID.</returns>
    public Task<bool> UnregisterConfigurationAsync(string connectionId)
    {
        if (!_configs.TryRemove(connectionId, out var previous)) return Task.FromResult(false);
        Core.PostgreSqlRuntimeOptions.Unregister(connectionId);
        ClearPool(previous);
        _logger?.Debug($"[PostgreSQL] Configuration removed for '{connectionId}'");
        return Task.FromResult(true);
    }

    private static void ClearPool(PostgreSqlDatabaseConfig config)
    {
        if (!config.EnablePooling) return;
        using var connection = new NpgsqlConnection(config.BuildConnectionString());
        NpgsqlConnection.ClearPool(connection);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a server-level connection: the connection ID's host
    /// and credentials, connected to its <see cref="PostgreSqlDatabaseConfig.MaintenanceDatabase"/>
    /// instead of its own database, so that database can be created or dropped. The
    /// connection is unpooled and closed afterwards.
    /// </summary>
    public async Task<TResult> ExecuteOnServerAsync<TResult>(
        Func<NpgsqlConnection, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var config = RequireConfig(connectionId);
        var builder = new NpgsqlConnectionStringBuilder(config.BuildConnectionString())
        {
            Database = string.IsNullOrWhiteSpace(config.MaintenanceDatabase) ? "postgres" : config.MaintenanceDatabase,
            Pooling = false
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await action(connection).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the idle pooled connections of every registered configuration that points at
    /// <paramref name="database"/>, so they do not hold the database open during a DROP
    /// DATABASE or go stale after it.
    /// </summary>
    internal void ClearPoolsForDatabase(string database)
    {
        // PostgreSQL database names are case-sensitive once created (quoted), so match exactly.
        foreach (var config in _configs.Values)
            if (string.Equals(config.Database, database, StringComparison.Ordinal))
                ClearPool(config);
    }

    /// <summary>Returns the configuration for the given connection ID, or null if not found.</summary>
    public PostgreSqlDatabaseConfig? GetConfiguration(string connectionId = "Default")
        => _configs.TryGetValue(connectionId, out var cfg) ? cfg : null;

    /// <summary>Returns true when a configuration exists for the given connection ID.</summary>
    public bool HasConfiguration(string connectionId = "Default")
        => _configs.ContainsKey(connectionId);

    /// <summary>Builds the ADO.NET connection string for the given connection ID.</summary>
    public string GetConnectionString(string connectionId = "Default")
        => RequireConfig(connectionId).BuildConnectionString();

    // ── Connection lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Opens and returns a new <see cref="NpgsqlConnection"/> for the given connection ID.
    /// Publishes a <see cref="DatabaseConnectedEvent"/> on success.
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var config = RequireConfig(connectionId);
        var connection = new NpgsqlConnection(config.BuildConnectionString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 1, (_, v) => v + 1);
            _connectionOwners[connection] = connectionId;
            _logger?.Debug($"[PostgreSQL] Connection opened for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseConnectedEvent(
                    connectionId, config.Host, config.Port, config.Database,
                    connection.PostgreSqlVersion.ToString(), DateTime.UtcNow))
                    .ConfigureAwait(false);
            }

            return connection;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] Failed to open connection for '{connectionId}': {ex.Message}", ex);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Closes the connection and publishes a <see cref="DatabaseDisconnectedEvent"/>.
    /// </summary>
    public async Task CloseConnectionAsync(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // O(1) id lookup from the owning map populated at open time.
        var connectionId = _connectionOwners.TryRemove(connection, out var id) ? id : "Default";

        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
            _logger?.Debug($"[PostgreSQL] Connection closed for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseDisconnectedEvent(connectionId, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tests connectivity for the given connection ID.
    /// Returns true on success, false on failure (does not throw).
    /// </summary>
    public async Task<bool> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand(connectionId);
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            await CloseConnectionAsync(conn).ConfigureAwait(false);
            _logger?.Info($"[PostgreSQL] Connection test passed for '{connectionId}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[PostgreSQL] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    // ── Higher-order helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Opens a connection, executes the given action, closes the connection, and returns the result.
    /// </summary>
    public async Task<TResult> ExecuteWithConnectionAsync<TResult>(
        Func<NpgsqlConnection, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var cfg = GetConfiguration(connectionId);
        var maxRetries = Math.Max(0, cfg?.TransientRetryCount ?? 0);
        var baseDelayMs = Math.Max(0, cfg?.TransientRetryBaseDelayMs ?? 50);

        // Each attempt uses a FRESH connection: a deadlock / lock-wait-timeout aborts the
        // server-side statement, so retrying on the same connection is wrong. Safe only for
        // single auto-commit statements — transaction-scoped work routes around this method
        // (it holds its own connection), so we never silently re-run half a transaction.
        for (var attempt = 0; ; attempt++)
        {
            var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
            try
            {
                return await action(conn).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (attempt < maxRetries && IsTransient(ex))
            {
                _logger?.Warning(
                    $"[PostgreSQL] Transient error {ex.SqlState} on '{connectionId}' (attempt {attempt + 1}/{maxRetries}); retrying: {ex.Message}");
                await DelayForRetryAsync(baseDelayMs, attempt, ct).ConfigureAwait(false);
            }
            finally
            {
                // Route through CloseConnectionAsync so the open count + owner map stay in sync.
                // Avoids the prior double-decrement where both this method and the await-using
                // path decremented _openCounts for the same physical connection.
                await CloseConnectionAsync(conn).ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// PostgreSQL errors that are safe to retry: the SQLSTATE class 40 transaction-rollback
    /// codes 40001 (serialization failure, raised by a serializable/repeatable-read conflict)
    /// and 40P01 (deadlock detected), which both mean the transaction rolled back cleanly and
    /// re-running it may succeed, plus 55P03 (lock not available), which means a
    /// <c>lock_timeout</c> wait expired and the lock may be free on the next attempt.
    /// </summary>
    private static bool IsTransient(PostgresException ex) =>
        ex.SqlState is "40001" or "40P01" or "55P03";

    private static async Task DelayForRetryAsync(int baseDelayMs, int attempt, CancellationToken ct)
    {
        if (baseDelayMs == 0) return;
        // Exponential backoff with jitter: base * 2^attempt ± up to 50%.
        var backoff = baseDelayMs * (1L << attempt);
        var jitter = (long)(backoff * (Random.Shared.NextDouble() - 0.5));
        var delay = Math.Clamp(backoff + jitter, 1, 30_000);
        await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a connection, begins a transaction, executes the action, commits, and returns the result.
    /// Rolls back automatically on exception.
    /// </summary>
    public async Task<TResult> ExecuteWithTransactionAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await action(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await CloseConnectionAsync(conn).ConfigureAwait(false);
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Server info ───────────────────────────────────────────────────────────

    /// <summary>Retrieves version and database metadata from the PostgreSQL server.</summary>
    public async Task<ServerInfo> GetServerInfoAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        return await ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand(connectionId);
            // version() is the full banner; the remaining MySQL spellings have no
            // PostgreSQL equivalent: @@version_comment -> the server's compile-time
            // settings, DATABASE() -> current_database(), @@hostname -> inet_server_addr(),
            // which is NULL over a Unix socket and so is coalesced.
            cmd.CommandText =
                "SELECT version(), " +
                "current_setting('server_version') , " +
                "current_database(), " +
                "COALESCE(host(inet_server_addr()), 'localhost')";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new ServerInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
            }
            return new ServerInfo(string.Empty, string.Empty, string.Empty, string.Empty);
        }, connectionId, ct).ConfigureAwait(false);
    }

    // ── Counters ──────────────────────────────────────────────────────────────

    /// <summary>Returns the current open connection count for the given connection ID.</summary>
    public int GetOpenConnectionCount(string connectionId = "Default")
        => _openCounts.TryGetValue(connectionId, out var v) ? v : 0;

    /// <summary>Returns a snapshot of open connection counts for all registered IDs.</summary>
    public IReadOnlyDictionary<string, int> GetAllConnectionCounts()
        => new Dictionary<string, int>(_openCounts, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the registered connection IDs.</summary>
    public IEnumerable<string> GetConnectionIds() => _configs.Keys;

    // ── Private helpers ───────────────────────────────────────────────────────

    private PostgreSqlDatabaseConfig RequireConfig(string connectionId)
    {
        if (_configs.TryGetValue(connectionId, out var cfg)) return cfg;
        throw new InvalidOperationException(
            $"No database configuration registered for connection ID '{connectionId}'. " +
            $"Call RegisterConfiguration first.");
    }

}

/// <summary>Basic PostgreSQL server metadata.</summary>
public record ServerInfo(string Version, string Comment, string Database, string Host);
