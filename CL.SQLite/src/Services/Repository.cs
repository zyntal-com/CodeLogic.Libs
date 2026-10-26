using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using CL.SQLite.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Generic repository providing CRUD and query operations for entity type <typeparamref name="T"/>.
/// Uses reflection with a static per-type cache for performance.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _cachedProperties = new();

    // ── Table metadata (cached per type) ─────────────────────────────────────

    private static readonly string _tableName = GetTableName();
    private static readonly string[] _pkColumnNames = GetPrimaryKeyColumnNames();

    /// <summary>
    /// Initializes a new instance of <see cref="Repository{T}"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager used to obtain and release database connections.</param>
    /// <param name="logger">Optional logger for debug and warning output.</param>
    /// <param name="connectionId">Named SQLite connection to use (as configured in <c>config.sqlite.json</c>).</param>
    /// <param name="slowQueryThresholdMs">Queries exceeding this threshold in milliseconds are logged as warnings.</param>
    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 500)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a new entity into the table. If the entity has an auto-increment primary key,
    /// the generated row ID is written back to the entity's PK property.
    /// </summary>
    /// <param name="entity">The entity to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the SQLite row ID of the inserted row.</returns>
    public async Task<Result<long>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var props = GetCachedProperties();
            var cols = new List<string>();
            var paramNames = new List<string>();
            var values = new Dictionary<string, object?>();

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                if (colAttr?.IsAutoIncrement == true) continue;
                var colName = colAttr?.ColumnName ?? prop.Name;
                // Parameter names are positional rather than derived from the column name: a
                // column name is an arbitrary quoted identifier and need not be a legal token.
                var paramName = $"@p{values.Count}";
                cols.Add($"\"{colName}\"");
                paramNames.Add(paramName);
                values[paramName] = SQLiteValueConverter.ToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            // An entity whose only column is the auto-increment key leaves nothing to write;
            // SQLite spells that INSERT INTO "t" DEFAULT VALUES, not an empty column list.
            var sql = cols.Count == 0
                ? $"INSERT INTO \"{_tableName}\" DEFAULT VALUES; SELECT last_insert_rowid();"
                : $"INSERT INTO \"{_tableName}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)}); SELECT last_insert_rowid();";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var rowId = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            // Assign auto-increment PK
            var pkProp = TryGetAutoIncrementPk();
            if (pkProp is not null)
            {
                var converted = Convert.ChangeType(rowId, pkProp.PropertyType);
                pkProp.SetValue(entity, converted);
            }

            _logger?.Debug($"[SQLite] Inserted into '{_tableName}', rowid={rowId}");
            return Result<long>.Success(rowId);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] InsertAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "sqlite.insert_failed"));
        }
    }

    /// <summary>
    /// Inserts the entity or replaces an existing row with the same primary key (INSERT OR REPLACE).
    /// </summary>
    /// <param name="entity">The entity to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> UpsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var props = GetCachedProperties();
            var cols = new List<string>();
            var paramNames = new List<string>();
            var values = new Dictionary<string, object?>();

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                var colName = colAttr?.ColumnName ?? prop.Name;
                var paramName = $"@p{values.Count}";
                cols.Add($"\"{colName}\"");
                paramNames.Add(paramName);
                values[paramName] = SQLiteValueConverter.ToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            var sql = $"INSERT OR REPLACE INTO \"{_tableName}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)});";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] UpsertAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.upsert_failed"));
        }
    }

    /// <summary>
    /// Inserts <paramref name="entity"/>, or — when it collides with an existing row on the
    /// conflict target — updates that row with <paramref name="onConflict"/>, in one statement.
    /// The setter receives the existing row and the row that failed to insert, so the update
    /// can build on current values:
    /// <code>
    /// await repo.UpsertAsync(
    ///     new LoginThrottle { Ip = ip, Failures = 1, LastUtc = now },
    ///     (current, incoming) =&gt; new LoginThrottle
    ///     {
    ///         Failures = current.Failures + 1,
    ///         LastUtc  = incoming.LastUtc,
    ///     });
    /// </code>
    /// </summary>
    /// <remarks>
    /// Emits <c>INSERT INTO "t" (…) VALUES (…) ON CONFLICT (target) DO UPDATE SET col = expr</c>
    /// (SQLite 3.24+), with <c>current.Col</c> written as the existing row's column and
    /// <c>incoming.Col</c> as <c>excluded."col"</c>. Columns the setter does not assign keep
    /// their current value on conflict; SQLite evaluates every assignment against the existing
    /// row as it was before the update. Captured values are sent as parameters.
    /// <para>
    /// SQLite only accepts a conflict target that matches a PRIMARY KEY or UNIQUE index
    /// exactly. When <paramref name="conflictTarget"/> is omitted it is inferred: the primary
    /// key (unless it is auto-increment, which an incoming row never collides on), a
    /// <c>[SQLiteColumn(IsUnique = true)]</c> column, or a unique <see cref="SQLiteIndexAttribute"/>
    /// — and it must be exactly one of them. The plain
    /// <see cref="UpsertAsync(T, CancellationToken)"/> (INSERT OR REPLACE) is unchanged.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity to insert.</param>
    /// <param name="onConflict">A member-init expression <c>(current, incoming) =&gt; new T { Prop = value, … }</c>.</param>
    /// <param name="conflictTarget">Property or column names of the unique key to arbitrate on, or <c>null</c> to infer it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the rows affected: 1 for an insert or an update.</returns>
    public async Task<Result<int>> UpsertAsync(
        T entity,
        Expression<Func<T, T, T>> onConflict,
        IReadOnlyList<string>? conflictTarget = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(onConflict);
        if (onConflict.Body is not MemberInitExpression mi || mi.Bindings.Count == 0)
            throw new ArgumentException(
                "onConflict must be a member-init expression like (current, incoming) => new T { Prop = value }.",
                nameof(onConflict));

        try
        {
            var target = ResolveConflictTarget(conflictTarget);

            var current = onConflict.Parameters[0];
            var incoming = onConflict.Parameters[1];
            var renderers = new Dictionary<ParameterExpression, Func<string, string>>
            {
                [current] = SQLiteColumnExpressionTranslator.Quote,
                [incoming] = c => $"excluded.{SQLiteColumnExpressionTranslator.Quote(c)}",
            };

            var sink = new SQLiteParameterSink("@ups_");
            var updateClauses = new List<string>();
            foreach (var binding in mi.Bindings)
            {
                if (binding is not MemberAssignment ma)
                    throw new ArgumentException("onConflict supports plain property assignments only.", nameof(onConflict));
                var attr = ma.Member.GetCustomAttribute<SQLiteColumnAttribute>()
                           ?? throw new ArgumentException(
                               $"Property '{ma.Member.Name}' is not a mapped column on '{typeof(T).Name}'.", nameof(onConflict));
                if (attr.IsAutoIncrement)
                    throw new ArgumentException(
                        $"Property '{ma.Member.Name}' is an auto-increment key and cannot be updated.", nameof(onConflict));
                var column = SQLiteColumnExpressionTranslator.Quote(SQLiteColumnExpressionTranslator.GetColumnName(ma.Member));

                if (SQLiteColumnExpressionTranslator.References(ma.Expression, current, incoming))
                {
                    var (exprSql, _) = SQLiteColumnExpressionTranslator.Translate(ma.Expression, renderers, sink);
                    updateClauses.Add($"{column} = {exprSql}");
                }
                else
                {
                    var value = SQLiteColumnExpressionTranslator.Evaluate(ma.Expression);
                    updateClauses.Add($"{column} = {(value is null ? "NULL" : sink.Add(value))}");
                }
            }

            var cols = new List<string>();
            var paramNames = new List<string>();
            var values = new Dictionary<string, object?>();
            foreach (var prop in GetCachedProperties())
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                if (colAttr?.IsAutoIncrement == true) continue;
                var paramName = $"@p{values.Count}";
                cols.Add($"\"{colAttr?.ColumnName ?? prop.Name}\"");
                paramNames.Add(paramName);
                values[paramName] = SQLiteValueConverter.ToDbValue(prop.GetValue(entity), prop.PropertyType);
            }
            sink.CopyTo(values);

            var targetList = string.Join(", ", target.Select(SQLiteColumnExpressionTranslator.Quote));
            var sql = $"INSERT INTO \"{_tableName}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)}) " +
                      $"ON CONFLICT ({targetList}) DO UPDATE SET {string.Join(", ", updateClauses)};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] UpsertAsync (expression) failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "sqlite.upsert_failed"));
        }
    }

    /// <summary>
    /// Determines the <c>ON CONFLICT</c> target columns. SQLite requires the target to match
    /// one unique index exactly, so when the entity has several candidate keys the caller must
    /// name one rather than have one picked silently.
    /// </summary>
    /// <param name="explicitTarget">Caller-supplied property or column names, or null to infer.</param>
    /// <returns>The column names of the conflict target.</returns>
    /// <exception cref="ArgumentException">An explicit name is not a mapped column.</exception>
    /// <exception cref="InvalidOperationException">
    /// The entity has no usable unique key, or has several and none was specified.
    /// </exception>
    private static IReadOnlyList<string> ResolveConflictTarget(IReadOnlyList<string>? explicitTarget)
    {
        var columns = GetCachedProperties()
            .Select(p => (Property: p, Attr: p.GetCustomAttribute<SQLiteColumnAttribute>()!))
            .Select(c => (c.Property, c.Attr, Name: string.IsNullOrWhiteSpace(c.Attr.ColumnName) ? c.Property.Name : c.Attr.ColumnName))
            .ToList();

        if (explicitTarget is { Count: > 0 })
        {
            return explicitTarget.Select(key =>
            {
                var match = columns.FirstOrDefault(c =>
                    string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Property.Name, key, StringComparison.OrdinalIgnoreCase));
                return match.Property is null
                    ? throw new ArgumentException($"'{key}' is not a mapped column on '{typeof(T).Name}'.", nameof(explicitTarget))
                    : match.Name;
            }).ToArray();
        }

        var candidates = new List<IReadOnlyList<string>>();

        // An auto-increment key cannot arbitrate: the incoming row has no value for it, so it
        // never collides and every call would insert.
        var primary = columns.Where(c => c.Attr.IsPrimaryKey).ToList();
        if (primary.Count > 0 && primary.All(c => !c.Attr.IsAutoIncrement))
            candidates.Add(primary.Select(c => c.Name).ToArray());

        candidates.AddRange(columns
            .Where(c => c.Attr.IsUnique && !c.Attr.IsPrimaryKey)
            .Select(c => (IReadOnlyList<string>)new[] { c.Name }));

        foreach (var index in typeof(T).GetCustomAttributes<SQLiteIndexAttribute>().Where(i => i.IsUnique))
            candidates.Add(index.Columns);

        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Entity '{typeof(T).Name}' needs a non-auto-increment primary key, a [SQLiteColumn(IsUnique = true)] " +
                "property, or a unique [SQLiteIndex] to be upserted with ON CONFLICT.");

        throw new InvalidOperationException(
            $"Entity '{typeof(T).Name}' has {candidates.Count} candidate unique keys " +
            $"({string.Join("; ", candidates.Select(g => string.Join(", ", g)))}). " +
            "SQLite upserts arbitrate on exactly one, so pass the conflictTarget argument to say which.");
    }

    /// <summary>
    /// Retrieves a single entity by its primary key value.
    /// </summary>
    /// <param name="id">The primary key value to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the matching entity, or <c>null</c> if not found.</returns>
    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result<T?>.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var pkCol = pkCols[0].ColumnName;
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE \"{pkCol}\" = @id LIMIT 1;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var entity = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", ConvertKey(id) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFromReader(reader) : null;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves a single entity by a composite primary key. The number and order of
    /// <paramref name="keys"/> must match the entity's primary key columns.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="keys">The ordered set of primary key values.</param>
    /// <returns>A <see cref="Result{T}"/> containing the matching entity, or <c>null</c> if not found.</returns>
    public async Task<Result<T?>> GetByKeysAsync(CancellationToken ct, params object[] keys)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count != keys.Length)
                return Result<T?>.Failure(Error.Validation("sqlite.pk_mismatch",
                    $"Expected {pkCols.Count} key(s), got {keys.Length}."));

            var (clause, parameters) = BuildPrimaryKeyWhere(pkCols, keys);
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE {clause} LIMIT 1;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var entity = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFromReader(reader) : null;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetByKeysAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves all rows from the table up to the specified limit.
    /// </summary>
    /// <param name="limit">Maximum number of rows to return (defaults to 1000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of entities.</returns>
    public async Task<Result<List<T>>> GetAllAsync(int limit = 1000, CancellationToken ct = default)
    {
        try
        {
            var sql = $"SELECT * FROM \"{_tableName}\" LIMIT {limit};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves all rows matching the given predicate expression.
    /// </summary>
    /// <param name="predicate">A LINQ predicate expression translated to a SQL WHERE clause.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of matching entities.</returns>
    public async Task<Result<List<T>>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        try
        {
            var (whereClause, parameters) = SQLiteExpressionVisitor.Parse(predicate);
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE {whereClause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.find_failed"));
        }
    }

    /// <summary>
    /// Updates an existing row by its primary key with the current property values of the entity.
    /// </summary>
    /// <param name="entity">The entity containing updated values. The primary key must be set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var props = GetCachedProperties();
            var setClauses = new List<string>();
            var values = new Dictionary<string, object?>();
            var pkColNames = pkCols.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                var colName = colAttr?.ColumnName ?? prop.Name;
                if (pkColNames.Contains(colName)) continue;
                var paramName = $"@p{setClauses.Count}";
                setClauses.Add($"\"{colName}\" = {paramName}");
                values[paramName] = SQLiteValueConverter.ToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            var pkValues = pkCols.Select(pk => pk.Property.GetValue(entity)).ToArray();
            var (whereClause, whereParams) = BuildPrimaryKeyWhere(pkCols, pkValues!);
            foreach (var kv in whereParams) values[kv.Key] = kv.Value;

            var sql = $"UPDATE \"{_tableName}\" SET {string.Join(", ", setClauses)} WHERE {whereClause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] UpdateAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.update_failed"));
        }
    }

    /// <summary>
    /// Deletes the row with the given single primary key value.
    /// </summary>
    /// <param name="id">The primary key value identifying the row to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> DeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var pkCol = pkCols[0].ColumnName;
            var sql = $"DELETE FROM \"{_tableName}\" WHERE \"{pkCol}\" = @id;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", ConvertKey(id) ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DeleteAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.delete_failed"));
        }
    }

    /// <summary>
    /// Deletes the row identified by a composite primary key. The number and order of
    /// <paramref name="keys"/> must match the entity's primary key columns.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="keys">The ordered set of primary key values.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> DeleteByKeysAsync(CancellationToken ct, params object[] keys)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count != keys.Length)
                return Result.Failure(Error.Validation("sqlite.pk_mismatch",
                    $"Expected {pkCols.Count} key(s), got {keys.Length}."));

            var (clause, parameters) = BuildPrimaryKeyWhere(pkCols, keys);
            var sql = $"DELETE FROM \"{_tableName}\" WHERE {clause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DeleteByKeysAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.delete_failed"));
        }
    }

    /// <summary>
    /// Returns the total number of rows in the table.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the row count.</returns>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var sql = $"SELECT COUNT(*) FROM \"{_tableName}\";";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var count = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "sqlite.count_failed"));
        }
    }

    /// <summary>
    /// Returns a paginated slice of rows from the table.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of rows per page.</param>
    /// <param name="orderBy">Optional column name to sort by.</param>
    /// <param name="desc">When <c>true</c>, sorts in descending order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing a <see cref="PagedResult{T}"/> with items and total count.</returns>
    public async Task<Result<PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderBy = null,
        bool desc = false,
        CancellationToken ct = default)
    {
        if (page < 1 || pageSize < 1)
            return Result<PagedResult<T>>.Failure(Error.Validation(
                "sqlite.paging", "page and pageSize must both be >= 1."));
        try
        {
            var offset = (page - 1) * pageSize;
            var orderClause = orderBy is not null
                ? $" ORDER BY \"{orderBy}\" {(desc ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM \"{_tableName}\";";
            var dataSql = $"SELECT * FROM \"{_tableName}\"{orderClause} LIMIT {pageSize} OFFSET {offset};";
            LogQuery(dataSql);
            var sw = Stopwatch.StartNew();

            var (items, total) = await _connectionManager.ExecuteAsync<(List<T>, long)>(async conn =>
            {
                await using var countCmd = conn.CreateCommand();
                countCmd.CommandText = countSql;
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = dataSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    entities.Add(MapFromReader(reader));

                return (entities, totalCount);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(dataSql, sw.ElapsedMilliseconds);

            return Result<PagedResult<T>>.Success(new PagedResult<T>
            {
                Items = items,
                PageNumber = page,
                PageSize = pageSize,
                TotalItems = total
            });
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetPagedAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Executes a raw SQL SELECT statement and maps the results to a list of entities.
    /// </summary>
    /// <param name="sql">The raw SQL SELECT query to execute.</param>
    /// <param name="params">Optional named parameters to bind (e.g. <c>@id</c> → value).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of mapped entities.</returns>
    public async Task<Result<List<T>>> RawQueryAsync(
        string sql,
        Dictionary<string, object?>? @params = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (@params is not null)
                    foreach (var kv in @params)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RawQueryAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes a raw non-query SQL statement (INSERT, UPDATE, DELETE, etc.).
    /// </summary>
    /// <param name="sql">The raw SQL statement to execute.</param>
    /// <param name="params">Optional named parameters to bind (e.g. <c>@id</c> → value).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of rows affected.</returns>
    public async Task<Result<int>> RawExecuteAsync(
        string sql,
        Dictionary<string, object?>? @params = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (@params is not null)
                    foreach (var kv in @params)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RawExecuteAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "sqlite.execute_failed"));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static T MapFromReader(SqliteDataReader reader)
    {
        var entity = new T();
        var props = GetCachedProperties();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            var prop = props.FirstOrDefault(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                var mapped = attr?.ColumnName ?? p.Name;
                return string.Equals(mapped, colName, StringComparison.OrdinalIgnoreCase);
            });

            if (prop is null || !prop.CanWrite) continue;
            var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
            prop.SetValue(entity, SQLiteValueConverter.FromDbValue(raw, prop.PropertyType));
        }

        return entity;
    }

    private static List<(string ColumnName, PropertyInfo Property)> GetPrimaryKeyColumns()
    {
        return GetCachedProperties()
            .Where(p => p.GetCustomAttribute<SQLiteColumnAttribute>()?.IsPrimaryKey == true)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return (attr?.ColumnName ?? p.Name, p);
            })
            .ToList();
    }

    private static string[] GetPrimaryKeyColumnNames() =>
        GetCachedProperties()
            .Where(p => p.GetCustomAttribute<SQLiteColumnAttribute>()?.IsPrimaryKey == true)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return attr?.ColumnName ?? p.Name;
            })
            .ToArray();

    /// <summary>
    /// Converts a primary-key value the same way the write path does, so a lookup matches
    /// what was stored. Falls back to the raw value when the entity has no single key.
    /// </summary>
    private object? ConvertKey(object? id)
    {
        if (id is null) return null;
        var pk = GetPrimaryKeyColumns().FirstOrDefault();
        return pk.Property is null ? id : SQLiteValueConverter.ToDbValue(id, pk.Property.PropertyType);
    }

    private static (string Clause, Dictionary<string, object?> Parameters) BuildPrimaryKeyWhere(
        List<(string ColumnName, PropertyInfo Property)> pkCols,
        object[] values)
    {
        var parameters = new Dictionary<string, object?>();
        var clauses = new List<string>();

        for (int i = 0; i < pkCols.Count; i++)
        {
            var paramName = $"@pk{i}";
            clauses.Add($"\"{pkCols[i].ColumnName}\" = {paramName}");
            // Through the same converter InsertAsync writes with: a raw Guid binds as a
            // BLOB and a raw DateTime in the provider's own format, neither of which
            // matches what was stored, so the lookup silently found nothing.
            parameters[paramName] = SQLiteValueConverter.ToDbValue(
                values[i], pkCols[i].Property.PropertyType);
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private static PropertyInfo? TryGetAutoIncrementPk() =>
        GetCachedProperties()
            .FirstOrDefault(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return attr?.IsPrimaryKey == true && attr.IsAutoIncrement;
            });

    private static string GetTableName()
    {
        var attr = typeof(T).GetCustomAttribute<SQLiteTableAttribute>();
        return attr?.TableName ?? typeof(T).Name;
    }

    private static PropertyInfo[] GetCachedProperties() =>
        _cachedProperties.GetOrAdd(typeof(T), t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<SQLiteColumnAttribute>() is not null)
             .ToArray());

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs < _slowQueryThresholdMs) return;

        _logger?.Warning($"[SQLite] {string.Format(SQLiteObservability.Strings.SlowQueryDetected, elapsedMs, sql)}");
        SQLiteObservability.RecordSlowQuery(_tableName, sql, elapsedMs);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[SQLite] SQL: {sql}");
    }
}
