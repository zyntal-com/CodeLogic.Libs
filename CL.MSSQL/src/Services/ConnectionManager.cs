using System.Collections.Concurrent;
using CL.MSSQL.Configuration;
using CL.MSSQL.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// Manages SQL Server database connections — registration, pooling, health checking,
/// and transaction orchestration for multiple named connection IDs.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    // Per-connection-id configuration storage
    // Concurrent: RegisterConfiguration can run while other threads resolve a
    // connection, and a plain Dictionary is not safe under that mix.
    private readonly ConcurrentDictionary<string, SqlServerDatabaseConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    // Per-connection-id open connection counter
    private readonly ConcurrentDictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);

    // Per-physical-connection owning id, indexed by reference-identity hash of the
    // SqlConnection instance. Lets CloseConnectionAsync resolve the id in O(1) without
    // comparing connection strings (which is both slow and wrong when two configs share
    // credentials).
    private readonly ConcurrentDictionary<SqlConnection, string> _connectionOwners = new();

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
    /// </summary>
    public void RegisterConfiguration(SqlServerDatabaseConfig config, string connectionId = "Default")
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[connectionId] = config;
        _logger?.Debug($"[MSSQL] Configuration registered for '{connectionId}'");
    }

    /// <summary>
    /// Replaces the configuration registered under <paramref name="connectionId"/>. When the
    /// connection string changed, idle pooled connections opened with the old settings are
    /// cleared so new work reconnects; connections already handed out finish normally.
    /// Other connection IDs are not touched.
    /// </summary>
    /// <returns><see langword="true"/> when the connection string changed (a reconnect is needed).</returns>
    public Task<bool> ReplaceConfigurationAsync(SqlServerDatabaseConfig config, string connectionId = "Default")
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
    /// Removes the configuration registered under <paramref name="connectionId"/> and clears its
    /// idle pooled connections. Connections already handed out finish normally.
    /// </summary>
    /// <returns><see langword="false"/> when nothing was registered under that ID.</returns>
    public Task<bool> UnregisterConfigurationAsync(string connectionId)
    {
        if (!_configs.TryRemove(connectionId, out var previous)) return Task.FromResult(false);
        ClearPool(previous);
        _logger?.Debug($"[MSSQL] Configuration removed for '{connectionId}'");
        return Task.FromResult(true);
    }

    private static void ClearPool(SqlServerDatabaseConfig config)
    {
        if (!config.EnablePooling) return;
        using var connection = new SqlConnection(config.BuildConnectionString());
        SqlConnection.ClearPool(connection);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a server-level connection: the connection ID's
    /// server and credentials with <c>master</c> as the current database, so the configured
    /// database itself can be created or dropped. The connection is unpooled and closed
    /// afterwards.
    /// </summary>
    public async Task<TResult> ExecuteOnServerAsync<TResult>(
        Func<SqlConnection, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var builder = new SqlConnectionStringBuilder(RequireConfig(connectionId).BuildConnectionString())
        {
            InitialCatalog = "master",
            Pooling = false
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await action(connection).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the idle pooled connections of every registered configuration that points at
    /// <paramref name="database"/>, so a following DROP DATABASE does not leave them stale.
    /// </summary>
    internal void ClearPoolsForDatabase(string database)
    {
        foreach (var config in _configs.Values)
            if (string.Equals(config.EffectiveDatabase, database, StringComparison.OrdinalIgnoreCase))
                ClearPool(config);
    }

    /// <summary>Returns the configuration for the given connection ID, or null if not found.</summary>
    public SqlServerDatabaseConfig? GetConfiguration(string connectionId = "Default")
        => _configs.TryGetValue(connectionId, out var cfg) ? cfg : null;

    /// <summary>Returns true when a configuration exists for the given connection ID.</summary>
    public bool HasConfiguration(string connectionId = "Default")
        => _configs.ContainsKey(connectionId);

    /// <summary>Builds the ADO.NET connection string for the given connection ID.</summary>
    public string GetConnectionString(string connectionId = "Default")
        => RequireConfig(connectionId).BuildConnectionString();

    /// <summary>
    /// The command timeout, in whole seconds, that library-issued query commands should carry
    /// for this connection id — <see cref="SqlServerDatabaseConfig.QueryTimeoutMs"/> rounded up.
    /// Returns null when the connection is unknown or the timeout is set to 0, in which case the
    /// command keeps whatever the connection string's <c>Command Timeout</c> gave it.
    /// </summary>
    internal int? QueryCommandTimeoutSeconds(string connectionId)
    {
        var ms = GetConfiguration(connectionId)?.QueryTimeoutMs ?? 0;
        return ms <= 0 ? null : Math.Max(1, (int)Math.Ceiling(ms / 1000.0));
    }

    // ── Connection lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Opens and returns a new <see cref="SqlConnection"/> for the given connection ID.
    /// Publishes a <see cref="DatabaseConnectedEvent"/> on success.
    /// </summary>
    public async Task<SqlConnection> OpenConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var config = RequireConfig(connectionId);
        var connection = new SqlConnection(config.BuildConnectionString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 1, (_, v) => v + 1);
            _connectionOwners[connection] = connectionId;
            _logger?.Debug($"[MSSQL] Connection opened for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseConnectedEvent(
                    connectionId, config.Host, config.Port, config.Database, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }

            return connection;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] Failed to open connection for '{connectionId}': {ex.Message}", ex);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Closes the connection and publishes a <see cref="DatabaseDisconnectedEvent"/>.
    /// </summary>
    public async Task CloseConnectionAsync(SqlConnection connection)
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
            _logger?.Debug($"[MSSQL] Connection closed for '{connectionId}'");

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
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            await CloseConnectionAsync(conn).ConfigureAwait(false);
            _logger?.Info($"[MSSQL] Connection test passed for '{connectionId}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[MSSQL] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    // ── Higher-order helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Opens a connection, executes the given action, closes the connection, and returns the result.
    /// </summary>
    public async Task<TResult> ExecuteWithConnectionAsync<TResult>(
        Func<SqlConnection, Task<TResult>> action,
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
            SqlConnection? conn = null;
            try
            {
                conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
                return await action(conn).ConfigureAwait(false);
            }
            catch (SqlException ex) when (attempt < maxRetries && IsTransient(ex))
            {
                _logger?.Warning(
                    $"[MSSQL] Transient error {ex.Number} on '{connectionId}' (attempt {attempt + 1}/{maxRetries}); retrying: {ex.Message}");
                await DelayForRetryAsync(baseDelayMs, attempt, ct).ConfigureAwait(false);
            }
            finally
            {
                if (conn is not null)
                {
                    // Route through CloseConnectionAsync so the open count + owner map stay in sync.
                    await CloseConnectionAsync(conn).ConfigureAwait(false);
                    await conn.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static readonly HashSet<int> TransientNumbers =
    [
        20, 64, 233, 1205, 1222, 4060, 10928, 10929, 40197, 40501, 40613,
        49918, 49919, 49920, 10053, 10054, 10060, 11001
    ];

    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(error => IsTransientNumber(error.Number));

    internal static bool IsTransientNumber(int number) => TransientNumbers.Contains(number);

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
        Func<SqlConnection, SqlTransaction, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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

    /// <summary>Retrieves version and database metadata from the SQL Server server.</summary>
    public async Task<ServerInfo> GetServerInfoAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        return await ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')), CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), DB_NAME(), CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'))";
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

    private SqlServerDatabaseConfig RequireConfig(string connectionId)
    {
        if (_configs.TryGetValue(connectionId, out var cfg)) return cfg;
        throw new InvalidOperationException(
            $"No database configuration registered for connection ID '{connectionId}'. " +
            $"Call RegisterConfiguration first.");
    }

}

/// <summary>Basic SQL Server server metadata.</summary>
public record ServerInfo(string Version, string Comment, string Database, string Host);
