using System.Collections.Concurrent;
using CL.SQLite.Models;
using CodeLogic.Core.Logging;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Manages pools of SQLite connections for multiple named database files.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string? _dataDirectory;
    private readonly ConcurrentDictionary<string, DatabaseState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    // Which pool handed out each live connection, so a connection is always returned to the
    // pool it came from, even when its connection ID was re-registered while it was in use.
    private readonly ConcurrentDictionary<SqliteConnection, DatabaseState> _owners =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public ConnectionManager(ILogger? logger = null, string? dataDirectory = null)
    {
        _logger = logger;
        _dataDirectory = dataDirectory;
    }

    public void RegisterConfiguration(string connectionId, SQLiteDatabaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var state = new DatabaseState(config, ResolveDatabasePath(config));
        EnsureDirectoryExists(state.DatabasePath);

        // Swap first, then retire: callers never see the ID missing, and connections still
        // in use from the old pool are closed when they are released instead of being reused.
        _states.TryGetValue(connectionId, out var existing);
        _states[connectionId] = state;
        existing?.Retire();

        _logger?.Debug($"[SQLite] Configuration registered for '{connectionId}' -> {state.DatabasePath}");
    }

    /// <summary>
    /// Replaces the configuration registered under <paramref name="connectionId"/>. When a setting
    /// that shapes the connections changed (database path, cache mode, WAL, foreign keys, connection
    /// timeout, pool size), the old pool is retired (idle connections closed, in-use ones closed on
    /// release) and new work opens fresh connections. Otherwise the configuration is swapped in
    /// place and pooled connections are kept. Other connection IDs are not touched.
    /// </summary>
    /// <returns><see langword="true"/> when a new pool was created (a reconnect is needed).</returns>
    public Task<bool> ReplaceConfigurationAsync(string connectionId, SQLiteDatabaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_states.TryGetValue(connectionId, out var current) && !NeedsNewPool(current, config))
        {
            current.Config = config;
            return Task.FromResult(false);
        }

        RegisterConfiguration(connectionId, config);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Removes the configuration registered under <paramref name="connectionId"/> and retires its
    /// pool: idle connections are closed now, in-use ones when they are released.
    /// </summary>
    /// <returns><see langword="false"/> when nothing was registered under that ID.</returns>
    public Task<bool> UnregisterConfigurationAsync(string connectionId)
    {
        if (!_states.TryRemove(connectionId, out var existing)) return Task.FromResult(false);
        existing.Retire();
        _logger?.Debug($"[SQLite] Configuration removed for '{connectionId}'");
        return Task.FromResult(true);
    }

    private bool NeedsNewPool(DatabaseState current, SQLiteDatabaseConfig config) =>
        !string.Equals(current.DatabasePath, ResolveDatabasePath(config), StringComparison.Ordinal)
        || current.Config.CacheMode != config.CacheMode
        || current.Config.UseWAL != config.UseWAL
        || current.Config.EnableForeignKeys != config.EnableForeignKeys
        || current.Config.ConnectionTimeoutSeconds != config.ConnectionTimeoutSeconds
        || Math.Max(1, current.Config.MaxPoolSize) != Math.Max(1, config.MaxPoolSize);

    public SQLiteDatabaseConfig GetConfiguration(string connectionId = "Default")
        => RequireState(connectionId).Config;

    public IEnumerable<string> GetConnectionIds() => _states.Keys;

    public string GetDatabasePath(string connectionId = "Default")
        => RequireState(connectionId).DatabasePath;

    public int GetActiveConnectionCount(string connectionId = "Default")
        => RequireState(connectionId).ActiveCount;

    public int GetPooledConnectionCount(string connectionId = "Default")
        => RequireState(connectionId).Pool.Count;

    public async Task<SqliteConnection> GetConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = RequireState(connectionId);

        // Bound the number of live connections to MaxPoolSize. Without this gate,
        // GetConnectionAsync would open an unbounded number of connections under load
        // (the pool only caps how many are *returned*, not how many are handed out).
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (state.Pool.TryPop(out var pooled))
            {
                if (pooled.IsValid() && !pooled.IsIdleTooLong())
                {
                    pooled.MarkInUse();
                    Interlocked.Increment(ref state.ActiveCount);
                    _owners[pooled.Connection] = state;
                    _logger?.Debug($"[SQLite] [{connectionId}] {SQLiteObservability.Strings.ConnectionReused}");
                    return pooled.Connection;
                }

                pooled.Connection.Dispose();
            }

            var conn = await CreateConnectionAsync(state, ct).ConfigureAwait(false);
            Interlocked.Increment(ref state.ActiveCount);
            _owners[conn] = state;
            return conn;
        }
        catch
        {
            // Acquisition failed (cancellation / open error) — give the permit back so
            // the slot isn't leaked, since no matching ReleaseConnectionAsync will run.
            state.Gate.Release();
            throw;
        }
    }

    public Task ReleaseConnectionAsync(
        SqliteConnection connection,
        string connectionId = "Default")
    {
        if (connection is null)
            return Task.CompletedTask;

        var owner = _owners.TryRemove(connection, out var issuedBy) ? issuedBy : null;

        if (_disposed)
        {
            connection.Dispose();
            return Task.CompletedTask;
        }

        var state = owner ?? RequireState(connectionId);
        Interlocked.Decrement(ref state.ActiveCount);

        if (!state.Retired && state.Pool.Count < state.Config.MaxPoolSize)
        {
            var pooled = new PooledConnection(connection);
            pooled.MarkAvailable();
            state.Pool.Push(pooled);
            _logger?.Debug($"[SQLite] [{connectionId}] {SQLiteObservability.Strings.ConnectionReleased}");
        }
        else
        {
            connection.Dispose();
        }

        // Release the live-connection slot acquired in GetConnectionAsync.
        state.Gate.Release();
        return Task.CompletedTask;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, Task<T>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(
        Func<SqliteConnection, Task> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
        }
    }

    public async Task<bool> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[SQLite] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var state in _states.Values)
            state.Dispose();

        _states.Clear();
        _owners.Clear();
    }

    private DatabaseState RequireState(string connectionId)
    {
        if (_states.TryGetValue(connectionId, out var state))
            return state;

        throw new InvalidOperationException(
            $"No SQLite configuration registered for connection ID '{connectionId}'.");
    }

    private string ResolveDatabasePath(SQLiteDatabaseConfig config)
    {
        if (_dataDirectory is not null && !Path.IsPathRooted(config.DatabasePath))
            return Path.Combine(_dataDirectory, config.DatabasePath);

        return config.DatabasePath;
    }

    private static void EnsureDirectoryExists(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string BuildConnectionString(SQLiteDatabaseConfig config, string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Microsoft.Data.Sqlite's own default is 30s, which is also this setting's default,
            // so a default configuration opens exactly the connection it opened before.
            DefaultTimeout = (int)Math.Clamp(config.ConnectionTimeoutSeconds, 1u, int.MaxValue)
        };

        // Default is left off the connection string entirely, so a default configuration opens
        // exactly the connection it opened before; the other two are requested explicitly.
        if (config.CacheMode is CacheMode.Shared or CacheMode.Private)
        {
            builder.Cache = config.CacheMode == CacheMode.Shared
                ? SqliteCacheMode.Shared
                : SqliteCacheMode.Private;
        }

        return builder.ToString();
    }

    private async Task<SqliteConnection> CreateConnectionAsync(DatabaseState state, CancellationToken ct)
    {
        var conn = new SqliteConnection(state.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (state.Config.UseWAL)
        {
            await using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Both directions are sent: Microsoft.Data.Sqlite turns foreign keys ON for every
        // connection it opens, so without the OFF form the false setting would do nothing.
        await using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = state.Config.EnableForeignKeys
                ? "PRAGMA foreign_keys=ON;"
                : "PRAGMA foreign_keys=OFF;";
            await fkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger?.Debug($"[SQLite] {string.Format(SQLiteObservability.Strings.ConnectionCreated, state.DatabasePath)}");
        return conn;
    }

    private sealed class DatabaseState : IDisposable
    {
        private volatile SQLiteDatabaseConfig _config;
        private volatile bool _retired;

        public DatabaseState(SQLiteDatabaseConfig config, string databasePath)
        {
            _config = config;
            DatabasePath = databasePath;
            ConnectionString = BuildConnectionString(config, databasePath);
            var max = Math.Max(1, config.MaxPoolSize);
            Gate = new SemaphoreSlim(max, max);
        }

        // Swappable for settings that don't shape the connections (see NeedsNewPool).
        public SQLiteDatabaseConfig Config
        {
            get => _config;
            set => _config = value;
        }

        public string DatabasePath { get; }
        public string ConnectionString { get; }

        // True once replaced or removed: released connections are closed, not pooled.
        public bool Retired => _retired;
        public ConcurrentStack<PooledConnection> Pool { get; } = new();
        public int ActiveCount;

        // Caps concurrent live connections (in-use + being created) at MaxPoolSize.
        public SemaphoreSlim Gate { get; }

        // Stops pooling and closes idle connections. The gate stays alive because connections
        // still in use release their slot on it when they come back.
        public void Retire()
        {
            _retired = true;
            DrainPool();
        }

        public void Dispose()
        {
            _retired = true;
            DrainPool();
            Gate.Dispose();
        }

        private void DrainPool()
        {
            while (Pool.TryPop(out var pooled))
                pooled.Connection.Dispose();

            // Release Microsoft.Data.Sqlite's own pooled handles for this database too, so a
            // retired file is not held open (it may be moved or deleted after a path change).
            using var probe = new SqliteConnection(ConnectionString);
            SqliteConnection.ClearPool(probe);
        }
    }

    private sealed class PooledConnection
    {
        private static readonly TimeSpan MaxIdleTime = TimeSpan.FromMinutes(5);

        public PooledConnection(SqliteConnection connection)
        {
            Connection = connection;
            _lastUsed = DateTime.UtcNow;
        }

        public SqliteConnection Connection { get; }
        private DateTime _lastUsed;
        private bool _inUse;

        public void MarkInUse()
        {
            _inUse = true;
            _lastUsed = DateTime.UtcNow;
        }

        public void MarkAvailable()
        {
            _inUse = false;
            _lastUsed = DateTime.UtcNow;
        }

        public bool IsValid() =>
            !_inUse &&
            Connection.State == System.Data.ConnectionState.Open;

        public bool IsIdleTooLong() =>
            DateTime.UtcNow - _lastUsed > MaxIdleTime;
    }
}
