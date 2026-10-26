using System.Text;
using System.Text.RegularExpressions;
using CL.PostgreSQL.Core;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Database- and table-level administration: create / drop / check databases, and create /
/// drop / truncate / check entity tables. Meant for first-run setup, demos, test fixtures
/// and resets — day-to-day schema changes belong to <see cref="TableSyncService"/>.
/// <para>
/// Database operations run on the connection's maintenance database
/// (<see cref="Configuration.PostgreSqlDatabaseConfig.MaintenanceDatabase"/>, "postgres" by
/// default), because PostgreSQL cannot create or drop the database a session is connected
/// to; the connection's user needs the matching privileges (<c>CREATEDB</c>, or ownership
/// to drop). Table operations are schema-aware: an entity lands in its
/// <c>[Table(Schema = …)]</c>, or else in the connection's <c>DefaultSchema</c>. Every
/// method returns a <see cref="Result{T}"/> instead of throwing, except for invalid arguments.
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

    // Encoding and locale names cannot be bound as parameters — they are written as string
    // literals — so they are limited to the characters real names use (UTF8, en_US.UTF-8,
    // sr_RS@latin, C.UTF-8).
    [GeneratedRegex(@"^[A-Za-z0-9_.\-@]+$")]
    private static partial Regex SqlNameToken();

    // PostgreSQL's identifier limit in bytes (NAMEDATALEN - 1).
    private const int MaxIdentifierLength = 63;

    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates database <paramref name="name"/>. Without <paramref name="encoding"/> and
    /// <paramref name="collation"/> the server's defaults (those of <c>template1</c>) apply.
    /// When either is given the database is copied from <c>template0</c>, which PostgreSQL
    /// requires to pick an encoding or locale different from the template's;
    /// <paramref name="collation"/> sets both <c>LC_COLLATE</c> and <c>LC_CTYPE</c>
    /// (e.g. <c>en_US.UTF-8</c>).
    /// </summary>
    /// <returns><see langword="true"/> when the database was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateDatabaseAsync(
        string name,
        string? encoding = null,
        string? collation = null,
        bool ifNotExists = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ValidateDatabaseName(name);
        ValidateToken(encoding, nameof(encoding));
        ValidateToken(collation, nameof(collation));

        try
        {
            if (await DatabaseExistsCoreAsync(name, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "postgresql.database_exists", $"Database {PostgreSqlDialect.Quote(name)} already exists."));
            }

            // PostgreSQL has no CREATE DATABASE IF NOT EXISTS; the check above covers it, and a
            // racing creator surfaces as a duplicate_database (42P04) failure.
            var sql = $"CREATE DATABASE {PostgreSqlDialect.Quote(name)}"
                      + (string.IsNullOrEmpty(encoding) ? "" : $" ENCODING '{encoding}'")
                      + (string.IsNullOrEmpty(collation) ? "" : $" LC_COLLATE '{collation}' LC_CTYPE '{collation}'")
                      + (string.IsNullOrEmpty(encoding) && string.IsNullOrEmpty(collation) ? "" : " TEMPLATE template0");
            await ExecuteOnServerAsync(sql, connectionId, ct).ConfigureAwait(false);
            _logger?.Info($"[PostgreSQL] Created database {PostgreSqlDialect.Quote(name)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] CreateDatabaseAsync failed for {PostgreSqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.create_database_failed"));
        }
    }

    /// <summary>
    /// Drops database <paramref name="name"/>, including the one this connection is configured
    /// for. Idle pooled connections to it are cleared first, and the drop uses
    /// <c>WITH (FORCE)</c> to terminate any other sessions still connected to it, so it needs
    /// PostgreSQL 13 or later.
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
                    "postgresql.database_not_found", $"Database {PostgreSqlDialect.Quote(name)} does not exist."));
            }

            _connectionManager.ClearPoolsForDatabase(name);
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {PostgreSqlDialect.Quote(name)} WITH (FORCE)", connectionId, ct)
                .ConfigureAwait(false);
            QueryCache.Clear();
            _logger?.Warning($"[PostgreSQL] Dropped database {PostgreSqlDialect.Quote(name)}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] DropDatabaseAsync failed for {PostgreSqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.drop_database_failed"));
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
            _logger?.Error($"[PostgreSQL] DatabaseExistsAsync failed for {PostgreSqlDialect.Quote(name)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.query_failed"));
        }
    }

    internal Task<bool> DatabaseExistsCoreAsync(string name, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteOnServerAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            cmd.Parameters.AddWithValue("@name", name);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is not null and not DBNull;
        }, connectionId, ct);

    // ── Tables ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the table for <typeparamref name="T"/> from its attributes, exactly as a first
    /// <c>SyncTableAsync</c> would (including <c>CREATE SCHEMA IF NOT EXISTS</c> for its
    /// schema), and records it in <c>__schema_state</c>. Unlike a sync it never alters an
    /// existing table.
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
        var schemaName = SchemaAnalyzer.GetSchemaName(entityType, connectionId);
        var qualified = PostgreSqlDialect.Qualify(schemaName, tableName);
        try
        {
            if (await _tableSync.TableExistsAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "postgresql.table_exists", $"Table {qualified} already exists."));
            }

            var ddl = _tableSync.Analyzer.GenerateCreateTable(entityType, connectionId);
            await _tableSync.ExecuteSqlAsync(ddl, connectionId, ct).ConfigureAwait(false);
            // Same sentinel key as the sync path: tables are unique per schema.
            await _tableSync.GetSchemaStateStore().UpsertStateAsync(
                $"{schemaName}.{tableName}", _tableSync.Analyzer.ComputeSchemaCrc(entityType, connectionId),
                SchemaSyncStatus.Synced, _tableSync.ResolveMode(connectionId).ToString(), _tableSync.AppVersion,
                ddl, connectionId, ct).ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Info($"[PostgreSQL] Created table {qualified}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] CreateTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.create_table_failed"));
        }
    }

    /// <summary>
    /// Drops the table for <typeparamref name="T"/> and forgets its <c>__schema_state</c> row,
    /// so the next sync recreates it. Without <paramref name="cascade"/> the drop fails if
    /// another table's foreign key (or a view) depends on it; with it, PostgreSQL drops those
    /// foreign-key constraints and dependent views too (<c>DROP TABLE … CASCADE</c>).
    /// </summary>
    /// <returns><see langword="true"/> when a table was dropped.</returns>
    public async Task<Result<bool>> DropTableAsync<T>(
        bool ifExists = true,
        bool cascade = false,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        var schemaName = SchemaAnalyzer.GetSchemaName(typeof(T), connectionId);
        var qualified = PostgreSqlDialect.Qualify(schemaName, tableName);
        try
        {
            if (!await _tableSync.TableExistsAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false))
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "postgresql.table_not_found", $"Table {qualified} does not exist."));
            }

            await _tableSync.ExecuteSqlAsync(
                $"DROP TABLE IF EXISTS {qualified}{(cascade ? " CASCADE" : "")}", connectionId, ct).ConfigureAwait(false);
            await _tableSync.GetSchemaStateStore().RemoveStateAsync($"{schemaName}.{tableName}", connectionId, ct)
                .ConfigureAwait(false);
            QueryCache.Invalidate(tableName);
            _logger?.Warning($"[PostgreSQL] Dropped table {qualified}{(cascade ? " (cascade)" : "")}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] DropTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.drop_table_failed"));
        }
    }

    /// <summary>
    /// Removes every row from the table for <typeparamref name="T"/> (<c>TRUNCATE TABLE</c>).
    /// <paramref name="restartIdentity"/> resets the sequences behind its identity / serial
    /// columns (<c>RESTART IDENTITY</c>). Without <paramref name="cascade"/> PostgreSQL refuses
    /// to truncate a table another table's foreign key references; with it, the referencing
    /// tables are truncated as well.
    /// </summary>
    public async Task<Result<bool>> TruncateTableAsync<T>(
        bool restartIdentity = true,
        bool cascade = false,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        var qualified = SchemaAnalyzer.GetQualifiedName(typeof(T), connectionId);
        try
        {
            await _tableSync.ExecuteSqlAsync(
                $"TRUNCATE TABLE {qualified}{(restartIdentity ? " RESTART IDENTITY" : "")}{(cascade ? " CASCADE" : "")}",
                connectionId, ct).ConfigureAwait(false);
            // CASCADE may have emptied other tables too; their names are not known here.
            if (cascade) QueryCache.Clear();
            else QueryCache.Invalidate(tableName);
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] TruncateTableAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.truncate_failed"));
        }
    }

    /// <summary>Returns whether the table for <typeparamref name="T"/> exists in its schema.</summary>
    public async Task<Result<bool>> TableExistsAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        var schemaName = SchemaAnalyzer.GetSchemaName(typeof(T), connectionId);
        return await TableExistsCoreAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns whether table <paramref name="tableName"/> exists. A bare name is looked up in
    /// the connection's <c>DefaultSchema</c>; <c>schema.table</c> names the schema explicitly.
    /// </summary>
    public Task<Result<bool>> TableExistsAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var dot = tableName.IndexOf('.');
        var (schema, table) = dot > 0 && dot < tableName.Length - 1
            ? (tableName[..dot], tableName[(dot + 1)..])
            : (PostgreSqlRuntimeOptions.DefaultSchemaFor(connectionId), tableName);
        return TableExistsCoreAsync(table, schema, connectionId, ct);
    }

    private async Task<Result<bool>> TableExistsCoreAsync(
        string tableName, string schemaName, string connectionId, CancellationToken ct)
    {
        try
        {
            return Result<bool>.Success(
                await _tableSync.TableExistsAsync(tableName, schemaName, connectionId, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] TableExistsAsync failed for {PostgreSqlDialect.Qualify(schemaName, tableName)}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.query_failed"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ValidateDatabaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // PostgreSQL silently truncates longer identifiers, so a longer name would create (or
        // drop) a different database than the one asked for.
        if (Encoding.UTF8.GetByteCount(name) > MaxIdentifierLength)
            throw new ArgumentException($"Database names are limited to {MaxIdentifierLength} bytes.", nameof(name));
    }

    private static void ValidateToken(string? value, string paramName)
    {
        if (!string.IsNullOrEmpty(value) && !SqlNameToken().IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid {paramName} name.", paramName);
    }

    private Task<bool> ExecuteOnServerAsync(string sql, string connectionId, CancellationToken ct) =>
        _connectionManager.ExecuteOnServerAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand(connectionId);
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct);
}
