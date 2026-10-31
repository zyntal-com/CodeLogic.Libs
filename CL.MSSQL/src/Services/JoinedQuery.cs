using System.Diagnostics;
using System.Linq.Expressions;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// A typed two-table join. Built by <see cref="QueryBuilder{T}.Join{TRight, TKey, TResult}"/>;
/// the left side is aliased <c>t0</c> and the right side <c>t1</c>. The result selector
/// projects matched rows into <typeparamref name="TResult"/> — only the referenced columns
/// are transferred, materialized by a compiled (reflection-free) row mapper.
/// <para>
/// Supports <see cref="Where"/>, <see cref="OrderBy{TKey}"/> / <see cref="OrderByDescending{TKey}"/>,
/// <see cref="Take"/> / <see cref="Skip"/>, and the <c>ToListAsync</c> / <c>FirstOrDefaultAsync</c> /
/// <c>CountAsync</c> terminals.
/// </para>
/// <para>
/// <b>Not cacheable in this version.</b> The result cache stamps entries with a single
/// table's version counter, so a join entry could not be invalidated when the <i>other</i>
/// joined table mutates. Rather than ship a cache that silently serves stale joins,
/// <c>.WithCache</c> / <c>.SmartCache</c> are intentionally absent here. Multi-table
/// invalidation is on the roadmap.
/// </para>
/// </summary>
public sealed class JoinedQuery<TLeft, TRight, TResult>
    where TLeft : class, new()
    where TRight : class, new()
{
    private const string LeftAlias = "t0";
    private const string RightAlias = "t1";

    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly TransactionScope? _transactionScope;
    private readonly int _slowQueryThresholdMs;

    private readonly string _leftTable;
    private readonly string _rightTable;
    private readonly string _joinKeyword;
    private readonly string _onClause;
    private readonly ProjectionCompiler.Compiled<TLeft, TResult> _projection;

    private readonly List<(string Clause, Dictionary<string, object?> Params)> _wheres = [];
    private readonly List<string> _orderBys = [];
    private int? _limit;
    private int? _offset;
    private int _paramCounter;

    internal JoinedQuery(
        ConnectionManager connectionManager,
        ILogger? logger,
        string connectionId,
        TransactionScope? transactionScope,
        int slowQueryThresholdMs,
        string leftTable,
        string rightTable,
        JoinType joinType,
        string onClause,
        ProjectionCompiler.Compiled<TLeft, TResult> projection)
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _connectionId = connectionId;
        _transactionScope = transactionScope;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _leftTable = leftTable;
        _rightTable = rightTable;
        _joinKeyword = joinType switch
        {
            JoinType.Left  => "LEFT JOIN",
            JoinType.Right => "RIGHT JOIN",
            JoinType.Inner => "INNER JOIN",
            _ => throw new NotSupportedException(
                $"Typed join does not support {joinType} (keys imply an equi-join). Use Inner, Left, or Right.")
        };
        _onClause = onClause;
        _projection = projection;
    }

    // ── Fluent chain ──────────────────────────────────────────────────────────

    /// <summary>
    /// Filters matched rows. The predicate may reference either side, e.g.
    /// <c>(o, c) =&gt; o.Total &gt; 100 &amp;&amp; c.Country == "DK"</c>.
    /// </summary>
    public JoinedQuery<TLeft, TRight, TResult> Where(Expression<Func<TLeft, TRight, bool>> predicate)
    {
        AddPredicate(predicate.Body, new Dictionary<ParameterExpression, string>
        {
            [predicate.Parameters[0]] = LeftAlias,
            [predicate.Parameters[1]] = RightAlias,
        });
        return this;
    }

    /// <summary>Orders ascending by a column from either side: <c>(o, c) =&gt; o.CreatedUtc</c>.</summary>
    public JoinedQuery<TLeft, TRight, TResult> OrderBy<TKey>(Expression<Func<TLeft, TRight, TKey>> selector)
        => AddOrder(selector, "ASC");

    /// <summary>Orders descending by a column from either side.</summary>
    public JoinedQuery<TLeft, TRight, TResult> OrderByDescending<TKey>(Expression<Func<TLeft, TRight, TKey>> selector)
        => AddOrder(selector, "DESC");

    public JoinedQuery<TLeft, TRight, TResult> Take(int count) { _limit = count; return this; }
    public JoinedQuery<TLeft, TRight, TResult> Skip(int count) { _offset = count; return this; }
    public JoinedQuery<TLeft, TRight, TResult> Limit(int count) => Take(count);
    public JoinedQuery<TLeft, TRight, TResult> Offset(int count) => Skip(count);

    private JoinedQuery<TLeft, TRight, TResult> AddOrder<TKey>(
        Expression<Func<TLeft, TRight, TKey>> selector, string direction)
    {
        var map = new Dictionary<ParameterExpression, string>
        {
            [selector.Parameters[0]] = LeftAlias,
            [selector.Parameters[1]] = RightAlias,
        };
        _orderBys.Add($"{JoinTranslator.OrderColumn(selector.Body, map)} {direction}");
        return this;
    }

    /// <summary>
    /// Adds a left-side-only predicate carried over from the originating
    /// <c>QueryBuilder&lt;TLeft&gt;.Where(...)</c> calls made before <c>.Join</c>.
    /// </summary>
    internal void AddLeftPredicate(LambdaExpression predicate) =>
        AddPredicate(predicate.Body, new Dictionary<ParameterExpression, string>
        {
            [predicate.Parameters[0]] = LeftAlias,
        });

    private void AddPredicate(Expression body, IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        var lambda = Expression.Lambda(body, aliasMap.Keys);
        var (clause, parms) = SqlServerExpressionVisitor.TranslateMulti(lambda, aliasMap);

        // Re-key parameters into a join-local namespace so multiple predicates
        // (and carried-over left predicates) never collide on @p0/@p1/...
        // Longer names are replaced first so @p1 can't clobber a substring of @p10/@p11.
        var rekeyed = new Dictionary<string, object?>();
        foreach (var kv in parms.OrderByDescending(k => k.Key.Length))
        {
            var newKey = $"@jn_{_paramCounter++}";
            rekeyed[newKey] = kv.Value;
            clause = clause.Replace(kv.Key, newKey);
        }
        _wheres.Add((clause, rekeyed));
    }

    // ── Terminals ───────────────────────────────────────────────────────────

    public async Task<Result<List<TResult>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            var (sql, parms) = BuildSelectSql();
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var items = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var list = new List<TResult>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(_projection.Materializer(reader));
                return list;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            RecordTiming(sql, sw.ElapsedMilliseconds, items.Count);
            return Result<List<TResult>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] JoinedQuery.ToListAsync failed: {ex.Message} — query: {DescribeForLog()}", ex);
            return Result<List<TResult>>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    public async Task<Result<TResult?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        _limit = 1;
        var list = await ToListAsync(ct).ConfigureAwait(false);
        if (list.IsFailure) return Result<TResult?>.Failure(list.Error!);
        return Result<TResult?>.Success(list.Value!.Count == 0 ? default : list.Value[0]);
    }

    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT COUNT(*) FROM {FromAndJoin()}{whereClause}";
            LogQuery(sql);

            var count = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] JoinedQuery.CountAsync failed: {ex.Message} — query: {DescribeForLog()}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mssql.query_failed"));
        }
    }

    // ── SQL assembly ──────────────────────────────────────────────────────────

    private string FromAndJoin() =>
        $"{_leftTable} AS {SqlServerDialect.Quote(LeftAlias)} {_joinKeyword} {_rightTable} AS {SqlServerDialect.Quote(RightAlias)} ON {_onClause}";

    private (string Sql, Dictionary<string, object?> Params) BuildSelectSql()
    {
        var (whereClause, parms) = BuildWhereSql();
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;
        if (_offset.HasValue && orderBySql.Length == 0) orderBySql = " ORDER BY (SELECT 1)";
        var topSql = _limit.HasValue && !_offset.HasValue ? $"TOP ({_limit.Value}) " : string.Empty;
        var pagingSql = _offset.HasValue ? $" OFFSET {_offset.Value} ROWS" + (_limit.HasValue ? $" FETCH NEXT {_limit.Value} ROWS ONLY" : "") : "";

        var sql = $"SELECT {topSql}{_projection.SelectList} FROM {FromAndJoin()}{whereClause}{orderBySql}{pagingSql}";
        return (sql, parms);
    }

    private (string Clause, Dictionary<string, object?> Params) BuildWhereSql()
    {
        if (_wheres.Count == 0) return (string.Empty, []);

        var allParms = new Dictionary<string, object?>();
        var clauses = new List<string>();
        foreach (var (clause, parms) in _wheres)
        {
            clauses.Add(clause);
            foreach (var kv in parms) allParms[kv.Key] = kv.Value;
        }
        return ($" WHERE {string.Join(" AND ", clauses)}", allParms);
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

    private async Task<T> ExecuteAsync<T>(Func<SqlConnection, Task<T>> action, CancellationToken ct)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        // The token has to reach ExecuteWithConnectionAsync, otherwise opening the
        // connection (and any transient-failure retry around it) ignores cancellation.
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId, ct).ConfigureAwait(false);
    }

    private void RecordTiming(string sql, long elapsedMs, int rowCount)
    {
        QueryObservability.RecordExecuted(_connectionId, sql, elapsedMs, rowCount, cacheHit: false);
        if (elapsedMs >= _slowQueryThresholdMs)
            QueryObservability.RecordSlow(_connectionId, sql, elapsedMs);
    }

    private string DescribeForLog()
    {
        try { return BuildSelectSql().Sql; }
        catch { return $"<join {_leftTable}⋈{_rightTable}>"; }
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MSSQL] SQL: {sql}");
    }
}
