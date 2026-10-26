using System.Text.RegularExpressions;
using CL.MSSQL.Core;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// Database- and table-level administration: create / drop / check databases, and create /
/// drop / truncate / check entity tables. Meant for first-run setup, demos, test fixtures
/// and resets — day-to-day schema changes belong to <see cref="TableSyncService"/>.
/// <para>
/// Database operations run on a server-level connection (<c>master</c>), so the connection's
/// login needs the matching server permissions. Table operations are schema-aware: an
/// entity's table lives in its <c>[Table(Schema = ...)]</c> schema (default <c>dbo</c>).
/// Every method returns a <see cref="Result{T}"/> instead of throwing, except for invalid
/// arguments.
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

    // Collation names cannot be bound as parameters, so they are limited to the characters
    // real names use (Latin1_General_100_CI_AS_SC_UTF8, SQL_Latin1_General_CP1_CI_AS, ...).
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex SqlNameToken();

    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates database <paramref name="name"/> on the connection's server. Without a
    /// <paramref name="collation"/> the database gets the server's default collation.
    /// SQL Server has no per-database character set; Unicode text lives in <c>nvarchar</c>
    /// columns (or use a <c>_UTF8</c> collation).
    /// </summary>
    /// <returns><see langword="true"/> when the database was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateDatabaseAsync(
        string name,
        string? collation = null,
        bool ifNotExists = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ValidateDatabaseName(name);
        ValidateToken(collation, nameof(collation));

        try
        {
            if (await DatabaseExistsCoreAsync(name, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mssql.database_exists", $"Database {SqlServerDialect.Quote(name)} already exists."));
            }

            var sql = $"CREATE DATABASE {SqlServerDialect.Quote(name)}"
                      + (string.IsNullOrEmpty(collation) ? "" : $" COLLATE {collation}");
            await ExecuteOnServerAsync(sql, connectionId, ct).ConfigureAwait(false);
            _logger?.Info($"[MSSQL] Created database {SqlServerDialect.Quote(name)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] CreateDatabaseAsync failed for {SqlServerDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.create_database_failed"));
        }
    }

    /// <summary>
    /// Drops database <paramref name="name"/>, including the one this connection is configured
    /// for. Idle pooled connections to it are cleared first, and other sessions still using it
    /// are disconnected (<c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c>) — their open
    /// transactions are rolled back.
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
                    "mssql.database_not_found", $"Database {SqlServerDialect.Quote(name)} does not exist."));
            }

            _connectionManager.ClearPoolsForDatabase(name);
            var quoted = SqlServerDialect.Quote(name);
            await ExecuteOnServerAsync(
                $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};",
                connectionId, ct).ConfigureAwait(false);
            QueryCache.Clear();
            _logger?.Warning($"[MSSQL] Dropped database {quoted}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] DropDatabaseAsync failed for {SqlServerDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.drop_database_failed"));
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
            _logger?.Error($"[MSSQL] DatabaseExistsAsync failed for {SqlServerDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    internal Task<bool> DatabaseExistsCoreAsync(string name, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteOnServerAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DB_ID(@name)";
            cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = name });
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is not null and not DBNull;
        }, connectionId, ct);

    // ── Tables ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the table for <typeparamref name="T"/> from its attributes, exactly as a first
    /// <c>SyncTableAsync</c> would, and records it in <c>__schema_state</c>. Unlike a sync it
    /// never alters an existing table. The entity's schema must already exist.
    /// </summary>
    /// <returns><see langword="true"/> when the table was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateTableAsync<T>(
        bool ifNotExists = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var schemaName = SchemaAnalyzer.GetSchemaName(entityType);
        var tableName = SchemaAnalyzer.GetTableName(entityType);
        var qualified = SqlServerDialect.Qualify(schemaName, tableName);
        try
        {
            if (await _tableSync.TableExistsAsync(schemaName, tableName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mssql.table_exists", $"Table {qualified} already exists."));
            }

            var stringSize = _tableSync.StringSizeFor(connectionId);
            var ddl = _tableSync.Analyzer.GenerateCreateTable(entityType, stringSize);
            await ExecuteAsync(ddl, connectionId, ct).ConfigureAwait(false);
            await _tableSync.GetSchemaStateStore().UpsertStateAsync(
                $"{schemaName}.{tableName}", _tableSync.Analyzer.ComputeSchemaCrc(entityType, stringSize),
                SchemaSyncStatus.Synced, _tableSync.ResolveMode(connectionId).ToString(), _tableSync.AppVersion,
                ddl, connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Info($"[MSSQL] Created table {qualified}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] CreateTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.create_table_failed"));
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
        var schemaName = SchemaAnalyzer.GetSchemaName(typeof(T));
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        var qualified = SqlServerDialect.Qualify(schemaName, tableName);
        try
        {
            if (!await _tableSync.TableExistsAsync(schemaName, tableName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "mssql.table_not_found", $"Table {qualified} does not exist."));
            }

            await ExecuteAsync($"DROP TABLE IF EXISTS {qualified}", connectionId, ct).ConfigureAwait(false);
            await _tableSync.GetSchemaStateStore()
                .RemoveStateAsync($"{schemaName}.{tableName}", connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Warning($"[MSSQL] Dropped table {qualified}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] DropTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.drop_table_failed"));
        }
    }

    /// <summary>
    /// Removes every row from the table for <typeparamref name="T"/> and reseeds its IDENTITY
    /// column (<c>TRUNCATE TABLE</c>). SQL Server refuses to truncate a table that another
    /// table's foreign key references.
    /// </summary>
    public async Task<Result<bool>> TruncateTableAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        var qualified = SqlServerDialect.Qualify(SchemaAnalyzer.GetSchemaName(typeof(T)), tableName);
        try
        {
            await ExecuteAsync($"TRUNCATE TABLE {qualified}", connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] TruncateTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.truncate_failed"));
        }
    }

    /// <summary>Returns whether the table for <typeparamref name="T"/> exists in the connection's database.</summary>
    public Task<Result<bool>> TableExistsAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
        => TableExistsAsync(
            $"{SchemaAnalyzer.GetSchemaName(typeof(T))}.{SchemaAnalyzer.GetTableName(typeof(T))}", connectionId, ct);

    /// <summary>
    /// Returns whether table <paramref name="tableName"/> exists in the connection's database.
    /// Pass <c>schema.table</c> for a table outside <c>dbo</c>; a bare name means <c>dbo</c>.
    /// </summary>
    public async Task<Result<bool>> TableExistsAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var dot = tableName.IndexOf('.', StringComparison.Ordinal);
        var schemaName = dot > 0 ? tableName[..dot] : "dbo";
        var bareName = dot > 0 ? tableName[(dot + 1)..] : tableName;
        try
        {
            return Result<bool>.Success(
                await _tableSync.TableExistsAsync(schemaName, bareName, connectionId, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] TableExistsAsync failed for {SqlServerDialect.Qualify(schemaName, bareName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateDatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // SQL Server's sysname limit; a longer name fails server-side with a less clear error.
        if (name.Length > 128)
            throw new ArgumentException("Database names are limited to 128 characters.", nameof(name));
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
            if (_connectionManager.QueryCommandTimeoutSeconds(connectionId) is { } timeoutSeconds)
                cmd.CommandTimeout = timeoutSeconds;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct);
}
