using System.Diagnostics;
using System.Linq.Expressions;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Generic repository providing CRUD for entity type <typeparamref name="T"/>.
/// Uses compiled materializers via <see cref="EntityMetadata{T}"/> — no per-row reflection.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;
    private readonly int _maxBatchInsertSize;
    private readonly TransactionScope? _transactionScope;

    // ── Constructors ──────────────────────────────────────────────────────────

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 1000,
        int maxBatchInsertSize = 500)
        : this(connectionManager, logger, connectionId, slowQueryThresholdMs, maxBatchInsertSize, distinguishDuplicateKeyError: false)
    {
    }

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger,
        TransactionScope transactionScope,
        int slowQueryThresholdMs = 1000,
        int maxBatchInsertSize = 500)
        : this(connectionManager, logger, transactionScope, slowQueryThresholdMs, maxBatchInsertSize, distinguishDuplicateKeyError: false)
    {
    }

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger,
        string connectionId,
        int slowQueryThresholdMs,
        int maxBatchInsertSize,
        bool distinguishDuplicateKeyError)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _maxBatchInsertSize = maxBatchInsertSize;
        DistinguishDuplicateKeyError = distinguishDuplicateKeyError;
    }

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger,
        TransactionScope transactionScope,
        int slowQueryThresholdMs,
        int maxBatchInsertSize,
        bool distinguishDuplicateKeyError)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _transactionScope = transactionScope ?? throw new ArgumentNullException(nameof(transactionScope));
        _connectionId = transactionScope.ConnectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _maxBatchInsertSize = maxBatchInsertSize;
        DistinguishDuplicateKeyError = distinguishDuplicateKeyError;
    }

    /// <summary>
    /// When true, duplicate key errors (MySQL error 1062) in <see cref="InsertAsync"/> and
    /// <see cref="InsertManyAsync"/> return the distinct error code <c>mysql.duplicate_key</c>
    /// and log at Warning instead of Error. Default: false.
    /// </summary>
    public bool DistinguishDuplicateKeyError { get; set; }

    /// <summary>
    /// Effective chunk size for <see cref="InsertManyAsync"/> / <see cref="UpsertManyAsync"/>.
    /// Internal: exposed so tests can assert the configured <c>MaxBatchInsertSize</c> actually
    /// reaches the repository, without widening the public surface.
    /// </summary>
    internal int MaxBatchInsertSize => _maxBatchInsertSize;

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Inserts a single entity and returns it (with auto-generated PK populated).</summary>
    public async Task<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var sql = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES ({paramList}); SELECT LAST_INSERT_ID();";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
            {
                var converted = Convert.ChangeType(lastId, pk.Property.PropertyType);
                pk.Set(entity, converted);
            }

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            if (DistinguishDuplicateKeyError && IsDuplicateKeyException(ex))
            {
                _logger?.Warning($"[MySQL2] InsertAsync duplicate key: {ex.Message}", ex);
                return Result<T>.Failure(Error.FromException(ex, "mysql.duplicate_key"));
            }

            _logger?.Error($"[MySQL2] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.insert_failed"));
        }
    }

    /// <summary>
    /// Bulk-inserts a collection of entities using real batched INSERT statements.
    /// Batches of up to the constructor's <c>maxBatchInsertSize</c> are sent per round-trip;
    /// <c>MySQL2Library.GetRepository&lt;T&gt;</c> passes the per-database
    /// <c>MaxBatchInsertSize</c> configuration value (default 500).
    /// </summary>
    public async Task<Result<int>> InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));

            var inserted = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += _maxBatchInsertSize)
                {
                    var end = Math.Min(start + _maxBatchInsertSize, list.Count);
                    var count = end - start;

                    await using var cmd = NewCommand(conn);

                    var valueTuples = BuildBatchParameters(cmd, list, start, count, insertCols);

                    cmd.CommandText = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES {string.Join(", ", valueTuples)};";
                    LogQuery(cmd.CommandText);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    inserted += count;
                }
                return inserted;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MySQL2] Bulk-inserted {inserted} records into `{table}` in {sw.ElapsedMilliseconds}ms");
            QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
        }
        catch (Exception ex)
        {
            if (DistinguishDuplicateKeyError && IsDuplicateKeyException(ex))
            {
                _logger?.Warning($"[MySQL2] InsertManyAsync duplicate key: {ex.Message}", ex);
                return Result<int>.Failure(Error.FromException(ex, "mysql.duplicate_key"));
            }

            _logger?.Error($"[MySQL2] InsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.bulk_insert_failed"));
        }
    }

    /// <summary>
    /// Inserts a single entity, or updates all non-auto-PK columns to the entity's values
    /// if a UNIQUE/PRIMARY-KEY conflict occurs (set semantics). Issues
    /// <c>INSERT ... ON DUPLICATE KEY UPDATE col = VALUES(col)</c> — the <c>VALUES(col)</c>
    /// form, which both MySQL (all versions) and MariaDB accept; the newer <c>... AS new</c>
    /// row-alias syntax is MySQL 8.0.19+ only and is deliberately not used.
    /// On a new insert the auto-PK is refreshed from <c>LAST_INSERT_ID()</c>; on a pure
    /// update the entity's existing PK value is preserved.
    /// </summary>
    public async Task<Result<T>> UpsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            // Use the VALUES(col) function for the conflict update — portable across MySQL
            // (all versions) and MariaDB. The newer `... AS new` row-alias form is MySQL
            // 8.0.19+ only and is rejected by MariaDB.
            var updateList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)} = VALUES({MySqlDialect.Quote(c.ColumnName)})"));
            var sql = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES ({paramList}) ON DUPLICATE KEY UPDATE {updateList}; SELECT LAST_INSERT_ID();";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
            {
                // LAST_INSERT_ID() is 0 on a pure update; only refresh on a new insert.
                var lastIdLong = Convert.ToInt64(lastId);
                if (lastIdLong > 0)
                {
                    var converted = Convert.ChangeType(lastIdLong, pk.Property.PropertyType);
                    pk.Set(entity, converted);
                }
            }

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.upsert_failed"));
        }
    }

    /// <summary>
    /// Bulk-upserts a collection of entities using batched
    /// <c>INSERT ... ON DUPLICATE KEY UPDATE</c> statements (set semantics).
    /// Batches of up to the constructor's <c>maxBatchInsertSize</c> (default 500) are sent
    /// per round-trip.
    /// Returns the total rows-affected count (MySQL counts 1 for each insert and 2 for each
    /// update, so this is not equal to <c>entities.Count</c>).
    /// </summary>
    public async Task<Result<int>> UpsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));
            // VALUES(col) conflict update — portable across MySQL and MariaDB (see UpsertAsync).
            var updateList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)} = VALUES({MySqlDialect.Quote(c.ColumnName)})"));

            var affected = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += _maxBatchInsertSize)
                {
                    var end = Math.Min(start + _maxBatchInsertSize, list.Count);
                    var count = end - start;

                    await using var cmd = NewCommand(conn);

                    var valueTuples = BuildBatchParameters(cmd, list, start, count, insertCols);

                    cmd.CommandText = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES {string.Join(", ", valueTuples)} ON DUPLICATE KEY UPDATE {updateList};";
                    LogQuery(cmd.CommandText);
                    affected += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return affected;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MySQL2] Bulk-upserted {list.Count} records into `{table}` in {sw.ElapsedMilliseconds}ms (rows affected: {affected})");
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.bulk_upsert_failed"));
        }
    }

    /// <summary>
    /// Inserts <paramref name="insertSeed"/> if no UNIQUE/PRIMARY-KEY conflict occurs;
    /// otherwise applies increment / set semantics to the listed properties on conflict.
    /// Properties NOT listed in either array are insert-only — present in the
    /// <c>VALUES</c> clause but absent from <c>ON DUPLICATE KEY UPDATE</c> (so they don't
    /// change on conflict — useful for <c>created_utc</c> style columns). Property names
    /// resolve through <see cref="EntityMetadata{T}"/> so callers can use
    /// <c>nameof(...)</c> for compile-time-safe column references.
    /// </summary>
    /// <param name="insertSeed">Row to INSERT if no conflict. All non-auto-PK columns
    /// go into the <c>VALUES</c> clause.</param>
    /// <param name="incrementProperties">C# property names whose columns should
    /// <c>col = col + new.col</c> on conflict.</param>
    /// <param name="setProperties">C# property names whose columns should <c>col = new.col</c>
    /// on conflict. Defaults to empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows affected (MySQL: 1 = insert, 2 = update with changes, 0 = update with
    /// no change).</returns>
    /// <exception cref="ArgumentException">
    /// A name in <paramref name="incrementProperties"/> or <paramref name="setProperties"/>
    /// does not resolve to a property on <typeparamref name="T"/>, refers to an
    /// auto-increment PK, or appears in both arrays.
    /// </exception>
    public async Task<Result<int>> UpsertWithIncrementsAsync(
        T insertSeed,
        IReadOnlyList<string> incrementProperties,
        IReadOnlyList<string>? setProperties = null,
        CancellationToken ct = default)
    {
        if (insertSeed is null) throw new ArgumentNullException(nameof(insertSeed));
        if (incrementProperties is null) throw new ArgumentNullException(nameof(incrementProperties));
        setProperties ??= Array.Empty<string>();

        var incrementCols = ResolveUpsertColumns(incrementProperties, nameof(incrementProperties));
        var setCols       = ResolveUpsertColumns(setProperties, nameof(setProperties));

        // Reject overlap: a property listed in both arrays would produce ambiguous SQL.
        if (incrementCols.Length > 0 && setCols.Length > 0)
        {
            var incNames = new HashSet<string>(incrementProperties, StringComparer.Ordinal);
            foreach (var name in setProperties)
            {
                if (incNames.Contains(name))
                    throw new ArgumentException(
                        $"Property '{name}' appears in both incrementProperties and setProperties.",
                        nameof(setProperties));
            }
        }

        if (incrementCols.Length == 0 && setCols.Length == 0)
            throw new ArgumentException(
                "At least one of incrementProperties or setProperties must contain entries.",
                nameof(incrementProperties));

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));

            // VALUES(col) refers to the would-be-inserted value; a bare `col` on the LHS/RHS is
            // the existing row. Portable across MySQL and MariaDB (the `AS new` row-alias form is
            // MySQL 8.0.19+ only). No ambiguity without the alias, so no table qualification needed.
            var updateClauses = incrementCols
                .Select(c => $"{MySqlDialect.Quote(c.ColumnName)} = {MySqlDialect.Quote(c.ColumnName)} + VALUES({MySqlDialect.Quote(c.ColumnName)})")
                .Concat(setCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)} = VALUES({MySqlDialect.Quote(c.ColumnName)})"));
            var sql = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES ({paramList}) ON DUPLICATE KEY UPDATE {string.Join(", ", updateClauses)};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(insertSeed), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpsertWithIncrementsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.upsert_increment_failed"));
        }
    }

    /// <summary>
    /// Inserts <paramref name="entity"/>, or — when it collides with an existing row on the
    /// primary key or a UNIQUE index — updates that row with <paramref name="onConflict"/>,
    /// in one statement. The setter receives the existing row and the row that failed to
    /// insert, so the update can build on current values:
    /// <code>
    /// await repo.UpsertAsync(
    ///     new LoginThrottle { Ip = ip, Failures = 1, LastUtc = now },
    ///     (current, incoming) =&gt; new LoginThrottle
    ///     {
    ///         Failures = current.Failures + 1,
    ///         LastUtc  = incoming.LastUtc,
    ///     });
    /// </code>
    /// Columns the setter does not assign keep their current value on conflict. Captured
    /// values are sent as parameters. Emits
    /// <c>INSERT … ON DUPLICATE KEY UPDATE col = expr</c>, with <c>incoming.Col</c> written
    /// as <c>VALUES(col)</c> so it also runs on MariaDB.
    /// <para>MySQL applies the assignments left to right: an assignment that reads
    /// <c>current.X</c> after <c>X</c> was assigned sees the new value. Assign a column after
    /// the ones that read it if you need its old value.</para>
    /// </summary>
    /// <returns>Rows affected (MySQL: 1 = insert, 2 = update with changes, 0 = update with
    /// no change).</returns>
    public async Task<Result<int>> UpsertAsync(
        T entity,
        Expression<Func<T, T, T>> onConflict,
        CancellationToken ct = default)
    {
        if (entity is null) throw new ArgumentNullException(nameof(entity));
        if (onConflict is null) throw new ArgumentNullException(nameof(onConflict));
        if (onConflict.Body is not MemberInitExpression mi || mi.Bindings.Count == 0)
            throw new ArgumentException(
                "onConflict must be a member-init expression like (current, incoming) => new T { Prop = value }.",
                nameof(onConflict));

        var current = onConflict.Parameters[0];
        var incoming = onConflict.Parameters[1];
        var renderers = new Dictionary<ParameterExpression, Func<string, string>>
        {
            [current] = MySqlDialect.Quote,
            [incoming] = c => $"VALUES({MySqlDialect.Quote(c)})",
        };

        var sink = new SqlParameterSink("@ups_");
        var updateClauses = new List<string>();
        foreach (var binding in mi.Bindings)
        {
            if (binding is not MemberAssignment ma)
                throw new ArgumentException("onConflict supports plain property assignments only.", nameof(onConflict));
            var col = ResolveUpsertColumns([ma.Member.Name], nameof(onConflict))[0];
            var target = MySqlDialect.Quote(col.ColumnName);

            if (ReferencesAny(ma.Expression, current, incoming))
            {
                var (sql, _) = SqlExpressionTranslator.Translate(ma.Expression, renderers, sink, col.EffectiveStorageType);
                updateClauses.Add($"{target} = {sql}");
            }
            else
            {
                var value = ClosureEvaluator.Evaluate(ma.Expression);
                updateClauses.Add($"{target} = {(value is null ? "NULL" : sink.Add(value, col.EffectiveStorageType))}");
            }
        }

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => MySqlDialect.Quote(c.ColumnName)));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var sql = $"INSERT INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES ({paramList}) ON DUPLICATE KEY UPDATE {string.Join(", ", updateClauses)};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                foreach (var kv in sink.Parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpsertAsync (expression) failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.upsert_failed"));
        }
    }

    private static bool ReferencesAny(Expression expr, params ParameterExpression[] parameters)
    {
        var walker = new ParameterFinder(parameters);
        walker.Visit(expr);
        return walker.Found;
    }

    private sealed class ParameterFinder(ParameterExpression[] parameters) : ExpressionVisitor
    {
        public bool Found { get; private set; }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (Array.IndexOf(parameters, node) >= 0) Found = true;
            return base.VisitParameter(node);
        }
    }

    private static ColumnMetadata[] ResolveUpsertColumns(IReadOnlyList<string> propertyNames, string paramName)
    {
        var cols = new ColumnMetadata[propertyNames.Count];
        for (var i = 0; i < propertyNames.Count; i++)
        {
            var name = propertyNames[i];
            if (!EntityMetadata<T>.ColumnsByPropertyName.TryGetValue(name, out var col))
                throw new ArgumentException($"Property '{name}' not found on type {typeof(T).Name}", paramName);
            if (col.IsAutoIncrement)
                throw new ArgumentException(
                    $"Property '{name}' is an auto-increment primary key and cannot appear in {paramName}.",
                    paramName);
            cols[i] = col;
        }
        return cols;
    }

    /// <summary>Retrieves an entity by its primary key value.</summary>
    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"SELECT * FROM {MySqlDialect.Quote(table)} WHERE {MySqlDialect.Quote(pk.ColumnName)} = @id{SoftAnd()} LIMIT 1";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                return map(reader);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["@id"] = TypeConverter.ToDbValue(id, pk.EffectiveStorageType) });
            return Result<T?>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities where the specified column equals the given value.</summary>
    public async Task<Result<List<T>>> GetByColumnAsync(string column, object value, CancellationToken ct = default)
    {
        try
        {
            var col = EntityMetadata<T>.RequireColumn(column);
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM {MySqlDialect.Quote(table)} WHERE {MySqlDialect.Quote(col.ColumnName)} = @val{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@val", TypeConverter.ToDbValue(value, col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
                return items;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds,
                new Dictionary<string, object?> { ["@val"] = TypeConverter.ToDbValue(value, col.EffectiveStorageType) });
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetByColumnAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities in the table.</summary>
    public async Task<Result<List<T>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM {MySqlDialect.Quote(table)}{SoftWhere()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
                return items;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves a paged result set.</summary>
    public async Task<Result<Models.PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken ct = default)
    {
        try
        {
            var orderCol = orderByColumn is not null
                ? EntityMetadata<T>.RequireColumn(orderByColumn).ColumnName
                : null;

            var table = EntityMetadata<T>.TableName;
            var offset = (page - 1) * pageSize;
            var orderClause = orderCol is not null
                ? $" ORDER BY {MySqlDialect.Quote(orderCol)} {(descending ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM {MySqlDialect.Quote(table)}{SoftWhere()}";
            var dataSql = $"SELECT * FROM {MySqlDialect.Quote(table)}{SoftWhere()}{orderClause} LIMIT {pageSize} OFFSET {offset}";

            LogQuery(dataSql);
            var sw = Stopwatch.StartNew();

            var (items, total) = await ExecuteAsync(async conn =>
            {
                await using var countCmd = NewCommand(conn);
                countCmd.CommandText = countSql;
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = NewCommand(conn);
                cmd.CommandText = dataSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) entities.Add(map(reader));
                return (entities, totalCount);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(dataSql, sw.ElapsedMilliseconds);

            return Result<Models.PagedResult<T>>.Success(new Models.PagedResult<T>
            {
                Items = items,
                PageNumber = page,
                PageSize = pageSize,
                TotalItems = total
            });
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetPagedAsync failed: {ex.Message}", ex);
            return Result<Models.PagedResult<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>
    /// Returns the total row count for the table. Like every other repository read this applies
    /// the <see cref="Models.SoftDeleteAttribute"/> filter, so soft-deleted rows are excluded and
    /// the count agrees with <see cref="GetAllAsync"/> and <see cref="GetPagedAsync"/>.
    /// Use <c>Query&lt;T&gt;().IncludeDeleted().CountAsync()</c> to count deleted rows as well.
    /// </summary>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT COUNT(*) FROM {MySqlDialect.Quote(table)}{SoftWhere()}";
            LogQuery(sql);

            var count = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mysql.count_failed"));
        }
    }

    /// <summary>Updates an existing entity by PK.</summary>
    public async Task<Result<T>> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var setCols = EntityMetadata<T>.Columns.Where(c => c != pk).ToArray();
            var setClauses = string.Join(", ", setCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)} = @{c.ColumnName}"));
            var sql = $"UPDATE {MySqlDialect.Quote(table)} SET {setClauses} WHERE {MySqlDialect.Quote(pk.ColumnName)} = @__pk";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var col in setCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                cmd.Parameters.Add(TypeConverter.CreateParameter("@__pk", TypeConverter.ToDbValue(pk.Get(entity), pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpdateAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>
    /// Deletes an entity by its primary key. For a <see cref="Models.SoftDeleteAttribute"/>
    /// entity this is a <b>soft delete</b> — it sets the timestamp column to UtcNow instead of
    /// removing the row (no-op if already deleted). Use <see cref="HardDeleteAsync"/> to remove
    /// the row physically. Returns true when a row was affected.
    /// </summary>
    public async Task<Result<bool>> DeleteAsync(object id, CancellationToken ct = default)
    {
        var soft = EntityMetadata<T>.SoftDeleteColumn;
        return soft is null
            ? await HardDeleteAsync(id, ct).ConfigureAwait(false)
            : await SoftDeleteAsync(id, soft, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Physically removes the row by primary key, even for soft-delete entities.
    /// </summary>
    public async Task<Result<bool>> HardDeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"DELETE FROM {MySqlDialect.Quote(table)} WHERE {MySqlDialect.Quote(pk.ColumnName)} = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] HardDeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.delete_failed"));
        }
    }

    private async Task<Result<bool>> SoftDeleteAsync(object id, ColumnMetadata soft, CancellationToken ct)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"UPDATE {MySqlDialect.Quote(table)} SET {MySqlDialect.Quote(soft.ColumnName)} = @now " +
                      $"WHERE {MySqlDialect.Quote(pk.ColumnName)} = @id AND {MySqlDialect.Quote(soft.ColumnName)} IS NULL";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@now", DateTime.UtcNow, soft.Attribute, soft.Property.PropertyType));
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] Soft DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.delete_failed"));
        }
    }

    // Soft-delete read-filter fragments — empty when the entity has no [SoftDelete].
    private static string SoftAnd()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" AND {MySqlDialect.Quote(c.ColumnName)} IS NULL" : string.Empty;

    private static string SoftWhere()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" WHERE {MySqlDialect.Quote(c.ColumnName)} IS NULL" : string.Empty;

    /// <summary>
    /// Atomically adjusts a numeric column by <paramref name="delta"/>. Negative for decrement.
    /// </summary>
    public async Task<Result<int>> AdjustAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty delta,
        CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var colName = MySqlExpressionVisitor.TranslateSelector(propertySelector);
            var deltaCol = EntityMetadata<T>.RequireColumn(colName);
            var sql = $"UPDATE {MySqlDialect.Quote(table)} SET {MySqlDialect.Quote(colName)} = {MySqlDialect.Quote(colName)} + @delta WHERE {MySqlDialect.Quote(pk.ColumnName)} = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@delta", TypeConverter.ToDbValue(delta, deltaCol.EffectiveStorageType), deltaCol.Attribute, deltaCol.Property.PropertyType));
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] AdjustAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>Increment a numeric column by <paramref name="amount"/>.</summary>
    public Task<Result<int>> IncrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
        => AdjustAsync(id, propertySelector, amount, ct);

    /// <summary>Decrement a numeric column by <paramref name="amount"/>.</summary>
    public Task<Result<int>> DecrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
    {
        // Negate via dynamic since TProperty is an unconstrained numeric.
        dynamic d = amount!;
        return AdjustAsync(id, propertySelector, (TProperty)(-d), ct);
    }

    /// <summary>Returns all entities matching the given LINQ predicate.</summary>
    public async Task<Result<List<T>>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var (whereClause, parameters) = MySqlExpressionVisitor.Translate(predicate);
            var sql = $"SELECT * FROM {MySqlDialect.Quote(table)} WHERE {whereClause}{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = NewCommand(conn);
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
                return items;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds, parameters);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.find_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a command on <paramref name="conn"/>, enlists it in the repository's transaction
    /// scope when there is one, and applies the connection's configured query timeout.
    /// </summary>
    private MySqlCommand NewCommand(MySqlConnection conn)
    {
        var cmd = conn.CreateCommand();
        if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
        _connectionManager.ApplyCommandTimeout(cmd, _connectionId);
        return cmd;
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<MySqlConnection, Task<TResult>> action,
        CancellationToken ct = default)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        // The token has to reach ExecuteWithConnectionAsync, otherwise opening the
        // connection (and any transient-failure retry around it) ignores cancellation.
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId, ct).ConfigureAwait(false);
    }

    private void LogSlowQuery(
        string sql, long elapsedMs, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        QueryObservability.RecordExecuted(_connectionId, sql, elapsedMs, rowCount: -1, cacheHit: false);
        if (elapsedMs >= _slowQueryThresholdMs)
            QueryObservability.RecordSlow(_connectionManager, _connectionId, sql, elapsedMs, parameters);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MySQL2] SQL: {sql}");
    }

    /// <summary>
    /// Inserts a single entity, ignoring duplicate-key conflicts (UNIQUE index or PRIMARY KEY).
    /// If the row was inserted, auto-generated PK (if any) is set on <paramref name="entity"/>
    /// and <see cref="InsertOutcome.Inserted"/> is returned. If a duplicate exists,
    /// <see cref="InsertOutcome.Duplicate"/> is returned without raising an error.
    /// Non-duplicate errors (data truncation, connection failure, etc.) return a failure Result.
    /// </summary>
    /// <remarks>
    /// Implementation choice: We use <c>INSERT IGNORE</c> while validating connection warnings.
    /// In MySqlConnector, connections use the default "found rows" semantics (<c>UseAffectedRows=false</c>),
    /// which causes <c>INSERT ... ON DUPLICATE KEY UPDATE</c> to report matched rows rather than changed
    /// rows, returning 1 for duplicates as well as new inserts.
    /// In contrast, <c>INSERT IGNORE</c> accurately returns 1 for inserted rows and 0 for ignored duplicates
    /// under found-rows semantics.
    /// To ensure <c>INSERT IGNORE</c> does not silently swallow severe data errors (such as data truncation or
    /// missing NOT NULL values), we intercept server warnings on the connection: any warning other than 1062
    /// (duplicate key entry) is converted into a thrown exception and surfaced as an error.
    /// </remarks>
    public async Task<Result<InsertOutcome>> InsertIgnoreAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var (table, insertCols, columnList, _) = GetInsertMetadata();
            var paramList = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var sql = $"INSERT IGNORE INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES ({paramList});";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var (affected, lastId) = await ExecuteInsertIgnoreAtomicAsync<(int, long)>(
                async (conn, tx, throwIfWarned) =>
            {
                await using var cmd = NewCommand(conn);
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter(
                        $"@{col.ColumnName}",
                        TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType),
                        col.Attribute,
                        col.Property.PropertyType));
                var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                throwIfWarned();
                return (rows, cmd.LastInsertedId);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            if (affected > 0)
            {
                var pk = EntityMetadata<T>.PrimaryKey;
                if (pk is not null && pk.IsAutoIncrement && lastId > 0)
                {
                    var converted = Convert.ChangeType(lastId, pk.Property.PropertyType);
                    pk.Set(entity, converted);
                }

                QueryCache.Invalidate(table);
                return Result<InsertOutcome>.Success(InsertOutcome.Inserted);
            }

            _logger?.Debug($"[MySQL2] InsertIgnoreAsync ignored duplicate key for `{table}`");
            return Result<InsertOutcome>.Success(InsertOutcome.Duplicate);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] InsertIgnoreAsync failed: {ex.Message}", ex);
            return Result<InsertOutcome>.Failure(Error.FromException(ex, "mysql.insert_failed"));
        }
    }

    /// <summary>
    /// Bulk-inserts a collection of entities, ignoring duplicate-key conflicts (UNIQUE index or PRIMARY KEY).
    /// Batches of up to <c>maxBatchInsertSize</c> are sent per round-trip.
    /// Returns the number of rows actually inserted (duplicates contribute 0 to the count).
    /// </summary>
    public async Task<Result<int>> InsertManyIgnoreAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var (table, insertCols, columnList, _) = GetInsertMetadata();

            var inserted = 0;
            var sw = Stopwatch.StartNew();

            inserted = await ExecuteInsertIgnoreAtomicAsync<int>(async (conn, tx, throwIfWarned) =>
            {
                var total = 0;
                for (var start = 0; start < list.Count; start += _maxBatchInsertSize)
                {
                    var end = Math.Min(start + _maxBatchInsertSize, list.Count);
                    var count = end - start;

                    await using var cmd = NewCommand(conn);
                    cmd.Transaction = tx;
                    var valueTuples = BuildBatchParameters(cmd, list, start, count, insertCols);

                    cmd.CommandText = $"INSERT IGNORE INTO {MySqlDialect.Quote(table)} ({columnList}) VALUES {string.Join(", ", valueTuples)};";
                    LogQuery(cmd.CommandText);
                    var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    throwIfWarned();
                    total += affected;
                }
                return total;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MySQL2] Bulk-inserted (ignore duplicates) {inserted} records into `{table}` in {sw.ElapsedMilliseconds}ms");
            if (inserted > 0)
                QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] InsertManyIgnoreAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.bulk_insert_failed"));
        }
    }

    private const string InsertIgnoreSavepoint = "cl_mysql2_insert_ignore";

    /// <summary>
    /// Runs INSERT IGNORE statement(s) atomically. <c>INSERT IGNORE</c> downgrades not only
    /// duplicate keys but also errors such as truncation (1265/1406) or a missing NOT NULL
    /// value (1364) to warnings, and still writes the affected row. Any warning other than
    /// ER_DUP_ENTRY (1062) therefore fails the call and everything the call wrote is rolled
    /// back, so a failure Result always means nothing was written. Without an ambient
    /// <see cref="TransactionScope"/> the call uses its own transaction; inside one it rolls
    /// back to a savepoint, leaving earlier work in the caller's transaction untouched.
    /// </summary>
    private Task<TResult> ExecuteInsertIgnoreAtomicAsync<TResult>(
        Func<MySqlConnection, MySqlTransaction, Action, Task<TResult>> body,
        CancellationToken ct)
        => ExecuteAsync(async conn =>
        {
            Exception? warning = null;
            void OnInfoMessage(object sender, MySqlInfoMessageEventArgs args)
            {
                foreach (var err in args.Errors)
                {
                    if (err.ErrorCode == MySqlErrorCode.DuplicateKeyEntry) continue;
                    warning ??= new InvalidOperationException(
                        $"INSERT IGNORE produced MySQL warning {(int)err.ErrorCode}: {err.Message}");
                    break;
                }
            }
            void ThrowIfWarned()
            {
                if (warning is not null) throw warning;
            }

            var ownTransaction = _transactionScope is null
                ? await conn.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;
            var transaction = ownTransaction ?? _transactionScope!.Transaction;
            try
            {
                if (ownTransaction is null)
                    await transaction.SaveAsync(InsertIgnoreSavepoint, ct).ConfigureAwait(false);

                conn.InfoMessage += OnInfoMessage;
                TResult result;
                try
                {
                    result = await body(conn, transaction, ThrowIfWarned).ConfigureAwait(false);
                }
                finally
                {
                    conn.InfoMessage -= OnInfoMessage;
                }

                if (ownTransaction is not null)
                    await ownTransaction.CommitAsync(ct).ConfigureAwait(false);
                else
                    await transaction.ReleaseAsync(InsertIgnoreSavepoint, ct).ConfigureAwait(false);
                return result;
            }
            catch
            {
                try
                {
                    if (ownTransaction is not null)
                        await ownTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    else
                        await transaction.RollbackAsync(InsertIgnoreSavepoint, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger?.Warning($"[MySQL2] INSERT IGNORE rollback failed: {rollbackEx.Message}");
                }
                throw;
            }
            finally
            {
                if (ownTransaction is not null)
                    await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }, ct);

    private static (string Table, ColumnMetadata[] InsertCols, string ColumnList, string ConflictCol) GetInsertMetadata()
    {
        var table = EntityMetadata<T>.TableName;
        var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
        var columnList = string.Join(", ", insertCols.Select(c => $"{MySqlDialect.Quote(c.ColumnName)}"));
        var conflictCol = EntityMetadata<T>.PrimaryKey?.ColumnName
            ?? (insertCols.Length > 0 ? insertCols[0].ColumnName : EntityMetadata<T>.Columns[0].ColumnName);
        return (table, insertCols, columnList, conflictCol);
    }

    private static string[] BuildBatchParameters(MySqlCommand cmd, IList<T> list, int start, int count, ColumnMetadata[] insertCols)
    {
        var valueTuples = new string[count];
        for (var i = 0; i < count; i++)
        {
            var entity = list[start + i];
            var tupleParts = new string[insertCols.Length];
            for (var j = 0; j < insertCols.Length; j++)
            {
                var paramName = $"@p_{i}_{j}";
                tupleParts[j] = paramName;
                cmd.Parameters.Add(TypeConverter.CreateParameter(
                    paramName,
                    TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType),
                    insertCols[j].Attribute,
                    insertCols[j].Property.PropertyType));
            }
            valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
        }
        return valueTuples;
    }

    private static bool IsDuplicateKeyException(Exception ex)
    {
        if (ex is MySqlException { Number: 1062 }) return true;
        if (ex.InnerException is MySqlException { Number: 1062 }) return true;
        return false;
    }
}
