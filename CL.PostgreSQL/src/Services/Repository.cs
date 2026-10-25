using System.Diagnostics;
using System.Linq.Expressions;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Npgsql;

namespace CL.PostgreSQL.Services;

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
    /// When true, a unique violation (SQLSTATE 23505) in <see cref="InsertAsync"/> and
    /// <see cref="InsertManyAsync"/> returns error code <c>postgresql.duplicate_key</c> and logs at
    /// Warning instead of <c>postgresql.insert_failed</c> / <c>postgresql.bulk_insert_failed</c> at Error.
    /// </summary>
    public bool DistinguishDuplicateKeyError { get; set; }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Inserts a single entity and returns it (with auto-generated PK populated).</summary>
    public async Task<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            // RETURNING gives the generated key back on the same round trip. Unlike
            // LAST_INSERT_ID() it works for any PK type (uuid, text, composite), not just
            // an integer sequence, and it is unambiguous under concurrency.
            var pkCol = EntityMetadata<T>.PrimaryKey;
            var returning = pkCol is null ? string.Empty : $" RETURNING {PostgreSqlDialect.Quote(pkCol.ColumnName)}";
            var sql = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES ({paramList}){returning};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
                pk.Set(entity, TypeConverter.FromDbValue(lastId, pk.Property.PropertyType, pk.EffectiveStorageType));

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            if (DistinguishDuplicateKeyError && IsDuplicateKeyException(ex))
            {
                _logger?.Warning($"[PostgreSQL] InsertAsync duplicate key: {ex.Message}", ex);
                return Result<T>.Failure(Error.FromException(ex, "postgresql.duplicate_key"));
            }

            _logger?.Error($"[PostgreSQL] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "postgresql.insert_failed"));
        }
    }

    /// <summary>
    /// Bulk-inserts a collection of entities using real batched INSERT statements.
    /// Batches of up to <c>maxBatchInsertSize</c> (default 500) are sent per round-trip.
    /// </summary>
    public async Task<Result<int>> InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            // Never exceed the wire protocol's 65535-parameter ceiling, whatever the config says.
            var batchSize = Math.Min(_maxBatchInsertSize, PostgreSqlDialect.MaxBatchRows(insertCols.Length));

            var inserted = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += batchSize)
                {
                    var end = Math.Min(start + batchSize, list.Count);
                    var count = end - start;

                    await using var cmd = conn.CreateCommand(_connectionId);
                    if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;

                    var valueTuples = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entity = list[start + i];
                        var tupleParts = new string[insertCols.Length];
                        for (var j = 0; j < insertCols.Length; j++)
                        {
                            var paramName = $"@p_{i}_{j}";
                            tupleParts[j] = paramName;
                            cmd.Parameters.Add(TypeConverter.CreateParameter(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType), insertCols[j].Attribute, insertCols[j].Property.PropertyType));
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES {string.Join(", ", valueTuples)};";
                    LogQuery(cmd.CommandText);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    inserted += count;
                }
                return inserted;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[PostgreSQL] Bulk-inserted {inserted} records into `{table}` in {sw.ElapsedMilliseconds}ms");
            QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
        }
        catch (Exception ex)
        {
            if (DistinguishDuplicateKeyError && IsDuplicateKeyException(ex))
            {
                _logger?.Warning($"[PostgreSQL] InsertManyAsync duplicate key: {ex.Message}", ex);
                return Result<int>.Failure(Error.FromException(ex, "postgresql.duplicate_key"));
            }

            _logger?.Error($"[PostgreSQL] InsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.bulk_insert_failed"));
        }
    }

    /// <summary>
    /// Inserts a single entity, or updates all non-auto-PK columns to the entity's values
    /// if a conflict occurs on <paramref name="conflictTarget"/> (set semantics). Issues
    /// <c>INSERT ... ON CONFLICT (...) DO UPDATE SET ... = EXCLUDED....</c> and returns the
    /// row's primary key via <c>RETURNING</c>, so the entity's auto-PK is populated on both
    /// the insert and the update path.
    /// </summary>
    /// <param name="entity">The row to insert or merge.</param>
    /// <param name="conflictTarget">
    /// Property or column names forming the unique key to match on. When null the entity's
    /// primary key is used, or its single unique key if the PK is auto-generated. Unlike
    /// MySQL's <c>ON DUPLICATE KEY UPDATE</c>, PostgreSQL requires one explicit conflict
    /// target and will not match "any unique key" — see <see cref="ResolveConflictTarget"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<T>> UpsertAsync(
        T entity,
        IReadOnlyList<string>? conflictTarget = null,
        CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var target = ResolveConflictTarget(conflictTarget);
            var targetList = string.Join(", ", target.Select(c => PostgreSqlDialect.Quote(c.ColumnName)));
            var targetNames = target.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // EXCLUDED is the row proposed for insertion. Conflict-target columns are left out
            // of the SET list: assigning a column to itself is pointless and, for a column the
            // arbiter index covers, can defeat the index-only update path.
            var updateList = string.Join(", ", insertCols
                .Where(c => !targetNames.Contains(c.ColumnName))
                .Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)} = EXCLUDED.{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var pkCol = EntityMetadata<T>.PrimaryKey;
            var returning = pkCol is null ? string.Empty : $" RETURNING {PostgreSqlDialect.Quote(pkCol.ColumnName)}";
            // DO NOTHING when every column is part of the key: DO UPDATE with an empty SET is a
            // syntax error, and there would be nothing to change anyway.
            var action = updateList.Length == 0 ? "DO NOTHING" : $"DO UPDATE SET {updateList}";
            var sql = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES ({paramList}) ON CONFLICT ({targetList}) {action}{returning};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            // RETURNING yields the key on both paths, but produces no row at all when the
            // statement degenerated to DO NOTHING and a conflict occurred.
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
                pk.Set(entity, TypeConverter.FromDbValue(lastId, pk.Property.PropertyType, pk.EffectiveStorageType));

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] UpsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "postgresql.upsert_failed"));
        }
    }

    /// <summary>
    /// Bulk-upserts a collection of entities using batched
    /// <c>INSERT ... ON CONFLICT (...) DO UPDATE</c> statements (set semantics).
    /// Batches of up to <c>maxBatchInsertSize</c> are sent per round-trip, further capped so a
    /// single statement stays under PostgreSQL's 65535-parameter limit.
    /// Returns the total rows affected; unlike MySQL, PostgreSQL counts each affected row once,
    /// so for a conflict-free batch this equals <c>entities.Count</c>.
    /// </summary>
    /// <param name="entities">Rows to insert or merge.</param>
    /// <param name="conflictTarget">Unique key to match on; see <see cref="UpsertAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<int>> UpsertManyAsync(
        IEnumerable<T> entities,
        IReadOnlyList<string>? conflictTarget = null,
        CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var target = ResolveConflictTarget(conflictTarget);
            var targetList = string.Join(", ", target.Select(c => PostgreSqlDialect.Quote(c.ColumnName)));
            var targetNames = target.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updateList = string.Join(", ", insertCols
                .Where(c => !targetNames.Contains(c.ColumnName))
                .Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)} = EXCLUDED.{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var action = updateList.Length == 0 ? "DO NOTHING" : $"DO UPDATE SET {updateList}";
            // Never exceed the wire protocol's parameter ceiling, whatever the configured batch size.
            var batchSize = Math.Min(_maxBatchInsertSize, PostgreSqlDialect.MaxBatchRows(insertCols.Length));

            var affected = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += batchSize)
                {
                    var end = Math.Min(start + batchSize, list.Count);
                    var count = end - start;

                    await using var cmd = conn.CreateCommand(_connectionId);
                    if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;

                    var valueTuples = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entity = list[start + i];
                        var tupleParts = new string[insertCols.Length];
                        for (var j = 0; j < insertCols.Length; j++)
                        {
                            var paramName = $"@p_{i}_{j}";
                            tupleParts[j] = paramName;
                            cmd.Parameters.Add(TypeConverter.CreateParameter(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType), insertCols[j].Attribute, insertCols[j].Property.PropertyType));
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES {string.Join(", ", valueTuples)} ON CONFLICT ({targetList}) {action};";
                    LogQuery(cmd.CommandText);
                    affected += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return affected;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[PostgreSQL] Bulk-upserted {list.Count} records into `{table}` in {sw.ElapsedMilliseconds}ms (rows affected: {affected})");
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] UpsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.bulk_upsert_failed"));
        }
    }

    /// <summary>
    /// Inserts <paramref name="insertSeed"/> if no UNIQUE/PRIMARY-KEY conflict occurs;
    /// otherwise applies increment / set semantics to the listed properties on conflict.
    /// Properties NOT listed in either array are insert-only — present in the
    /// <c>VALUES</c> clause but absent from the <c>ON CONFLICT … DO UPDATE SET</c> list (so
    /// they don't change on conflict — useful for <c>created_utc</c> style columns). Property names
    /// resolve through <see cref="EntityMetadata{T}"/> so callers can use
    /// <c>nameof(...)</c> for compile-time-safe column references.
    /// </summary>
    /// <param name="insertSeed">Row to INSERT if no conflict. All non-auto-PK columns
    /// go into the <c>VALUES</c> clause.</param>
    /// <param name="incrementProperties">C# property names whose columns should
    /// <c>col = col + new.col</c> on conflict.</param>
    /// <param name="setProperties">C# property names whose columns should
    /// <c>col = EXCLUDED.col</c> on conflict. Defaults to empty.</param>
    /// <param name="conflictTarget">Unique key to arbitrate on; see <see cref="UpsertAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows affected. PostgreSQL counts an <c>ON CONFLICT … DO UPDATE</c> row once,
    /// so this is 1 whether the row was inserted or updated.</returns>
    /// <exception cref="ArgumentException">
    /// A name in <paramref name="incrementProperties"/> or <paramref name="setProperties"/>
    /// does not resolve to a property on <typeparamref name="T"/>, refers to an
    /// auto-increment PK, or appears in both arrays.
    /// </exception>
    public async Task<Result<int>> UpsertWithIncrementsAsync(
        T insertSeed,
        IReadOnlyList<string> incrementProperties,
        IReadOnlyList<string>? setProperties = null,
        IReadOnlyList<string>? conflictTarget = null,
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
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));

            var target = ResolveConflictTarget(conflictTarget);
            var targetList = string.Join(", ", target.Select(c => PostgreSqlDialect.Quote(c.ColumnName)));
            // EXCLUDED.col is the value proposed for insertion; the table-qualified name is the
            // existing row, so `counter = tbl.counter + EXCLUDED.counter` accumulates.
            var qualified = EntityMetadata<T>.QualifiedTableNameFor(_connectionId);
            var updateClauses = incrementCols
                .Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)} = {qualified}.{PostgreSqlDialect.Quote(c.ColumnName)} + EXCLUDED.{PostgreSqlDialect.Quote(c.ColumnName)}")
                .Concat(setCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)} = EXCLUDED.{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var sql = $"INSERT INTO {qualified} ({columnList}) VALUES ({paramList}) ON CONFLICT ({targetList}) DO UPDATE SET {string.Join(", ", updateClauses)};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] UpsertWithIncrementsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.upsert_increment_failed"));
        }
    }

    /// <summary>
    /// Determines the <c>ON CONFLICT</c> arbiter columns.
    /// <para>
    /// PostgreSQL requires an explicit conflict target and, unlike MySQL's
    /// <c>ON DUPLICATE KEY UPDATE</c>, will not arbitrate over "whichever unique key
    /// happens to collide". When the entity has exactly one candidate key the choice is
    /// unambiguous and is made automatically; when it has several, the caller must say
    /// which one it means rather than have one picked silently.
    /// </para>
    /// </summary>
    /// <param name="explicitTarget">Caller-supplied property or column names, or null to infer.</param>
    /// <exception cref="InvalidOperationException">
    /// The entity has no usable unique key, or has several and none was specified.
    /// </exception>
    private static IReadOnlyList<ColumnMetadata> ResolveConflictTarget(IReadOnlyList<string>? explicitTarget)
    {
        if (explicitTarget is { Count: > 0 })
            return explicitTarget.Select(EntityMetadata<T>.RequireColumn).ToArray();

        var candidates = new List<IReadOnlyList<ColumnMetadata>>();

        // A generated primary key cannot arbitrate: the incoming row has no value for it yet,
        // so it can never collide and every call would insert.
        var primary = EntityMetadata<T>.Columns.Where(c => c.IsPrimary && !c.IsAutoIncrement).ToArray();
        if (primary.Length > 0) candidates.Add(primary);

        candidates.AddRange(EntityMetadata<T>.Columns
            .Where(c => c.Attribute?.Unique == true)
            .Select(c => (IReadOnlyList<ColumnMetadata>)new[] { c }));

        foreach (var index in EntityMetadata<T>.CompositeIndexes.Where(i => i.Unique))
            candidates.Add(index.ColumnNames.Select(EntityMetadata<T>.RequireColumn).ToArray());

        if (candidates.Count == 1) return candidates[0];

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Entity '{typeof(T).Name}' needs a non-generated primary key, a [Column(Unique = true)] " +
                $"property, or a unique [CompositeIndex] to be upserted.");

        throw new InvalidOperationException(
            $"Entity '{typeof(T).Name}' has {candidates.Count} candidate unique keys " +
            $"({string.Join("; ", candidates.Select(g => string.Join(", ", g.Select(c => c.ColumnName))))}). " +
            $"PostgreSQL upserts arbitrate on exactly one, so pass the conflictTarget argument to say which.");
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
            var sql = $"SELECT * FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} WHERE {PostgreSqlDialect.Quote(pk.ColumnName)} = @id{SoftAnd()} LIMIT 1";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                return map(reader);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities where the specified column equals the given value.</summary>
    public async Task<Result<List<T>>> GetByColumnAsync(string column, object value, CancellationToken ct = default)
    {
        try
        {
            var col = EntityMetadata<T>.RequireColumn(column);
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} WHERE {PostgreSqlDialect.Quote(col.ColumnName)} = @val{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@val", TypeConverter.ToDbValue(value, col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
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
            _logger?.Error($"[PostgreSQL] GetByColumnAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities in the table.</summary>
    public async Task<Result<List<T>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)}{SoftWhere()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
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
                ? $" ORDER BY {PostgreSqlDialect.Quote(orderCol)} {(descending ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)}{SoftWhere()}";
            var dataSql = $"SELECT * FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)}{SoftWhere()}{orderClause} LIMIT {pageSize} OFFSET {offset}";

            LogQuery(dataSql);
            var sw = Stopwatch.StartNew();

            var (items, total) = await ExecuteAsync(async conn =>
            {
                await using var countCmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) countCmd.Transaction = _transactionScope.Transaction;
                countCmd.CommandText = countSql;
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] GetPagedAsync failed: {ex.Message}", ex);
            return Result<Models.PagedResult<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    /// <summary>
    /// Returns the total row count for the table. On a <c>[SoftDelete]</c> entity, rows whose
    /// delete timestamp is set are excluded — the same filter <see cref="GetAllAsync"/> and
    /// <see cref="GetPagedAsync"/> apply. Use <c>Query&lt;T&gt;().IncludeDeleted().CountAsync()</c>
    /// to count them too.
    /// </summary>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT COUNT(*) FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)}{SoftWhere()}";
            LogQuery(sql);

            var count = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "postgresql.count_failed"));
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
            var setClauses = string.Join(", ", setCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)} = @{c.ColumnName}"));
            var sql = $"UPDATE {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} SET {setClauses} WHERE {PostgreSqlDialect.Quote(pk.ColumnName)} = @__pk";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] UpdateAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "postgresql.update_failed"));
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
            var sql = $"DELETE FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} WHERE {PostgreSqlDialect.Quote(pk.ColumnName)} = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.Add(TypeConverter.CreateParameter("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType), pk.Attribute, pk.Property.PropertyType));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] HardDeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.delete_failed"));
        }
    }

    private async Task<Result<bool>> SoftDeleteAsync(object id, ColumnMetadata soft, CancellationToken ct)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"UPDATE {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} SET {PostgreSqlDialect.Quote(soft.ColumnName)} = @now " +
                      $"WHERE {PostgreSqlDialect.Quote(pk.ColumnName)} = @id AND {PostgreSqlDialect.Quote(soft.ColumnName)} IS NULL";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] Soft DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.delete_failed"));
        }
    }

    // Soft-delete read-filter fragments — empty when the entity has no [SoftDelete].
    private static string SoftAnd()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" AND {PostgreSqlDialect.Quote(c.ColumnName)} IS NULL" : string.Empty;

    private static string SoftWhere()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" WHERE {PostgreSqlDialect.Quote(c.ColumnName)} IS NULL" : string.Empty;

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
            var colName = PostgreSqlExpressionVisitor.TranslateSelector(propertySelector);
            var deltaCol = EntityMetadata<T>.RequireColumn(colName);
            var sql = $"UPDATE {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} SET {PostgreSqlDialect.Quote(colName)} = {PostgreSqlDialect.Quote(colName)} + @delta WHERE {PostgreSqlDialect.Quote(pk.ColumnName)} = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] AdjustAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.update_failed"));
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
            var (whereClause, parameters, maxInList) = PostgreSqlExpressionVisitor.TranslateWithStats(predicate);
            var inLimit = PostgreSqlRuntimeOptions.For(_connectionId).MaxInClauseValues;
            if (maxInList > inLimit)
                _logger?.Warning(
                    $"[PostgreSQL] [{_connectionId}] {typeof(T).Name}: generated IN list has {maxInList} values, " +
                    $"above the configured MaxInClauseValues of {inLimit}. The list is sent whole.");
            var sql = $"SELECT * FROM {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} WHERE {whereClause}{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
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
            _logger?.Error($"[PostgreSQL] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.find_failed"));
        }
    }

    // ── Insert ignoring duplicates ───────────────────────────────────────────

    /// <summary>
    /// Inserts a single entity, ignoring a duplicate-key conflict (UNIQUE index or PRIMARY KEY).
    /// If the row was inserted, the auto-generated PK (if any) is set on <paramref name="entity"/>
    /// and <see cref="InsertOutcome.Inserted"/> is returned. If a duplicate exists,
    /// <see cref="InsertOutcome.Duplicate"/> is returned without raising an error.
    /// Any other error returns a failure Result and nothing is written.
    /// </summary>
    /// <remarks>
    /// Issues <c>INSERT ... ON CONFLICT DO NOTHING RETURNING ...</c>: with no conflict target every
    /// unique constraint and unique index is an arbiter, so any duplicate key is skipped server-side.
    /// Inside an explicit <see cref="TransactionScope"/> the statement runs under a savepoint, so a
    /// failure does not abort the caller's transaction and earlier work in it is left untouched.
    /// </remarks>
    public async Task<Result<InsertOutcome>> InsertIgnoreAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var pk = EntityMetadata<T>.PrimaryKey;
            // A skipped row returns no row at all, so RETURNING tells inserted from duplicate.
            var returning = pk is null ? "RETURNING 1" : $"RETURNING {PostgreSqlDialect.Quote(pk.ColumnName)}";
            var sql = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES ({paramList}) ON CONFLICT DO NOTHING {returning};";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteInsertIgnoreAtomicAsync(async (conn, tx) =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute, col.Property.PropertyType));
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            if (lastId is null || lastId is DBNull)
            {
                _logger?.Debug($"[PostgreSQL] InsertIgnoreAsync ignored duplicate key for `{table}`");
                return Result<InsertOutcome>.Success(InsertOutcome.Duplicate);
            }

            if (pk is not null && pk.IsAutoIncrement)
                pk.Set(entity, TypeConverter.FromDbValue(lastId, pk.Property.PropertyType, pk.EffectiveStorageType));

            QueryCache.Invalidate(table);
            return Result<InsertOutcome>.Success(InsertOutcome.Inserted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] InsertIgnoreAsync failed: {ex.Message}", ex);
            return Result<InsertOutcome>.Failure(Error.FromException(ex, "postgresql.insert_failed"));
        }
    }

    /// <summary>
    /// Bulk-inserts a collection of entities, ignoring duplicate-key conflicts (UNIQUE index or
    /// PRIMARY KEY), including duplicates within the collection itself. Returns the number of rows
    /// actually inserted (duplicates contribute 0). Batches use <c>ON CONFLICT DO NOTHING</c>; any
    /// other error rolls back everything the call wrote, so a failure Result means nothing was written.
    /// </summary>
    public async Task<Result<int>> InsertManyIgnoreAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"{PostgreSqlDialect.Quote(c.ColumnName)}"));
            var batchSize = Math.Min(_maxBatchInsertSize, PostgreSqlDialect.MaxBatchRows(insertCols.Length));
            var sw = Stopwatch.StartNew();

            var inserted = await ExecuteInsertIgnoreAtomicAsync(async (conn, tx) =>
            {
                var total = 0;
                for (var start = 0; start < list.Count; start += batchSize)
                {
                    var count = Math.Min(batchSize, list.Count - start);

                    await using var cmd = conn.CreateCommand(_connectionId);
                    cmd.Transaction = tx;

                    var valueTuples = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entity = list[start + i];
                        var tupleParts = new string[insertCols.Length];
                        for (var j = 0; j < insertCols.Length; j++)
                        {
                            var paramName = $"@p_{i}_{j}";
                            tupleParts[j] = paramName;
                            cmd.Parameters.Add(TypeConverter.CreateParameter(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType), insertCols[j].Attribute, insertCols[j].Property.PropertyType));
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = $"INSERT INTO {EntityMetadata<T>.QualifiedTableNameFor(_connectionId)} ({columnList}) VALUES {string.Join(", ", valueTuples)} ON CONFLICT DO NOTHING;";
                    LogQuery(cmd.CommandText);
                    total += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return total;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[PostgreSQL] Bulk-inserted (ignore duplicates) {inserted} records into `{table}` in {sw.ElapsedMilliseconds}ms");
            if (inserted > 0)
                QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] InsertManyIgnoreAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.bulk_insert_failed"));
        }
    }

    private const string InsertIgnoreSavepoint = "cl_pg_insert_ignore";

    /// <summary>
    /// Runs an insert-ignoring-duplicates body atomically: in its own transaction, or under a
    /// savepoint when the repository is bound to an ambient <see cref="TransactionScope"/>. Any
    /// exception rolls back everything the call wrote; the caller's earlier work is untouched and,
    /// because PostgreSQL aborts a transaction on any error, the caller's transaction stays usable.
    /// </summary>
    private Task<TResult> ExecuteInsertIgnoreAtomicAsync<TResult>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> body,
        CancellationToken ct)
        => ExecuteAsync(async conn =>
        {
            var ownTransaction = _transactionScope is null
                ? await conn.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;
            var transaction = ownTransaction ?? _transactionScope!.Transaction;
            try
            {
                if (ownTransaction is null)
                    await transaction.SaveAsync(InsertIgnoreSavepoint, ct).ConfigureAwait(false);

                var result = await body(conn, transaction).ConfigureAwait(false);

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
                    _logger?.Warning($"[PostgreSQL] insert-ignore rollback failed: {rollbackEx.Message}");
                }
                throw;
            }
            finally
            {
                if (ownTransaction is not null)
                    await ownTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }, ct);

    /// <summary>PostgreSQL SQLSTATE 23505 = unique_violation (UNIQUE constraint, unique index or PRIMARY KEY).</summary>
    private static bool IsDuplicateKeyException(Exception ex)
    {
        if (ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        if (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) return true;
        return false;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<NpgsqlConnection, Task<TResult>> action,
        CancellationToken ct = default)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        // The token has to reach ExecuteWithConnectionAsync, otherwise opening the
        // connection (and any transient-failure retry around it) ignores cancellation.
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the per-query observability events. <paramref name="parameters"/> is
    /// forwarded to the EXPLAIN capture on the slow path; when it is null a parameterized
    /// statement is simply not explained (see <see cref="SlowQueryExplain"/>).
    /// </summary>
    private void LogSlowQuery(string sql, long elapsedMs, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        QueryObservability.RecordExecuted(_connectionId, sql, elapsedMs, rowCount: -1, cacheHit: false);
        if (elapsedMs >= _slowQueryThresholdMs)
            SlowQueryExplain.Record(
                _connectionManager, _connectionId, sql, parameters, elapsedMs,
                insideTransaction: _transactionScope is not null);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[PostgreSQL] SQL: {sql}");
    }
}
