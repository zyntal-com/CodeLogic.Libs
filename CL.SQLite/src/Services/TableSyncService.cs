using System.Reflection;
using System.Runtime.CompilerServices;
using CL.SQLite.Events;
using CL.SQLite.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Synchronizes SQLite table schemas with C# entity type definitions.
/// Creates tables that don't exist and adds missing columns to existing ones.
/// </summary>
public sealed class TableSyncService
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly SchemaAnalyzer _analyzer;
    private readonly MigrationTracker _migrationTracker;

    /// <summary>
    /// Initializes a new instance of <see cref="TableSyncService"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager providing database access.</param>
    /// <param name="dataDirectory">Optional base directory used to resolve relative migration tracking paths.</param>
    /// <param name="logger">Optional logger for sync progress and errors.</param>
    /// <param name="events">Optional event bus for publishing <see cref="TableSyncedEvent"/>.</param>
    public TableSyncService(
        ConnectionManager connectionManager,
        string? dataDirectory = null,
        ILogger? logger = null,
        IEventBus? events = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _events = events;
        _analyzer = new SchemaAnalyzer(logger);
        _migrationTracker = new MigrationTracker(dataDirectory, logger);
    }

    /// <summary>Gets the migration tracker used to record schema changes.</summary>
    public MigrationTracker MigrationTracker => _migrationTracker;

    /// <summary>
    /// Synchronizes the database table for entity type <typeparamref name="T"/>.
    /// Creates the table if it does not exist, or adds any missing columns if it does.
    /// Indexes declared via attributes are also created.
    /// </summary>
    /// <remarks>
    /// When the target connection is configured with <c>SkipTableSync</c>, nothing is read or
    /// written and the call succeeds with a result saying the sync was skipped.
    /// </remarks>
    /// <typeparam name="T">The entity type whose table should be synchronized.</typeparam>
    /// <param name="connectionId">Named SQLite connection to sync against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing a <see cref="TableSyncResult"/> describing what was done.</returns>
    public async Task<Result<TableSyncResult>> SyncTableAsync<T>(
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var tableName = SchemaAnalyzer.GetTableName(entityType);

        _logger?.Info($"[SQLite] {string.Format(SQLiteObservability.Strings.TableSyncStarted, tableName)}");

        try
        {
            if (_connectionManager.GetConfiguration(connectionId).SkipTableSync)
            {
                var skipped = $"Table sync skipped for '{tableName}': SkipTableSync is set on connection '{connectionId}'";
                _logger?.Info($"[SQLite] {skipped}");
                return Result<TableSyncResult>.Success(TableSyncResult.Succeeded(skipped));
            }

            var result = await _connectionManager.ExecuteAsync(async conn =>
            {
                var tableExists = await _analyzer.TableExistsAsync(conn, tableName).ConfigureAwait(false);
                var modelColumns = _analyzer.GetModelColumns(entityType);

                if (!tableExists)
                {
                    // CREATE TABLE
                    var createSql = _analyzer.GenerateCreateTableSql(tableName, modelColumns);
                    _logger?.Debug($"[SQLite] Creating table '{tableName}': {createSql}");

                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = createSql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    await _migrationTracker.RecordMigrationAsync(
                        $"create_table_{tableName}",
                        $"Created table {tableName}",
                        ct).ConfigureAwait(false);

                    _logger?.Info($"[SQLite] {string.Format(SQLiteObservability.Strings.TableCreated, tableName)}");

                    // Create indexes
                    await SyncIndexesAsync(conn, entityType, tableName, ct).ConfigureAwait(false);

                    return TableSyncResult.Succeeded($"Created table '{tableName}'");
                }
                else
                {
                    // ALTER TABLE — add missing columns
                    var existingColumns = await _analyzer.GetDatabaseColumnsAsync(conn, tableName).ConfigureAwait(false);
                    var addedColumns = new List<string>();

                    foreach (var col in modelColumns)
                    {
                        if (existingColumns.Any(c => string.Equals(c, col.ColumnName, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var typeName = _analyzer.DeclaredType(col);
                        var alterSql = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{col.ColumnName}\" {typeName}";

                        if (col.DefaultValue is not null)
                            alterSql += $" DEFAULT {col.DefaultValue}";

                        await using var cmd = conn.CreateCommand();
                        cmd.CommandText = alterSql;
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                        addedColumns.Add(col.ColumnName);
                        _logger?.Debug($"[SQLite] Added column '{col.ColumnName}' to '{tableName}'");

                        await _migrationTracker.RecordMigrationAsync(
                            $"add_column_{tableName}_{col.ColumnName}",
                            $"Added column {col.ColumnName} to {tableName}",
                            ct).ConfigureAwait(false);
                    }

                    // Sync indexes
                    await SyncIndexesAsync(conn, entityType, tableName, ct).ConfigureAwait(false);

                    var msg = addedColumns.Count > 0
                        ? $"Updated table '{tableName}': added columns [{string.Join(", ", addedColumns)}]"
                        : $"Table '{tableName}' is up to date";

                    _logger?.Info($"[SQLite] {string.Format(SQLiteObservability.Strings.TableSynced, tableName)} — {msg}");
                    return TableSyncResult.Succeeded(msg);
                }
            }, connectionId, ct).ConfigureAwait(false);

            if (_events is not null)
            {
                await _events.PublishAsync(new TableSyncedEvent(
                    tableName,
                    result.Message.StartsWith("Created"),
                    result.Message,
                    DateTime.UtcNow)).ConfigureAwait(false);
            }

            return Result<TableSyncResult>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] {string.Format(SQLiteObservability.Strings.TableSyncFailed, tableName, ex.Message)}", ex);
            return Result<TableSyncResult>.Failure(
                Error.Internal("sqlite.sync_failed", $"Table synchronization failed for {tableName}", ex.Message));
        }
    }

    /// <summary>
    /// Synchronizes the database tables for a collection of entity types.
    /// </summary>
    /// <param name="modelTypes">The entity types whose tables should be synchronized.</param>
    /// <param name="connectionId">Named SQLite connection to sync against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary keyed by table name, where each value is the <see cref="Result{T}"/> of that table's sync.
    /// </returns>
    public async Task<Dictionary<string, Result<TableSyncResult>>> SyncTablesAsync(
        Type[] modelTypes,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, Result<TableSyncResult>>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in modelTypes)
        {
            var tableName = SchemaAnalyzer.GetTableName(type);
            try
            {
                var method = typeof(TableSyncService)
                    .GetMethod(nameof(SyncTableAsync))!
                    .MakeGenericMethod(type);

                var task = (Task<Result<TableSyncResult>>)method.Invoke(this, [connectionId, ct])!;
                results[tableName] = await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Error($"[SQLite] Failed to sync '{tableName}': {ex.Message}", ex);
                results[tableName] = Result<TableSyncResult>.Failure(
                    Error.FromException(ex, "sqlite.sync_failed"));
            }
        }

        return results;
    }

    /// <summary>
    /// Synchronizes all entity types in the specified namespace within the calling assembly.
    /// Only types decorated with <see cref="SQLiteTableAttribute"/> are processed.
    /// </summary>
    /// <remarks>
    /// The calling assembly is captured here, before any state machine starts — inside an
    /// <c>async</c> body <see cref="Assembly.GetCallingAssembly"/> sees the async infrastructure
    /// rather than the caller. Use the
    /// <see cref="SyncNamespaceAsync(Assembly, string, bool, string, CancellationToken)"/>
    /// overload to name the assembly explicitly instead of relying on the stack.
    /// </remarks>
    /// <param name="namespaceName">The namespace to scan for entity types.</param>
    /// <param name="includeDerived">
    /// When <c>true</c>, also includes types in sub-namespaces that start with <paramref name="namespaceName"/>.
    /// </param>
    /// <param name="connectionId">Named SQLite connection to sync against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary keyed by table name, where each value is the <see cref="Result{T}"/> of that table's sync.
    /// </returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task<Dictionary<string, Result<TableSyncResult>>> SyncNamespaceAsync(
        string namespaceName,
        bool includeDerived = false,
        string connectionId = "Default",
        CancellationToken ct = default)
        => SyncNamespaceAsync(Assembly.GetCallingAssembly(), namespaceName, includeDerived, connectionId, ct);

    /// <summary>
    /// Synchronizes all entity types in the specified namespace within <paramref name="assembly"/>.
    /// Only types decorated with <see cref="SQLiteTableAttribute"/> are processed.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="namespaceName">The namespace to scan for entity types.</param>
    /// <param name="includeDerived">
    /// When <c>true</c>, also includes types in sub-namespaces that start with <paramref name="namespaceName"/>.
    /// </param>
    /// <param name="connectionId">Named SQLite connection to sync against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary keyed by table name, where each value is the <see cref="Result{T}"/> of that table's sync.
    /// </returns>
    public async Task<Dictionary<string, Result<TableSyncResult>>> SyncNamespaceAsync(
        Assembly assembly,
        string namespaceName,
        bool includeDerived = false,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var types = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract &&
                        (t.Namespace == namespaceName || (includeDerived && t.Namespace?.StartsWith(namespaceName) == true)) &&
                        t.GetCustomAttribute<SQLiteTableAttribute>() is not null)
            .ToArray();

        return await SyncTablesAsync(types, connectionId, ct).ConfigureAwait(false);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the indexes declared on <paramref name="entityType"/> (<see cref="SQLiteIndexAttribute"/>
    /// and indexed or unique <see cref="SQLiteColumnAttribute"/> properties) with
    /// <c>CREATE INDEX IF NOT EXISTS</c>. Shared with <see cref="DatabaseAdmin"/>.
    /// </summary>
    internal static async Task SyncIndexesAsync(SqliteConnection conn, Type entityType, string tableName, CancellationToken ct)
    {
        // Class-level indexes via [SQLiteIndex]
        var classIndexes = entityType.GetCustomAttributes<SQLiteIndexAttribute>();
        foreach (var idx in classIndexes)
        {
            var idxName = idx.Name ?? $"idx_{tableName}_{string.Join("_", idx.Columns)}";
            var unique = idx.IsUnique ? "UNIQUE " : "";
            var cols = string.Join(", ", idx.Columns.Select(c => $"\"{c}\""));
            var sql = $"CREATE {unique}INDEX IF NOT EXISTS \"{idxName}\" ON \"{tableName}\" ({cols});";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Property-level indexes via [SQLiteColumn(IsIndexed=true)]
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
            if (colAttr is null || (!colAttr.IsIndexed && !colAttr.IsUnique)) continue;
            if (colAttr.IsPrimaryKey) continue;

            var colName = colAttr.ColumnName ?? prop.Name;
            var idxName = $"idx_{tableName}_{colName}";
            var unique = colAttr.IsUnique ? "UNIQUE " : "";
            var sql = $"CREATE {unique}INDEX IF NOT EXISTS \"{idxName}\" ON \"{tableName}\" (\"{colName}\");";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
