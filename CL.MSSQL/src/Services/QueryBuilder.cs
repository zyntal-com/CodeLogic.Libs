using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// Fluent query builder for entity type <typeparamref name="T"/>.
/// Chains WHERE, ORDER BY, JOIN, GROUP BY, TOP/OFFSET-FETCH clauses and executes
/// terminal operations returning <see cref="Result{T}"/>.
/// </summary>
public sealed class QueryBuilder<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private string _connectionId;
    private readonly TransactionScope? _transactionScope;
    private readonly int _slowQueryThresholdMs;

    // Builder state
    private readonly List<(string Clause, Dictionary<string, object?> Params)> _wheres = [];
    // Raw predicate expressions, retained so a later typed .Join can re-translate them
    // with the left-table alias (the pre-translated SQL above is alias-less).
    private readonly List<Expression<Func<T, bool>>> _wherePredicates = [];
    private readonly List<string> _orderBys = [];
    private readonly List<CursorOrder> _cursorOrders = [];
    private readonly List<string> _joins = [];
    private readonly List<string> _groupBys = [];
    private int? _limit;
    private int? _offset;
    private int _paramCounter;
    private TimeSpan? _cacheTtl;
    private string? _smartCachePool;
    // Set when a subquery filter (EXISTS / IN) is present. Such filters reference a
    // second table the result cache can't track for invalidation, and they can't be
    // re-aliased into a typed .Join — both are gated on this flag.
    private bool _hasSubqueryWhere;
    // When false (default), reads on a [SoftDelete] entity append `<col> IS NULL`.
    private bool _includeDeleted;
    private string? _afterCursor;
    // Set by ForUpdate / ForShare: a locking read, only valid inside a transaction scope.
    private (bool Exclusive, LockWait Wait)? _lock;
    private HashSet<string>? _excluded;

    // ── Constructors ──────────────────────────────────────────────────────────

    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 1000)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger,
        TransactionScope transactionScope,
        int slowQueryThresholdMs = 1000)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _transactionScope = transactionScope ?? throw new ArgumentNullException(nameof(transactionScope));
        _connectionId = transactionScope.ConnectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    // ── Fluent chain methods ──────────────────────────────────────────────────

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        var (clause, parms) = SqlServerExpressionVisitor.Translate(predicate, string.Empty, out var inListCount);
        WarnOnLargeInList(inListCount);
        // Re-key parameters to avoid collisions. Longer parameter names are replaced
        // first so @p1 can't clobber a substring of @p10/@p11 (predicates with 11+ params).
        var rekeyed = new Dictionary<string, object?>();
        foreach (var kv in parms.OrderByDescending(k => k.Key.Length))
        {
            var newKey = $"@qb_{_paramCounter++}";
            rekeyed[newKey] = kv.Value;
            clause = clause.Replace(kv.Key, newKey);
        }
        _wheres.Add((clause, rekeyed));
        _wherePredicates.Add(predicate);
        return this;
    }

    /// <summary>
    /// Adds a correlated <c>EXISTS</c> filter. The predicate may reference both the outer
    /// row and a row of <typeparamref name="TInner"/>; any non-correlated condition on the
    /// inner side belongs in the same predicate. Translates to
    /// <c>EXISTS (SELECT 1 FROM [inner] WHERE …)</c>.
    /// <para>Example:
    /// <code>
    /// mssql.Query&lt;Order&gt;()
    ///   .WhereExists&lt;Shipment&gt;((o, s) =&gt; s.OrderId == o.Id &amp;&amp; s.Status == "sent")
    /// </code></para>
    /// <para><b>Not supported:</b> EXISTS against the same table as the outer query
    /// (unqualified inner columns would be ambiguous). A query carrying a subquery filter
    /// is not cacheable and cannot be turned into a typed <c>.Join</c>.</para>
    /// </summary>
    public QueryBuilder<T> WhereExists<TInner>(Expression<Func<T, TInner, bool>> predicate)
        where TInner : class, new()
        => AddExists(predicate, negate: false);

    /// <summary>Correlated <c>NOT EXISTS</c> filter. See <see cref="WhereExists{TInner}"/>.</summary>
    public QueryBuilder<T> WhereNotExists<TInner>(Expression<Func<T, TInner, bool>> predicate)
        where TInner : class, new()
        => AddExists(predicate, negate: true);

    private QueryBuilder<T> AddExists<TInner>(Expression<Func<T, TInner, bool>> predicate, bool negate)
        where TInner : class, new()
    {
        var outerTable = GetTableName();
        var innerTable = EntityMetadata<TInner>.TableName;
        if (string.Equals(outerTable, innerTable, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "WhereExists/WhereNotExists against the same table as the outer query is not " +
                "supported — unqualified inner columns would be ambiguous.");

        var map = new Dictionary<ParameterExpression, string>
        {
            [predicate.Parameters[0]] = outerTable,
            [predicate.Parameters[1]] = innerTable,
        };
        var (cond, parms) = SqlServerExpressionVisitor.TranslateMulti(predicate, map);
        var keyword = negate ? "NOT EXISTS" : "EXISTS";
        AppendSubqueryWhere($"{keyword} (SELECT 1 FROM {EntityMetadata<TInner>.QualifiedTableName} WHERE {cond})", parms);
        return this;
    }

    /// <summary>
    /// Adds an <c>IN (subquery)</c> filter: <c>outerColumn IN (SELECT innerColumn FROM
    /// [inner] [WHERE innerFilter])</c>. The inner filter is uncorrelated (it sees only
    /// <typeparamref name="TInner"/>).
    /// <para>Example:
    /// <code>
    /// mssql.Query&lt;Order&gt;()
    ///   .WhereIn&lt;Customer, long&gt;(o =&gt; o.CustomerId, c =&gt; c.Id, c =&gt; c.IsVip)
    /// </code></para>
    /// <para>A query carrying a subquery filter is not cacheable and cannot be turned into
    /// a typed <c>.Join</c>.</para>
    /// </summary>
    public QueryBuilder<T> WhereIn<TInner, TKey>(
        Expression<Func<T, TKey>> outerColumn,
        Expression<Func<TInner, TKey>> innerColumn,
        Expression<Func<TInner, bool>>? innerFilter = null)
        where TInner : class, new()
        => AddIn(outerColumn, innerColumn, innerFilter, negate: false);

    /// <summary>Adds a <c>NOT IN (subquery)</c> filter. See <see cref="WhereIn{TInner, TKey}"/>.</summary>
    public QueryBuilder<T> WhereNotIn<TInner, TKey>(
        Expression<Func<T, TKey>> outerColumn,
        Expression<Func<TInner, TKey>> innerColumn,
        Expression<Func<TInner, bool>>? innerFilter = null)
        where TInner : class, new()
        => AddIn(outerColumn, innerColumn, innerFilter, negate: true);

    private QueryBuilder<T> AddIn<TInner, TKey>(
        Expression<Func<T, TKey>> outerColumn,
        Expression<Func<TInner, TKey>> innerColumn,
        Expression<Func<TInner, bool>>? innerFilter,
        bool negate) where TInner : class, new()
    {
        var outerCol = SqlServerExpressionVisitor.TranslateSelector(outerColumn);
        var innerCol = SqlServerExpressionVisitor.TranslateSelector(innerColumn);
        var innerTable = EntityMetadata<TInner>.TableName;

        var parms = new Dictionary<string, object?>();
        var whereSql = string.Empty;
        if (innerFilter is not null)
        {
            var (cond, p) = SqlServerExpressionVisitor.Translate(innerFilter);
            whereSql = $" WHERE {cond}";
            parms = p;
        }

        var keyword = negate ? "NOT IN" : "IN";
        AppendSubqueryWhere(
            $"{SqlServerDialect.Quote(outerCol)} {keyword} (SELECT {SqlServerDialect.Quote(innerCol)} FROM {EntityMetadata<TInner>.QualifiedTableName}{whereSql})", parms);
        return this;
    }

    /// <summary>
    /// Re-keys a subquery fragment's parameters into the outer query's namespace
    /// (<c>@sub_N</c>) and appends it as a WHERE term. Longer parameter names are replaced
    /// first so <c>@p1</c> can't clobber a substring of <c>@p10</c>.
    /// </summary>
    private void AppendSubqueryWhere(string clause, Dictionary<string, object?> parms)
    {
        var rekeyed = new Dictionary<string, object?>();
        foreach (var kv in parms.OrderByDescending(k => k.Key.Length))
        {
            var newKey = $"@sub_{_paramCounter++}";
            rekeyed[newKey] = kv.Value;
            clause = clause.Replace(kv.Key, newKey);
        }
        _wheres.Add((clause, rekeyed));
        _hasSubqueryWhere = true;
    }

    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SqlServerExpressionVisitor.TranslateSelector(keySelector);
        _orderBys.Add($"{SqlServerDialect.Quote(col)} ASC");
        _cursorOrders.Add(new CursorOrder(EntityMetadata<T>.RequireColumn(col), Descending: false));
        return this;
    }

    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SqlServerExpressionVisitor.TranslateSelector(keySelector);
        _orderBys.Add($"{SqlServerDialect.Quote(col)} DESC");
        _cursorOrders.Add(new CursorOrder(EntityMetadata<T>.RequireColumn(col), Descending: true));
        return this;
    }

    /// <summary>
    /// Continues a cursor-paged query after <paramref name="cursor"/>. The cursor must have
    /// been returned by <see cref="ToCursorPagedListAsync"/> for the same entity and ordering.
    /// A null cursor selects the first page.
    /// </summary>
    public QueryBuilder<T> After(string? cursor)
    {
        _afterCursor = cursor;
        return this;
    }

    /// <summary>
    /// Leaves <paramref name="columns"/> out of the rows this query reads: the SELECT lists every other mapped
    /// column instead of <c>*</c>, and the excluded properties keep their default value. Meant for wide text/binary
    /// columns a reader doesn't need; unlike <see cref="Select{TResult}"/> a column added to <typeparamref name="T"/>
    /// later is still read. Applies to <c>ToListAsync</c>, <c>FirstOrDefaultAsync</c>, the paged and cursor-paged
    /// reads and <c>UpdateAndGetAsync</c>; <see cref="Select{TResult}"/> picks its own columns. Don't write an entity
    /// read this way back with <c>Repository.UpdateAsync</c> — the excluded columns would be overwritten with defaults.
    /// </summary>
    public QueryBuilder<T> Exclude(params Expression<Func<T, object?>>[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _excluded ??= [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var column in columns)
            _excluded.Add(EntityMetadata<T>.RequireColumn(SqlServerExpressionVisitor.TranslateSelector(column)).ColumnName);
        if (EntityMetadata<T>.Columns.All(c => _excluded.Contains(c.ColumnName)))
            throw new ArgumentException($"Exclude would leave no columns of {typeof(T).Name} to read.", nameof(columns));
        return this;
    }

    /// <summary>The column list: <c>{source}.*</c>, or the table's columns minus <see cref="Exclude"/>d ones.</summary>
    private string SelectColumns(string source)
    {
        if (_excluded is null) return $"{source}.*";
        return string.Join(", ", EntityMetadata<T>.Columns
            .Where(c => !_excluded.Contains(c.ColumnName))
            .Select(c => $"{source}.{SqlServerDialect.Quote(c.ColumnName)}"));
    }

    public QueryBuilder<T> Limit(int count) { _limit = count; return this; }
    public QueryBuilder<T> Offset(int count) { _offset = count; return this; }
    public QueryBuilder<T> Take(int count) => Limit(count);
    public QueryBuilder<T> Skip(int count) => Offset(count);

    public QueryBuilder<T> Join(string table, string condition, JoinType type = JoinType.Inner)
    {
        EnsureCursorNotSet(nameof(Join));
        EnsureNoLock(nameof(Join));
        var keyword = type switch
        {
            JoinType.Left  => "LEFT JOIN",
            JoinType.Right => "RIGHT JOIN",
            JoinType.Cross => "CROSS JOIN",
            _              => "INNER JOIN"
        };
        _joins.Add($"{keyword} {SqlServerDialect.QuoteMultipart(table)} ON {condition}");
        return this;
    }

    /// <summary>
    /// Strongly-typed equi-join to <typeparamref name="TRight"/>, translated to real SQL with
    /// table aliases (left <c>t0</c>, right <c>t1</c>) and a compiled projection into
    /// <typeparamref name="TResult"/> — only the referenced columns are transferred.
    /// <para>
    /// Example:
    /// <code>
    /// await mssql.Query&lt;Order&gt;()
    ///     .Where(o =&gt; o.Total &gt; 100)
    ///     .Join&lt;Customer, long, OrderView&gt;(
    ///         o =&gt; o.CustomerId,             // left key
    ///         c =&gt; c.Id,                     // right key
    ///         (o, c) =&gt; new OrderView { OrderId = o.Id, Customer = c.Name })
    ///     .OrderByDescending((o, c) =&gt; o.Total)
    ///     .ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// Keys may be single members or composite anonymous keys
    /// (<c>o =&gt; new { o.A, o.B }</c> matched with <c>c =&gt; new { c.X, c.Y }</c>).
    /// <c>.Where(...)</c> calls made before <c>.Join</c> are carried over and re-qualified
    /// to the left table. Apply ordering and paging <i>after</i> the join via the returned
    /// <see cref="JoinedQuery{TLeft, TRight, TResult}"/>.
    /// </para>
    /// </summary>
    public JoinedQuery<T, TRight, TResult> Join<TRight, TKey, TResult>(
        Expression<Func<T, TKey>> leftKey,
        Expression<Func<TRight, TKey>> rightKey,
        Expression<Func<T, TRight, TResult>> resultSelector,
        JoinType type = JoinType.Inner)
        where TRight : class, new()
    {
        EnsureCursorNotSet(nameof(Join));
        EnsureNoLock(nameof(Join));
        if (_orderBys.Count > 0 || _limit.HasValue || _offset.HasValue)
            throw new InvalidOperationException(
                "Apply OrderBy / Take / Skip after .Join, on the returned JoinedQuery — " +
                "ordering and paging set before the join would be silently dropped.");
        if (_joins.Count > 0 || _groupBys.Count > 0)
            throw new InvalidOperationException(
                "Typed .Join cannot be combined with raw .Join(string,...) or .GroupBy on the same query.");
        if (_hasSubqueryWhere)
            throw new InvalidOperationException(
                "Typed .Join cannot be combined with a subquery filter (WhereExists / WhereIn) — " +
                "express the relationship through the join instead.");

        const string leftAlias = "t0";
        const string rightAlias = "t1";

        var onClause = JoinTranslator.OnClause(leftKey, leftAlias, rightKey, rightAlias);

        var aliasMap = new Dictionary<ParameterExpression, string>
        {
            [resultSelector.Parameters[0]] = leftAlias,
            [resultSelector.Parameters[1]] = rightAlias,
        };
        var columns = JoinTranslator.ProjectionColumns(resultSelector.Body, aliasMap);
        var projection = ProjectionCompiler.CompileFromColumns<T, TResult>(resultSelector.Body, columns);

        var joined = new JoinedQuery<T, TRight, TResult>(
            _connectionManager, _logger, _connectionId, _transactionScope, _slowQueryThresholdMs,
            EntityMetadata<T>.QualifiedTableName, EntityMetadata<TRight>.QualifiedTableName, type, onClause, projection);

        // Carry forward any left-side filters applied before the join.
        foreach (var predicate in _wherePredicates)
            joined.AddLeftPredicate(predicate);

        return joined;
    }

    /// <summary>
    /// Typed projection: transforms rows of <typeparamref name="T"/> into rows of
    /// <typeparamref name="TResult"/>. Emits a real <c>SELECT col1, col2 AS alias</c>
    /// column list and returns a pipeline whose terminals hydrate
    /// <typeparamref name="TResult"/> directly — skipping <typeparamref name="T"/>
    /// materialization and any columns not referenced by the projection.
    /// </summary>
    public ProjectedQuery<T, TResult> Select<TResult>(Expression<Func<T, TResult>> selector)
    {
        EnsureCursorNotSet(nameof(Select));
        EnsureLockUsable(nameof(Select));
        var compiled = ProjectionCompiler.Compile(selector);

        var tableName = GetTableName();
        var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
        var (whereClause, parms) = BuildWhereSql();
        var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;
        if (_offset.HasValue && orderBySql.Length == 0) orderBySql = " ORDER BY (SELECT 1)";
        var topSql = _limit.HasValue && !_offset.HasValue ? $"TOP ({_limit.Value}) " : string.Empty;
        var pagingSql = _offset.HasValue
            ? $" OFFSET {_offset.Value} ROWS" + (_limit.HasValue ? $" FETCH NEXT {_limit.Value} ROWS ONLY" : "") : string.Empty;

        var sql = $"SELECT {topSql}{compiled.SelectList} FROM {EntityMetadata<T>.QualifiedTableName}{LockHint()}{joinSql}{whereClause}{groupBySql}{orderBySql}{pagingSql}";

        // A subquery-filtered query is not cacheable — the cache stamps an entry with a single
        // table's version, so a mutation on the EXISTS / IN inner table could never invalidate
        // it. ShouldCache / ShouldSmartCache refuse it here; the projection must refuse it too,
        // otherwise .WhereExists(...).Select(...).WithCache(...) would serve a stale cross-table
        // result.
        var projectionCacheTtl = _hasSubqueryWhere ? null : _cacheTtl;
        var projectionCachePool = _hasSubqueryWhere ? null : _smartCachePool;
        if (_hasSubqueryWhere && (_cacheTtl is not null || _smartCachePool is not null))
            _logger?.Warning(
                "[MSSQL] Caching is not applied to a projected query with a subquery filter " +
                "(WhereExists / WhereIn) — the entry could not be invalidated by the inner table.");

        return new ProjectedQuery<T, TResult>(
            _connectionManager, _logger, _connectionId, _transactionScope,
            _slowQueryThresholdMs, sql, parms, compiled, projectionCacheTtl, projectionCachePool,
            cacheDisallowed: _hasSubqueryWhere);
    }

    /// <summary>
    /// Groups rows by the key selector and returns a <see cref="GroupedQuery{TKey, TSource}"/>.
    /// Collapse the groups with <c>.Select(g =&gt; new { … g.Sum(...) … })</c>; nothing is
    /// executed until a terminal on the projected query.
    /// </summary>
    public GroupedQuery<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        EnsureCursorNotSet(nameof(GroupBy));
        EnsureNoLock(nameof(GroupBy));
        var (whereClause, parms) = BuildWhereSql();
        // Same guard as Select: a grouped projection over a subquery filter is not cacheable.
        return new GroupedQuery<TKey, T>(
            _connectionManager, _logger, _connectionId, _transactionScope,
            _slowQueryThresholdMs, whereClause, parms, keySelector,
            _hasSubqueryWhere ? null : _cacheTtl, _orderBys, _limit, _offset,
            cacheDisallowed: _hasSubqueryWhere);
    }

    /// <summary>
    /// Makes the read lock the rows it returns until the transaction ends, so a
    /// read-modify-write needs no retry loop. Renders the table hint
    /// <c>WITH (UPDLOCK, ROWLOCK)</c> after the table; <see cref="LockWait.NoWait"/> adds
    /// <c>NOWAIT</c> and <see cref="LockWait.SkipLocked"/> adds <c>READPAST</c>, which turns
    /// the query into a job-queue claim. Only valid on a query created from a
    /// <see cref="TransactionScope"/>; honoured by <c>ToListAsync</c>,
    /// <c>FirstOrDefaultAsync</c>, <c>AnyAsync</c>, <c>Select</c> and <c>UpdateAndGetAsync</c>.
    /// </summary>
    public QueryBuilder<T> ForUpdate(LockWait wait = LockWait.Wait)
    {
        _lock = (true, wait);
        return this;
    }

    /// <summary>
    /// Makes the read take a shared lock on the rows it returns until the transaction ends:
    /// others can read them but not change them. Same rules as <see cref="ForUpdate"/>.
    /// Renders <c>WITH (HOLDLOCK, ROWLOCK)</c> (serializable: key ranges are locked too, so no
    /// new matching row can appear). SQL Server does not allow <c>READPAST</c> with
    /// <c>HOLDLOCK</c>, so <see cref="LockWait.SkipLocked"/> uses <c>REPEATABLEREAD</c> instead,
    /// which holds the shared locks on the rows read but not on the ranges between them.
    /// </summary>
    public QueryBuilder<T> ForShare(LockWait wait = LockWait.Wait)
    {
        _lock = (false, wait);
        return this;
    }

    public QueryBuilder<T> WithConnection(string connectionId)
    {
        _connectionId = connectionId;
        return this;
    }

    /// <summary>
    /// Includes soft-deleted rows in the results. Only meaningful when the entity carries
    /// <see cref="Models.SoftDeleteAttribute"/>; otherwise a no-op. Reads exclude soft-deleted
    /// rows by default.
    /// </summary>
    public QueryBuilder<T> IncludeDeleted()
    {
        _includeDeleted = true;
        return this;
    }

    /// <summary>
    /// Enable result caching for this query. Cached results are automatically invalidated
    /// when any mutation (INSERT/UPDATE/DELETE) occurs on the same table.
    /// Has no effect inside a transaction scope (reads in transactions see uncommitted writes).
    /// </summary>
    public QueryBuilder<T> WithCache(TimeSpan ttl)
    {
        _cacheTtl = ttl;
        return this;
    }

    /// <summary>
    /// Enable result caching using the configured default TTL
    /// (<c>mssql.cache</c> → <c>DefaultTtlSeconds</c>, 60 s out of the box). Equivalent to
    /// <see cref="WithCache(TimeSpan)"/> with that value.
    /// </summary>
    public QueryBuilder<T> WithCache() => WithCache(QueryCache.DefaultTtl);

    /// <summary>
    /// Opt this query into a named <see cref="SmartCachePool"/>. The pool's
    /// background timer keeps the cache entry warm — readers never block on
    /// the DB once the entry is populated. The pool must be registered via
    /// <c>MSSQLLibrary.RegisterCachePool</c> beforehand; an unknown name
    /// falls back to non-cached execution (logged at warn).
    /// <para>
    /// Mutually exclusive with <see cref="WithCache(TimeSpan)"/> — if both
    /// are set, <c>SmartCache</c> wins and the TTL is derived from the
    /// pool's refresh interval.
    /// </para>
    /// </summary>
    public QueryBuilder<T> SmartCache(string poolName)
    {
        _smartCachePool = poolName;
        return this;
    }

    // Plain cache-aside requires an explicit TTL. A query that asked for
    // SmartCache against an UNREGISTERED pool must fall through to truly
    // uncached execution (the logged fallback) — including _smartCachePool
    // here used to send it into the TTL branch and dereference a null
    // _cacheTtl ("Nullable object must have a value").
    // Subquery-filtered queries are not cacheable: the cache stamps entries with a single
    // table's version, so a mutation on the EXISTS/IN inner table couldn't invalidate them.
    // MaxInClauseValues is advisory: a list above it still executes (chunking or throwing
    // would break callers who exceed it today), but it is logged once per query build.
    private bool _inListWarningIssued;

    private void WarnOnLargeInList(int count)
    {
        if (count <= 0 || _inListWarningIssued) return;
        var max = _connectionManager.GetConfiguration(_connectionId)?.MaxInClauseValues ?? 0;
        if (max <= 0 || count <= max) return;
        _inListWarningIssued = true;
        _logger?.Warning(
            $"[MSSQL] {typeof(T).Name}: a generated IN (...) list holds {count} values, above the " +
            $"configured MaxInClauseValues of {max}. The query still runs — consider a join or a " +
            "temp table if SQL Server rejects it on the 2,100-parameter limit.");
    }

    private bool ShouldCache => _cacheTtl is not null && _transactionScope is null && !_hasSubqueryWhere;
    private bool ShouldSmartCache => _smartCachePool is not null && _transactionScope is null && !_hasSubqueryWhere;

    // ── Terminal methods ──────────────────────────────────────────────────────

    public async Task<Result<List<T>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(ToListAsync));
            EnsureLockUsable(nameof(ToListAsync));
            var (sql, parms) = BuildSelectSql();
            var tableName = GetTableName();

            if (ShouldSmartCache)
            {
                var pool = SmartCachePoolRegistry.Get(_smartCachePool!);
                if (pool is null)
                {
                    _logger?.Warning($"[MSSQL] SmartCache pool '{_smartCachePool}' is not registered — falling back to uncached execution.");
                }
                else
                {
                    var ttl = TimeSpan.FromMilliseconds(pool.RefreshEvery.TotalMilliseconds * 2);
                    pool.RegisterOrTouch(_connectionId, tableName, sql, parms,
                        refreshFactory: async tickCt =>
                        {
                            // Store the full Result — GetOrSetAsync reads it back as Result<List<T>> and casts.
                            var r = await ExecuteToList(sql, parms, tickCt).ConfigureAwait(false);
                            return r.IsSuccess ? (object?)r : null;
                        });

                    var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, sql, parms);
                    pool.Touch(_connectionId, tableName, sql, parms);
                    return await QueryCache.GetOrSetAsync(
                        cacheKey, tableName,
                        () => ExecuteToList(sql, parms, ct), ttl, _connectionId).ConfigureAwait(false);
                }
            }

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, sql, parms);
                // connectionId passed in so cache hits / misses fire observability events.
                return await QueryCache.GetOrSetAsync(cacheKey, tableName, () => ExecuteToList(sql, parms, ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecuteToList(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.ToListAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }


    /// <summary>
    /// Best-effort query description for error logs — the SELECT shape (table +
    /// WHERE) pinpoints the failing call site even for update/delete variants.
    /// Never throws.
    /// </summary>
    private string DescribeQueryForLog()
    {
        try
        {
            var (sql, _) = BuildSelectSql();
            return sql;
        }
        catch
        {
            try { return $"<table {GetTableName()}>"; } catch { return "<unknown>"; }
        }
    }

    private async Task<Result<List<T>>> ExecuteToList(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();

        var list = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var items = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                items.Add(map(reader));
            return items;
        }, ct).ConfigureAwait(false);

        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds);
        return Result<List<T>>.Success(list);
    }

    public async Task<Result<T?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(FirstOrDefaultAsync));
            EnsureLockUsable(nameof(FirstOrDefaultAsync));
            Limit(1);
            var (sql, parms) = BuildSelectSql();
            var tableName = GetTableName();

            if (ShouldSmartCache)
            {
                var pool = SmartCachePoolRegistry.Get(_smartCachePool!);
                if (pool is null)
                {
                    _logger?.Warning($"[MSSQL] SmartCache pool '{_smartCachePool}' is not registered — falling back to uncached execution.");
                }
                else
                {
                    var ttl = TimeSpan.FromMilliseconds(pool.RefreshEvery.TotalMilliseconds * 2);
                    pool.RegisterOrTouch(_connectionId, tableName, sql, parms,
                        refreshFactory: async tickCt =>
                        {
                            var r = await ExecuteFirstOrDefault(sql, parms, tickCt).ConfigureAwait(false);
                            return r.IsSuccess ? (object?)r : null;
                        });

                    var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, sql, parms);
                    pool.Touch(_connectionId, tableName, sql, parms);
                    return await QueryCache.GetOrSetAsync(
                        cacheKey, tableName,
                        () => ExecuteFirstOrDefault(sql, parms, ct), ttl, _connectionId).ConfigureAwait(false);
                }
            }

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, sql, parms);
                // connectionId passed in so cache hits / misses fire observability events.
                return await QueryCache.GetOrSetAsync(cacheKey, tableName, () => ExecuteFirstOrDefault(sql, parms, ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecuteFirstOrDefault(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.FirstOrDefaultAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    private async Task<Result<T?>> ExecuteFirstOrDefault(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();

        var result = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            return map(reader);
        }, ct).ConfigureAwait(false);

        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds);
        return Result<T?>.Success(result);
    }

    public async Task<Result<PagedResult<T>>> ToPagedListAsync(
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(ToPagedListAsync));
            EnsureNoLock(nameof(ToPagedListAsync));
            var tblName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
            var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM {EntityMetadata<T>.QualifiedTableName}{joinSql}{whereClause}{groupBySql}";
            var dataSql = BuildSelectSql(page, pageSize).Sql;

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, GetTableName(), dataSql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, tblName,
                    () => ExecutePagedList(countSql, page, pageSize, parms, ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecutePagedList(countSql, page, pageSize, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.ToPagedListAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    private async Task<Result<PagedResult<T>>> ExecutePagedList(
        string countSql, int page, int pageSize, Dictionary<string, object?> parms, CancellationToken ct)
    {
        var dataSql = BuildSelectSql(page, pageSize).Sql;
        LogQuery(dataSql);

        var sw = Stopwatch.StartNew();
        var (items, total) = await ExecuteAsync(async conn =>
        {
            await using var countCmd = BuildCommand(conn, countSql, parms);
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            var (dSql, dParms) = BuildSelectSql(page, pageSize);
            await using var cmd = BuildCommand(conn, dSql, dParms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var entities = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                entities.Add(map(reader));

            return (entities, totalCount);
        }, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Executes a forward-only keyset page. At least one explicit ordering is required;
    /// the mapped primary key is appended automatically when it is not already present.
    /// </summary>
    public async Task<Result<CursorPagedResult<T>>> ToCursorPagedListAsync(
        int pageSize,
        CancellationToken ct = default)
    {
        try
        {
            EnsureNoLock(nameof(ToCursorPagedListAsync));
            if (pageSize <= 0 || pageSize == int.MaxValue)
                throw new CursorPagingException("Cursor page size must be between 1 and Int32.MaxValue - 1.");
            if (_limit.HasValue || _offset.HasValue)
                throw new CursorPagingException(
                    "Take/Limit and Skip/Offset cannot be combined with ToCursorPagedListAsync; pass the size to the terminal.");
            if (_joins.Count > 0 || _groupBys.Count > 0)
                throw new CursorPagingException(
                    "Cursor paging is supported only for plain entity queries, not raw joins or grouped queries.");

            var orders = CursorPagination.GetEffectiveOrder<T>(_cursorOrders);
            var (whereClause, parms) = BuildWhereSql();

            if (_afterCursor is not null)
            {
                var values = CursorTokenCodec.Decode<T>(_afterCursor, orders);
                var (seekSql, seekParms) = CursorPagination.BuildSeekPredicate(orders, values);
                whereClause = AppendWherePredicate(whereClause, seekSql);
                foreach (var pair in seekParms)
                    parms.Add(pair.Key, pair.Value);
            }

            var orderSql = string.Join(", ", orders.Select(RenderOrder));
            var sql = $"SELECT TOP ({pageSize + 1}) {(_excluded is null ? "*" : SelectColumns(EntityMetadata<T>.QualifiedTableName))} FROM {EntityMetadata<T>.QualifiedTableName}{whereClause} ORDER BY {orderSql}";
            var tableName = GetTableName();

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, sql, parms);
                return await QueryCache.GetOrSetAsync(
                    cacheKey, tableName,
                    () => ExecuteCursorPage(sql, parms, orders, pageSize, ct),
                    _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecuteCursorPage(sql, parms, orders, pageSize, ct).ConfigureAwait(false);
        }
        catch (CursorPagingException ex)
        {
            _logger?.Warning($"[MSSQL] QueryBuilder.ToCursorPagedListAsync rejected the query: {ex.Message}");
            return Result<CursorPagedResult<T>>.Failure(Error.FromException(ex, ex.ErrorCode));
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.ToCursorPagedListAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<CursorPagedResult<T>>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    private async Task<Result<CursorPagedResult<T>>> ExecuteCursorPage(
        string sql,
        Dictionary<string, object?> parms,
        IReadOnlyList<CursorOrder> orders,
        int pageSize,
        CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();

        var items = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var entities = new List<T>(pageSize + 1);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                entities.Add(map(reader));
            return entities;
        }, ct).ConfigureAwait(false);

        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds, items.Count);

        var hasNextPage = items.Count > pageSize;
        if (hasNextPage)
            items.RemoveAt(pageSize);

        var nextCursor = hasNextPage && items.Count > 0
            ? CursorTokenCodec.Encode(items[^1], orders)
            : null;

        return Result<CursorPagedResult<T>>.Success(new CursorPagedResult<T>
        {
            Items = items,
            PageSize = pageSize,
            NextCursor = nextCursor
        });
    }

    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(CountAsync));
            EnsureNoLock(nameof(CountAsync));
            var tblName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
            var sql = $"SELECT COUNT(*) FROM {EntityMetadata<T>.QualifiedTableName}{joinSql}{whereClause}";

            if (ShouldSmartCache)
            {
                var pool = SmartCachePoolRegistry.Get(_smartCachePool!);
                if (pool is null)
                {
                    _logger?.Warning($"[MSSQL] SmartCache pool '{_smartCachePool}' is not registered — falling back to uncached execution.");
                }
                else
                {
                    var ttl = TimeSpan.FromMilliseconds(pool.RefreshEvery.TotalMilliseconds * 2);
                    pool.RegisterOrTouch(_connectionId, tblName, sql, parms,
                        refreshFactory: async tickCt =>
                        {
                            var r = await ExecuteCount(sql, parms, tickCt).ConfigureAwait(false);
                            return r.IsSuccess ? (object?)r : null;
                        });

                    var cacheKey = QueryCache.BuildCacheKey(_connectionId, tblName, sql, parms);
                    pool.Touch(_connectionId, tblName, sql, parms);
                    return await QueryCache.GetOrSetAsync(
                        cacheKey, tblName,
                        () => ExecuteCount(sql, parms, ct), ttl, _connectionId).ConfigureAwait(false);
                }
            }

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, GetTableName(), sql, parms);
                // connectionId passed in so cache hits / misses fire observability events.
                return await QueryCache.GetOrSetAsync(cacheKey, tblName, () => ExecuteCount(sql, parms, ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecuteCount(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.CountAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    private async Task<Result<long>> ExecuteCount(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var count = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);

        return Result<long>.Success(count);
    }

    /// <summary>
    /// Whether any row matches the query — <c>SELECT TOP (1) 1 …</c>, so the server stops at
    /// the first hit instead of counting. Honours <see cref="WithCache(TimeSpan)"/> and
    /// <see cref="ForUpdate"/>.
    /// </summary>
    public async Task<Result<bool>> AnyAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(AnyAsync));
            EnsureLockUsable(nameof(AnyAsync));
            var (sql, parms) = BuildAnySql();
            var tblName = GetTableName();

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, tblName, sql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, tblName, () => ExecuteAny(sql, parms, ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }

            return await ExecuteAny(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.AnyAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    /// <summary>
    /// Whether any row matches the query and <paramref name="predicate"/>. Same as
    /// <c>.Where(predicate).AnyAsync()</c>.
    /// </summary>
    public Task<Result<bool>> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        Where(predicate);
        return AnyAsync(ct);
    }

    internal (string Sql, Dictionary<string, object?> Params) BuildAnySql()
    {
        var (whereClause, parms) = BuildWhereSql();
        var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
        return ($"SELECT TOP (1) 1 FROM {EntityMetadata<T>.QualifiedTableName}{LockHint()}{joinSql}{whereClause}", parms);
    }

    private async Task<Result<bool>> ExecuteAny(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();
        var any = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return raw is not null and not DBNull;
        }, ct).ConfigureAwait(false);
        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds);
        return Result<bool>.Success(any);
    }

    public Task<Result<TResult>> MaxAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MAX", selector, ct);

    public Task<Result<TResult>> MinAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MIN", selector, ct);

    public Task<Result<TResult>> SumAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("SUM", selector, ct);

    public async Task<Result<double>> AverageAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(AverageAsync));
            EnsureNoLock(nameof(AverageAsync));
            var col = SqlServerExpressionVisitor.TranslateSelector(selector);
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT AVG({SqlServerDialect.Quote(col)}) FROM {EntityMetadata<T>.QualifiedTableName}{whereClause}";

            LogQuery(sql);
            var value = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return raw is null || raw is DBNull ? 0.0 : Convert.ToDouble(raw);
            }, ct).ConfigureAwait(false);

            return Result<double>.Success(value);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.AverageAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<double>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    public async Task<Result<int>> DeleteAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(DeleteAsync));
            EnsureNoLock(nameof(DeleteAsync));
            var tableName = GetTableName();
            // Hard delete — bypass the soft-delete read filter so it targets all matching rows.
            var (whereClause, parms) = BuildWhereSql(applySoftDelete: false);
            var sql = $"DELETE FROM {EntityMetadata<T>.QualifiedTableName}{whereClause}";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(GetTableName());
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.DeleteAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.delete_failed"));
        }
    }

    /// <summary>
    /// Bulk-update matching rows with a typed setter expression. Emits a single
    /// <c>UPDATE … SET … WHERE …</c> statement; no rows are fetched client-side.
    /// <para>
    /// Example:
    /// <code>
    /// await mssql.Query&lt;Ticket&gt;()
    ///     .Where(t =&gt; t.Status == "open" &amp;&amp; t.CreatedUtc &lt; cutoff)
    ///     .UpdateAsync(t =&gt; new Ticket { Status = "stale", ReviewedUtc = now });
    /// </code>
    /// </para>
    /// Each <c>new T { Prop = value }</c> binding maps to one <c>SET col = value</c>.
    /// Values may reference captured variables or other columns of the same row
    /// (<c>new T { Counter = Math.Min(cap, t.Counter + step) }</c>). Captured values are
    /// always sent as parameters, including inside column expressions; only primitive
    /// numbers are written into the SQL text.
    /// </summary>
    public async Task<Result<int>> UpdateAsync(
        Expression<Func<T, T>> setExpression,
        CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(UpdateAsync));
            EnsureNoLock(nameof(UpdateAsync));
            if (setExpression.Body is not MemberInitExpression mi)
                throw new ArgumentException(
                    "UpdateAsync setter must be a member-init expression like x => new T { Prop = value }.",
                    nameof(setExpression));

            var rowParam = setExpression.Parameters[0];
            var tableName = GetTableName();
            // Bulk update bypasses the soft-delete read filter so it can target / restore deleted rows.
            var (whereClause, whereParms) = BuildWhereSql(applySoftDelete: false);
            var allParms = new Dictionary<string, object?>(whereParms);

            var sets = BuildSetClauses(mi, rowParam, allParms);
            if (sets.Count == 0)
                return Result<int>.Success(0);

            var sql_full = $"UPDATE {EntityMetadata<T>.QualifiedTableName} SET {string.Join(", ", sets)}{whereClause}";
            LogQuery(sql_full);

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql_full, allParms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(tableName);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.UpdateAsync (typed) failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.update_failed"));
        }
    }

    /// <summary>
    /// Updates the matching rows and returns them as they are after the update, in one
    /// statement: no other writer can change them between the update and the read.
    /// <para>
    /// Example — refill a token bucket and read the new balance in one call:
    /// <code>
    /// var r = await mssql.Query&lt;Bucket&gt;()
    ///     .Where(b =&gt; b.Key == key)
    ///     .UpdateAndGetAsync(b =&gt; new Bucket { Tokens = Math.Min(cap, b.Tokens + refill) });
    /// </code>
    /// </para>
    /// <para>
    /// The setter follows the rules of <see cref="UpdateAsync(Expression{Func{T, T}}, CancellationToken)"/>.
    /// <c>Take</c> (with an optional <c>OrderBy</c>) limits which rows are updated, and a
    /// <see cref="ForUpdate"/> wait mode is respected — <c>.OrderBy(j =&gt; j.Id).Take(1)
    /// .ForUpdate(LockWait.SkipLocked).UpdateAndGetAsync(…)</c> claims one job from a queue.
    /// </para>
    /// <para>
    /// Emits <c>UPDATE … SET … OUTPUT INSERTED.* WHERE …</c>; with <c>Take</c> it updates
    /// through <c>WITH cte AS (SELECT TOP (n) * FROM … WITH (UPDLOCK, ROWLOCK) WHERE … ORDER BY …)
    /// UPDATE cte SET … OUTPUT INSERTED.*</c>. <c>OUTPUT</c> carries no order, so the rows
    /// are sorted by the <c>OrderBy</c> keys after they are read. Runs on the query's
    /// <see cref="TransactionScope"/> when there is one; otherwise the single statement is
    /// atomic by itself. No primary key is needed.
    /// </para>
    /// <para><b>Triggers:</b> SQL Server rejects <c>OUTPUT</c> without <c>INTO</c> on a table
    /// that has an enabled trigger, so this fails on such tables.</para>
    /// </summary>
    public async Task<Result<List<T>>> UpdateAndGetAsync(
        Expression<Func<T, T>> setExpression,
        CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(UpdateAndGetAsync));
            var (sql, parms) = BuildUpdateAndGetSql(setExpression);
            var tableName = GetTableName();

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var items = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var rows = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    rows.Add(map(reader));
                return rows;
            }, ct).ConfigureAwait(false);
            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds, items.Count);

            if (_cursorOrders.Count > 0 && items.Count > 1)
                items.Sort(CompareByOrder);

            QueryCache.Invalidate(tableName);
            return Result<List<T>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.UpdateAndGetAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mssql.update_failed"));
        }
    }

    internal (string Sql, Dictionary<string, object?> Params) BuildUpdateAndGetSql(Expression<Func<T, T>> setExpression)
    {
        if (setExpression.Body is not MemberInitExpression mi)
            throw new ArgumentException(
                "UpdateAndGetAsync setter must be a member-init expression like x => new T { Prop = value }.",
                nameof(setExpression));
        if (_joins.Count > 0 || _groupBys.Count > 0 || _offset.HasValue)
            throw new NotSupportedException("UpdateAndGetAsync supports Where, OrderBy, Take and ForUpdate only.");

        var table = EntityMetadata<T>.QualifiedTableName;
        // Same row targeting as UpdateAsync: soft-deleted rows are not filtered out. The WHERE
        // terms are unqualified column names, valid against the table and against the CTE.
        var (whereClause, parms) = BuildWhereSql(applySoftDelete: false);
        var sets = BuildSetClauses(mi, setExpression.Parameters[0], parms);
        if (sets.Count == 0)
            throw new ArgumentException("UpdateAndGetAsync setter assigns no columns.", nameof(setExpression));
        var setSql = string.Join(", ", sets);

        // A shared lock would deadlock two callers upgrading to the update; always lock
        // exclusively, keeping only the caller's wait mode.
        var wait = _lock?.Wait ?? LockWait.Wait;
        if (_limit.HasValue)
        {
            var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;
            return ($"WITH [__target] AS (SELECT TOP ({_limit.Value}) * FROM {table}{LockHintFor(true, wait)}{whereClause}{orderBySql}) " +
                    $"UPDATE [__target] SET {setSql} OUTPUT {SelectColumns("INSERTED")};", parms);
        }

        // Without Take every matching row is updated; the UPDATE takes its own exclusive locks,
        // so a hint is only needed to carry a NOWAIT / READPAST wait mode.
        var hint = wait == LockWait.Wait ? string.Empty : LockHintFor(true, wait);
        return ($"UPDATE {table}{hint} SET {setSql} OUTPUT {SelectColumns("INSERTED")}{whereClause};", parms);
    }

    // Orders materialized rows by the query's OrderBy keys (NULLs first, as SQL Server does).
    private int CompareByOrder(T a, T b)
    {
        foreach (var order in _cursorOrders)
        {
            var cmp = Comparer<object>.Default.Compare(order.Column.Get(a)!, order.Column.Get(b)!);
            if (cmp != 0) return order.Descending ? -cmp : cmp;
        }
        return 0;
    }

    /// <summary>
    /// Turns the bindings of a <c>new T { … }</c> setter into <c>col = value</c> clauses and
    /// adds every value they need to <paramref name="parms"/>. A value that depends only on
    /// captured state binds as one parameter; one that reads the row translates as a column
    /// expression (<c>Counter = t.Counter + 1</c> → <c>counter = (counter + 1)</c>) whose
    /// captured values are parameters too.
    /// </summary>
    private static List<string> BuildSetClauses(
        MemberInitExpression mi, ParameterExpression rowParam, Dictionary<string, object?> parms)
    {
        var sets = new List<string>();
        var sink = new SqlParameterSink("@upd_");
        foreach (var binding in mi.Bindings)
        {
            if (binding is not MemberAssignment ma) continue;

            var colMeta = EntityMetadata<T>.TryResolve(ma.Member.Name)
                          ?? throw new ArgumentException(
                              $"Property '{ma.Member.Name}' is not mapped on '{typeof(T).Name}'.");
            if (colMeta.IsAutoIncrement || colMeta.IsRowVersion)
                throw new ArgumentException(
                    $"Property '{ma.Member.Name}' is database-generated and cannot be updated.");
            var colName = SqlServerDialect.Quote(colMeta.ColumnName);

            if (ReferencesRowParameter(ma.Expression, rowParam))
            {
                var (sql, _) = SqlExpressionTranslator.Translate(
                    ma.Expression, rowParam, null, null, sink, colMeta.EffectiveStorageType);
                sets.Add($"{colName} = {sql}");
            }
            else
            {
                var value = ClosureEvaluator.Evaluate(ma.Expression);
                var valueSql = value is null ? "NULL" : sink.Add(value, colMeta.EffectiveStorageType);
                sets.Add($"{colName} = {valueSql}");
            }
        }
        sink.CopyTo(parms);
        return sets;
    }

    private static bool ReferencesRowParameter(Expression expr, ParameterExpression row)
    {
        var found = false;
        var walker = new RowParamWalker(row, () => found = true);
        walker.Visit(expr);
        return found;
    }

    private sealed class RowParamWalker : ExpressionVisitor
    {
        private readonly ParameterExpression _row;
        private readonly Action _onHit;
        public RowParamWalker(ParameterExpression row, Action onHit) { _row = row; _onHit = onHit; }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == _row) _onHit();
            return base.VisitParameter(node);
        }
    }

    public async Task<Result<int>> UpdateAsync(Dictionary<string, object?> updates, CancellationToken ct = default)
    {
        try
        {
            EnsureCursorNotSet(nameof(UpdateAsync));
            EnsureNoLock(nameof(UpdateAsync));
            foreach (var key in updates.Keys)
            {
                EnsureValidColumn(key);
                var column = EntityMetadata<T>.RequireColumn(key);
                if (column.IsAutoIncrement || column.IsRowVersion)
                    throw new ArgumentException($"Column '{key}' is database-generated and cannot be updated.", nameof(updates));
            }
            var tableName = GetTableName();
            // Bulk update bypasses the soft-delete read filter so it can target / restore deleted rows.
            var (whereClause, whereParms) = BuildWhereSql(applySoftDelete: false);
            var mappedUpdates = updates.Select((pair, index) =>
                (Pair: pair, Column: EntityMetadata<T>.RequireColumn(pair.Key), Parameter: $"@upd_{index}")).ToArray();
            var setClauses = string.Join(", ", mappedUpdates.Select(item =>
                $"{SqlServerDialect.Quote(item.Column.ColumnName)} = {item.Parameter}"));
            var sql = $"UPDATE {EntityMetadata<T>.QualifiedTableName} SET {setClauses}{whereClause}";

            var allParms = new Dictionary<string, object?>(whereParms);
            foreach (var item in mappedUpdates)
            {
                allParms[item.Parameter] = TypeConverter.ToDbValue(item.Pair.Value, item.Column.EffectiveStorageType);
            }

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, allParms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            QueryCache.Invalidate(GetTableName());
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder.UpdateAsync failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.update_failed"));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private (string Sql, Dictionary<string, object?> Params) BuildSelectSql(
        int? page = null,
        int? pageSize = null)
    {
        var tableName = GetTableName();
        var selectCols = _excluded is null ? "*" : SelectColumns(EntityMetadata<T>.QualifiedTableName);
        var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
        var (whereClause, parms) = BuildWhereSql();
        var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;

        int? effectiveLimit = page.HasValue ? pageSize : _limit;
        int? effectiveOffset = page.HasValue ? (page.Value - 1) * (pageSize ?? 0) : _offset;

        if (effectiveOffset.HasValue && orderBySql.Length == 0)
            orderBySql = EntityMetadata<T>.PrimaryKey is { } pk ? $" ORDER BY {SqlServerDialect.Quote(pk.ColumnName)}" : " ORDER BY (SELECT 1)";
        var topSql = effectiveLimit.HasValue && !effectiveOffset.HasValue ? $"TOP ({effectiveLimit.Value}) " : string.Empty;
        var pagingSql = effectiveOffset.HasValue
            ? $" OFFSET {effectiveOffset.Value} ROWS" + (effectiveLimit.HasValue ? $" FETCH NEXT {effectiveLimit.Value} ROWS ONLY" : "") : string.Empty;

        var sql = $"SELECT {topSql}{selectCols} FROM {EntityMetadata<T>.QualifiedTableName}{LockHint()}{joinSql}{whereClause}{groupBySql}{orderBySql}{pagingSql}";
        return (sql, parms);
    }

    private (string Clause, Dictionary<string, object?> Params) BuildWhereSql(bool applySoftDelete = true)
    {
        var allParms = new Dictionary<string, object?>();
        var clauses = new List<string>();

        foreach (var (clause, parms) in _wheres)
        {
            clauses.Add(clause);
            foreach (var kv in parms)
                allParms[kv.Key] = kv.Value;
        }

        // Reads on a [SoftDelete] entity hide deleted rows unless .IncludeDeleted() was called.
        // Writers (UpdateAsync/DeleteAsync) pass applySoftDelete:false so they can still target
        // or restore soft-deleted rows.
        if (applySoftDelete && !_includeDeleted && EntityMetadata<T>.SoftDeleteColumn is { } sd)
            clauses.Add($"{SqlServerDialect.Quote(sd.ColumnName)} IS NULL");

        if (clauses.Count == 0) return (string.Empty, allParms);
        return ($" WHERE {string.Join(" AND ", clauses)}", allParms);
    }

    private static string AppendWherePredicate(string whereClause, string predicate) =>
        string.IsNullOrEmpty(whereClause)
            ? $" WHERE {predicate}"
            : $"{whereClause} AND {predicate}";

    private static string RenderOrder(CursorOrder order) =>
        $"{SqlServerDialect.Quote(order.Column.ColumnName)} {(order.Descending ? "DESC" : "ASC")}";

    private void EnsureCursorNotSet(string operation)
    {
        if (_afterCursor is not null)
            throw new InvalidOperationException(
                $"After(cursor) can only be consumed by ToCursorPagedListAsync; '{operation}' is not a cursor terminal.");
    }

    // ── Row locking ───────────────────────────────────────────────────────────

    private string LockHint() => _lock is { } l ? LockHintFor(l.Exclusive, l.Wait) : string.Empty;

    // SQL Server locks through table hints placed right after the table reference.
    // READPAST is rejected together with HOLDLOCK (serializable), so a skip-locked shared
    // read holds its row locks with REPEATABLEREAD instead.
    private static string LockHintFor(bool exclusive, LockWait wait) => (exclusive, wait) switch
    {
        (true, LockWait.Wait)        => " WITH (UPDLOCK, ROWLOCK)",
        (true, LockWait.NoWait)      => " WITH (UPDLOCK, ROWLOCK, NOWAIT)",
        (true, LockWait.SkipLocked)  => " WITH (UPDLOCK, ROWLOCK, READPAST)",
        (false, LockWait.Wait)       => " WITH (HOLDLOCK, ROWLOCK)",
        (false, LockWait.NoWait)     => " WITH (HOLDLOCK, ROWLOCK, NOWAIT)",
        _                            => " WITH (REPEATABLEREAD, ROWLOCK, READPAST)",
    };

    // Outside a transaction the lock would be released as soon as the statement finished,
    // so a locking read there is almost certainly a bug — fail loudly instead.
    private void EnsureLockUsable(string operation)
    {
        if (_lock is null) return;
        if (_transactionScope is null)
            throw new InvalidOperationException(
                $"ForUpdate/ForShare needs a transaction: create the query with Query<T>(transactionScope) before calling {operation}.");
        if (_groupBys.Count > 0)
            throw new InvalidOperationException("ForUpdate/ForShare cannot be combined with GROUP BY.");
    }

    private void EnsureNoLock(string operation)
    {
        if (_lock is not null)
            throw new InvalidOperationException(
                $"ForUpdate/ForShare is not supported by {operation}; use ToListAsync, FirstOrDefaultAsync, AnyAsync, Select or UpdateAndGetAsync.");
    }

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<SqlConnection, Task<TResult>> action,
        CancellationToken ct = default)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        // The token has to reach ExecuteWithConnectionAsync, otherwise opening the
        // connection (and any transient-failure retry around it) ignores cancellation.
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId, ct).ConfigureAwait(false);
    }

    private SqlCommand BuildCommand(SqlConnection conn, string sql, Dictionary<string, object?> parms)
    {
        var cmd = conn.CreateCommand();
        // Configured QueryTimeoutMs wins over the connection string's Command Timeout for
        // commands this library issues.
        if (_connectionManager.QueryCommandTimeoutSeconds(_connectionId) is { } timeoutSeconds)
            cmd.CommandTimeout = timeoutSeconds;
        if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
        cmd.CommandText = sql;
        foreach (var kv in parms)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        return cmd;
    }

    private async Task<Result<TResult>> ExecuteAggregateAsync<TResult>(
        string func,
        Expression<Func<T, TResult>> selector,
        CancellationToken ct)
    {
        try
        {
            EnsureCursorNotSet($"{func} aggregate");
            EnsureNoLock($"{func} aggregate");
            var col = SqlServerExpressionVisitor.TranslateSelector(selector);
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT {func}({SqlServerDialect.Quote(col)}) FROM {EntityMetadata<T>.QualifiedTableName}{whereClause}";

            LogQuery(sql);
            var value = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null || raw is DBNull) return default!;
                // Convert.ChangeType does not understand Nullable<T>: convert to the
                // underlying type so nullable selectors such as (DateTime?)r.X work.
                var underlyingType = Nullable.GetUnderlyingType(typeof(TResult)) ?? typeof(TResult);
                if (underlyingType.IsEnum)
                    return (TResult)Enum.ToObject(underlyingType,
                        Convert.ChangeType(raw, Enum.GetUnderlyingType(underlyingType)));
                return (TResult)Convert.ChangeType(raw, underlyingType)!;
            }, ct).ConfigureAwait(false);

            return Result<TResult>.Success(value);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] QueryBuilder aggregate failed: {ex.Message} — query: {DescribeQueryForLog()}", ex);
            return Result<TResult>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    private static string GetTableName() => EntityMetadata<T>.TableName;

    private static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
    }

    private static void EnsureValidColumn(string column) =>
        _ = EntityMetadata<T>.RequireColumn(column);

    private void LogSlowQuery(string sql, long elapsedMs, int rowCount = -1)
    {
        QueryObservability.RecordExecuted(_connectionId, sql, elapsedMs, rowCount, cacheHit: false);
        if (elapsedMs >= _slowQueryThresholdMs)
            QueryObservability.RecordSlow(_connectionId, sql, elapsedMs);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MSSQL] SQL: {sql}");
    }
}
