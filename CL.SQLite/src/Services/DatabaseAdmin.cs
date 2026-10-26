using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Database- and table-level administration: create / drop / check database files, and
/// create / drop / truncate / check entity tables. Meant for first-run setup, demos, test
/// fixtures and resets — day-to-day schema changes belong to <see cref="TableSyncService"/>.
/// <para>
/// A SQLite database is a file, so the database operations work on paths. A relative path is
/// resolved against the library's data directory, exactly as a configured
/// <c>DatabasePath</c> is. Every method returns a <see cref="Result{T}"/> instead of
/// throwing, except for invalid arguments.
/// </para>
/// </summary>
public sealed class DatabaseAdmin
{
    // Files SQLite keeps next to a database: write-ahead log, its shared-memory index, and
    // the rollback journal.
    private static readonly string[] SiblingSuffixes = ["-wal", "-shm", "-journal"];

    private readonly ConnectionManager _connectionManager;
    private readonly TableSyncService? _tableSync;
    private readonly ILogger? _logger;
    private readonly SchemaAnalyzer _analyzer;

    /// <summary>
    /// Initializes a new instance of <see cref="DatabaseAdmin"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager whose pools are used for table operations and drained before a database file is deleted.</param>
    /// <param name="tableSync">Optional table sync service; when given, created tables are recorded in its migration ledger as a sync would.</param>
    /// <param name="logger">Optional logger.</param>
    public DatabaseAdmin(ConnectionManager connectionManager, TableSyncService? tableSync = null, ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _tableSync = tableSync;
        _logger = logger;
        _analyzer = new SchemaAnalyzer(logger);
    }

    // ── Databases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the database file at <paramref name="path"/> (and its parent directory). The
    /// file is created by opening it in <c>ReadWriteCreate</c> mode, so it is an empty, valid
    /// SQLite database.
    /// </summary>
    /// <remarks>
    /// SQLite also creates a configured database file the first time a connection opens it,
    /// so this is only needed for files the library is not (yet) configured for.
    /// </remarks>
    /// <param name="path">Database file path; relative paths resolve against the data directory.</param>
    /// <param name="ifNotExists">When <c>true</c>, an existing file is a success that returns <c>false</c>; otherwise it is a failure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the file was created, <see langword="false"/>
    /// when it already existed and <paramref name="ifNotExists"/> is set.</returns>
    public async Task<Result<bool>> CreateDatabaseAsync(
        string path,
        bool ifNotExists = true,
        CancellationToken ct = default)
    {
        var fullPath = ResolveDatabasePath(path);
        try
        {
            if (File.Exists(fullPath))
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "sqlite.database_exists", $"Database file '{fullPath}' already exists."));
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = fullPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                // Not pooled, so the handle is really closed when this method returns.
                Pooling = false
            }.ToString();

            await using (var conn = new SqliteConnection(connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }

            _logger?.Info($"[SQLite] Created database '{fullPath}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] CreateDatabaseAsync failed for '{fullPath}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.create_database_failed"));
        }
    }

    /// <summary>
    /// Deletes the database file at <paramref name="path"/> together with its <c>-wal</c>,
    /// <c>-shm</c> and <c>-journal</c> siblings. Idle pooled connections to the file — the
    /// library's own and Microsoft.Data.Sqlite's — are closed first.
    /// </summary>
    /// <remarks>
    /// Dropping a configured database is allowed: its connection ID stays registered, and the
    /// next query recreates an empty file. A connection that is in use while the drop runs
    /// keeps the file open; on Windows the delete then fails and this returns a failure.
    /// </remarks>
    /// <param name="path">Database file path; relative paths resolve against the data directory.</param>
    /// <param name="ifExists">When <c>true</c>, a missing file is a success that returns <c>false</c>; otherwise it is a failure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a database file was deleted.</returns>
    public async Task<Result<bool>> DropDatabaseAsync(
        string path,
        bool ifExists = true,
        CancellationToken ct = default)
    {
        var fullPath = ResolveDatabasePath(path);
        try
        {
            if (!File.Exists(fullPath))
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "sqlite.database_not_found", $"Database file '{fullPath}' does not exist."));
            }

            _connectionManager.ReleaseConnectionsForPath(fullPath);

            // The main file goes last: while it exists, a leftover -wal still belongs to it.
            foreach (var suffix in SiblingSuffixes)
                await DeleteFileAsync(fullPath + suffix, ct).ConfigureAwait(false);
            await DeleteFileAsync(fullPath, ct).ConfigureAwait(false);

            _logger?.Warning($"[SQLite] Dropped database '{fullPath}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DropDatabaseAsync failed for '{fullPath}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.drop_database_failed"));
        }
    }

    /// <summary>Returns whether the database file at <paramref name="path"/> exists.</summary>
    /// <param name="path">Database file path; relative paths resolve against the data directory.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<Result<bool>> DatabaseExistsAsync(string path, CancellationToken ct = default)
    {
        var fullPath = ResolveDatabasePath(path);
        try
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result<bool>.Success(File.Exists(fullPath)));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DatabaseExistsAsync failed for '{fullPath}': {ex.Message}", ex);
            return Task.FromResult(Result<bool>.Failure(Error.FromException(ex, "sqlite.query_failed")));
        }
    }

    // ── Tables ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the table for <typeparamref name="T"/> from its attributes, with its declared
    /// indexes, exactly as a first <see cref="TableSyncService.SyncTableAsync{T}"/> would.
    /// Unlike a sync it never alters an existing table, and it runs even on a connection
    /// configured with <c>SkipTableSync</c>.
    /// </summary>
    /// <typeparam name="T">The entity type whose table should be created.</typeparam>
    /// <param name="ifNotExists">When <c>true</c>, an existing table is a success that returns <c>false</c>; otherwise it is a failure.</param>
    /// <param name="connectionId">Named SQLite connection to use.</param>
    /// <param name="ct">Cancellation token.</param>
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
            var created = await _connectionManager.ExecuteAsync(async conn =>
            {
                if (await _analyzer.TableExistsAsync(conn, tableName).ConfigureAwait(false))
                    return false;

                var createSql = _analyzer.GenerateCreateTableSql(tableName, _analyzer.GetModelColumns(entityType));
                _logger?.Debug($"[SQLite] Creating table '{tableName}': {createSql}");

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = createSql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await TableSyncService.SyncIndexesAsync(conn, entityType, tableName, ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            if (!created)
            {
                if (ifNotExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "sqlite.table_exists", $"Table '{tableName}' already exists."));
            }

            if (_tableSync is not null)
                await _tableSync.MigrationTracker.RecordMigrationAsync(
                    $"create_table_{tableName}", $"Created table {tableName}", ct).ConfigureAwait(false);

            _logger?.Info($"[SQLite] Created table '{tableName}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] CreateTableAsync failed for '{tableName}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.create_table_failed"));
        }
    }

    /// <summary>
    /// Drops the table for <typeparamref name="T"/> (with its indexes). With foreign keys
    /// enabled, SQLite fails the drop when rows in another table still reference it.
    /// </summary>
    /// <typeparam name="T">The entity type whose table should be dropped.</typeparam>
    /// <param name="ifExists">When <c>true</c>, a missing table is a success that returns <c>false</c>; otherwise it is a failure.</param>
    /// <param name="connectionId">Named SQLite connection to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a table was dropped.</returns>
    public async Task<Result<bool>> DropTableAsync<T>(
        bool ifExists = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        try
        {
            var dropped = await _connectionManager.ExecuteAsync(async conn =>
            {
                if (!await _analyzer.TableExistsAsync(conn, tableName).ConfigureAwait(false))
                    return false;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS \"{tableName}\"";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            if (!dropped)
            {
                if (ifExists) return Result<bool>.Success(false);
                return Result<bool>.Failure(Error.Internal(
                    "sqlite.table_not_found", $"Table '{tableName}' does not exist."));
            }

            _logger?.Warning($"[SQLite] Dropped table '{tableName}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DropTableAsync failed for '{tableName}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.drop_table_failed"));
        }
    }

    /// <summary>
    /// Removes every row from the table for <typeparamref name="T"/> and restarts its
    /// <c>AUTOINCREMENT</c> counter. SQLite has no <c>TRUNCATE</c>: this runs
    /// <c>DELETE FROM "t"</c> (which SQLite optimizes to a table truncation when no trigger or
    /// foreign key needs the rows one by one) and removes the table's row from
    /// <c>sqlite_sequence</c>, in one transaction.
    /// </summary>
    /// <typeparam name="T">The entity type whose table should be emptied.</typeparam>
    /// <param name="connectionId">Named SQLite connection to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<bool>> TruncateTableAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var tableName = SchemaAnalyzer.GetTableName(typeof(T));
        try
        {
            await _connectionManager.ExecuteAsync(async conn =>
            {
                // sqlite_sequence only exists once some table with AUTOINCREMENT was created.
                // Checked before the transaction opens: a command on a connection with a
                // pending transaction must be enlisted in it.
                var hasSequence = await _analyzer.TableExistsAsync(conn, "sqlite_sequence").ConfigureAwait(false);

                await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM \"{tableName}\"";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (hasSequence)
                {
                    await using var seq = conn.CreateCommand();
                    seq.Transaction = tx;
                    seq.CommandText = "DELETE FROM sqlite_sequence WHERE name = @t";
                    seq.Parameters.AddWithValue("@t", tableName);
                    await seq.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[SQLite] Truncated table '{tableName}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] TruncateTableAsync failed for '{tableName}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.truncate_failed"));
        }
    }

    /// <summary>Returns whether the table for <typeparamref name="T"/> exists.</summary>
    /// <typeparam name="T">The entity type whose table to look for.</typeparam>
    /// <param name="connectionId">Named SQLite connection to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<Result<bool>> TableExistsAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
        => TableExistsAsync(SchemaAnalyzer.GetTableName(typeof(T)), connectionId, ct);

    /// <summary>Returns whether table <paramref name="tableName"/> exists.</summary>
    /// <param name="tableName">The table name to look for.</param>
    /// <param name="connectionId">Named SQLite connection to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<bool>> TableExistsAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        try
        {
            var exists = await _connectionManager.ExecuteAsync(
                conn => _analyzer.TableExistsAsync(conn, tableName), connectionId, ct).ConfigureAwait(false);
            return Result<bool>.Success(exists);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] TableExistsAsync failed for '{tableName}': {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ResolveDatabasePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An in-memory database has no file to create or drop.", nameof(path));
        return Path.GetFullPath(_connectionManager.ResolvePath(path));
    }

    // Windows can report a just-closed handle as still open for a moment, so a sharing
    // violation is retried briefly before it is reported.
    private static async Task DeleteFileAsync(string file, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!File.Exists(file)) return;
            try
            {
                File.Delete(file);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
