using System.Text.RegularExpressions;
using CL.MySQL2.Core;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.MySQL2.Services;

/// <summary>
/// Database- and table-level administration: create / drop / check databases, and create /
/// drop / truncate / check entity tables. Meant for first-run setup, demos, test fixtures
/// and resets — day-to-day schema changes belong to <see cref="TableSyncService"/>.
/// <para>
/// Database operations run on a server-level connection (no default database), so the
/// connection's user needs the matching server privileges. Every method returns a
/// <see cref="Result{T}"/> instead of throwing, except for invalid arguments.
/// </para>
/// </summary>
public sealed partial class DatabaseAdmin
{
    private readonly ConnectionManager _connectionManager;
    private readonly TableSyncService _tableSync;
    private readonly ILogger? _logger;

    public DatabaseAdmin(ConnectionManager connectionManager, TableSyncService tableSync, ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _tableSync = tableSync ?? throw new ArgumentNullException(nameof(tableSync));
        _logger = logger;
    }

    // Character set and collation names cannot be bound as parameters, so they are limited
    // to the characters real names use.
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SqlNameToken();

    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates database <paramref name="name"/>. <paramref name="charset"/> and
    /// <paramref name="collation"/> default to the connection's configured
    /// <c>CharacterSet</c> / <c>Collation</c>.
    /// </summary>
    /// <returns><see langword="true"/> when the database was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateDatabaseAsync(
        string name,
        string? charset = null,
        string? collation = null,
        bool ifNotExists = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ValidateDatabaseName(name);
        var config = _connectionManager.GetConfiguration(connectionId);
        charset ??= config?.CharacterSet;
        collation ??= config?.Collation;
        ValidateToken(charset, nameof(charset));
        ValidateToken(collation, nameof(collation));

        try
        {
            if (await DatabaseExistsCoreAsync(name, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mysql.database_exists", $"Database {MySqlDialect.Quote(name)} already exists."));
            }

            var sql = $"CREATE DATABASE IF NOT EXISTS {MySqlDialect.Quote(name)}"
                      + (string.IsNullOrEmpty(charset) ? "" : $" CHARACTER SET {charset}")
                      + (string.IsNullOrEmpty(collation) ? "" : $" COLLATE {collation}");
            await ExecuteOnServerAsync(sql, connectionId, ct).ConfigureAwait(false);
            _logger?.Info($"[MySQL2] Created database {MySqlDialect.Quote(name)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] CreateDatabaseAsync failed for {MySqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.create_database_failed"));
        }
    }

    /// <summary>
    /// Drops database <paramref name="name"/>, including the one this connection is configured
    /// for. Idle pooled connections to it are cleared first.
    /// </summary>
    /// <returns><see langword="true"/> when a database was dropped.</returns>
    public async Task<Result<bool>> DropDatabaseAsync(
        string name,
        bool ifExists = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ValidateDatabaseName(name);
        try
        {
            if (!await DatabaseExistsCoreAsync(name, connectionId, ct).ConfigureAwait(false))
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mysql.database_not_found", $"Database {MySqlDialect.Quote(name)} does not exist."));
            }

            await _connectionManager.ClearPoolsForDatabaseAsync(name).ConfigureAwait(false);
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {MySqlDialect.Quote(name)}", connectionId, ct)
                .ConfigureAwait(false);
            QueryCache.Clear();
            _logger?.Warning($"[MySQL2] Dropped database {MySqlDialect.Quote(name)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DropDatabaseAsync failed for {MySqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.drop_database_failed"));
        }
    }

    /// <summary>Returns whether database <paramref name="name"/> exists on the server.</summary>
    public async Task<Result<bool>> DatabaseExistsAsync(
        string name,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ValidateDatabaseName(name);
        try
        {
            return Result<bool>.Success(await DatabaseExistsCoreAsync(name, connectionId, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DatabaseExistsAsync failed for {MySqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    internal Task<bool> DatabaseExistsCoreAsync(string name, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteOnServerAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
            cmd.Parameters.AddWithValue("@name", name);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        }, connectionId, ct);

    // ── Tables ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the table for <typeparamref name="T"/> from its attributes, exactly as a first
    /// <c>SyncTableAsync</c> would, and records it in <c>__schema_state</c>. Unlike a sync it
    /// never alters an existing table.
    /// </summary>
    /// <returns><see langword="true"/> when the table was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateTableAsync<T>(
        bool ifNotExists = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var tableName = SchemaAnalyzer.GetTableName(entityType);
        try
        {
            if (await _tableSync.TableExistsAsync(tableName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mysql.table_exists", $"Table {MySqlDialect.Quote(tableName)} already exists."));
            }

            var ddl = _tableSync.Analyzer.GenerateCreateTable(entityType);
            await ExecuteAsync(ddl, connectionId, ct).ConfigureAwait(false);
            await _tableSync.GetSchemaStateStore().UpsertStateAsync(
                tableName, _tableSync.Analyzer.ComputeSchemaCrc(entityType), SchemaSyncStatus.Synced,
                _tableSync.ResolveMode(connectionId).ToString(), _tableSync.AppVersion, ddl, connectionId, ct)
                .ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Info($"[MySQL2] Created table {MySqlDialect.Quote(tableName)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] CreateTableAsync failed for {MySqlDialect.Quote(tableName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.create_table_failed"));
        }
    }

    /// <summary>
    /// Drops the table for <typeparamref name="T"/> and forgets its <c>__schema_state</c> row,
    /// so the next sync recreates it. Fails if another table's foreign key references it.
    /// </summary>
    /// <returns><see langword="true"/> when a table was dropped.</returns>
    public async Task<Result<bool>> DropTableAsync<T>(
        bool ifExists = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        try
        {
            if (!await _tableSync.TableExistsAsync(tableName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mysql.table_not_found", $"Table {MySqlDialect.Quote(tableName)} does not exist."));
            }

            await ExecuteAsync($"DROP TABLE IF EXISTS {MySqlDialect.Quote(tableName)}", connectionId, ct)
                .ConfigureAwait(false);
            await _tableSync.GetSchemaStateStore().RemoveStateAsync(tableName, connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Warning($"[MySQL2] Dropped table {MySqlDialect.Quote(tableName)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DropTableAsync failed for {MySqlDialect.Quote(tableName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.drop_table_failed"));
        }
    }

    /// <summary>
    /// Removes every row from the table for <typeparamref name="T"/> and resets its
    /// AUTO_INCREMENT counter (<c>TRUNCATE TABLE</c>). MySQL refuses to truncate a table that
    /// another table's foreign key references.
    /// </summary>
    public async Task<Result<bool>> TruncateTableAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        try
        {
            await ExecuteAsync($"TRUNCATE TABLE {MySqlDialect.Quote(tableName)}", connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] TruncateTableAsync failed for {MySqlDialect.Quote(tableName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.truncate_failed"));
        }
    }

    /// <summary>Returns whether the table for <typeparamref name="T"/> exists in the connection's database.</summary>
    public Task<Result<bool>> TableExistsAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
        => TableExistsAsync(SchemaAnalyzer.GetTableName(typeof(T)), connectionId, ct);

    /// <summary>Returns whether table <paramref name="tableName"/> exists in the connection's database.</summary>
    public async Task<Result<bool>> TableExistsAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        try
        {
            return Result<bool>.Success(
                await _tableSync.TableExistsAsync(tableName, connectionId, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] TableExistsAsync failed for {MySqlDialect.Quote(tableName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateDatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // MySQL's identifier limit; a longer name fails server-side with a less clear error.
        if (name.Length > 64)
            throw new ArgumentException("Database names are limited to 64 characters.", nameof(name));
    }

    private static void ValidateToken(string? value, string paramName)
    {
        if (!string.IsNullOrEmpty(value) && !SqlNameToken().IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid {paramName} name.", paramName);
    }

    private Task<bool> ExecuteOnServerAsync(string sql, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteOnServerAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct);

    private Task<bool> ExecuteAsync(string sql, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            _connectionManager.ApplyCommandTimeout(cmd, connectionId);
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct);
}
