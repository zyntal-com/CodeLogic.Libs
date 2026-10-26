using System.Linq.Expressions;
using CL.MSSQL.Core;
using CodeLogic.Core.Logging;

namespace CL.MSSQL.Services;

/// <summary>
/// Intermediate query produced by <see cref="QueryBuilder{T}.GroupBy{TKey}"/>. Not
/// directly executable — the only exit is <see cref="Select{TResult}"/> which collapses
/// the groups back into a shaped result set. Calls inside the projection lambda
/// (<c>g.Sum</c>, <c>g.Average</c>, <c>g.Count</c>, <c>g.Key</c>, …) translate to SQL
/// aggregates against the current <c>GROUP BY</c>.
/// </summary>
public sealed class GroupedQuery<TKey, TSource> where TSource : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly TransactionScope? _transactionScope;
    private readonly int _slowQueryThresholdMs;
    private readonly string _whereClause;
    private readonly Dictionary<string, object?> _parameters;
    private readonly Expression<Func<TSource, TKey>> _keySelector;
    private readonly TimeSpan? _cacheTtl;
    private readonly List<string> _orderBys;
    private readonly int? _limit;
    private readonly int? _offset;
    private readonly bool _cacheDisallowed;

    internal GroupedQuery(
        ConnectionManager connectionManager,
        ILogger? logger,
        string connectionId,
        TransactionScope? transactionScope,
        int slowQueryThresholdMs,
        string whereClause,
        Dictionary<string, object?> parameters,
        Expression<Func<TSource, TKey>> keySelector,
        TimeSpan? cacheTtl,
        List<string> orderBys,
        int? limit,
        int? offset,
        bool cacheDisallowed = false)
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _connectionId = connectionId;
        _transactionScope = transactionScope;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _whereClause = whereClause;
        _parameters = parameters;
        _keySelector = keySelector;
        _cacheTtl = cacheTtl;
        _orderBys = orderBys;
        _limit = limit;
        _offset = offset;
        _cacheDisallowed = cacheDisallowed;
    }

    /// <summary>
    /// Collapse groups into a shaped result. Inside <paramref name="projection"/>, use
    /// <c>g.Key</c> to reference the grouping key and <c>g.Sum/Average/Min/Max/Count/Any</c>
    /// to aggregate rows. Everything translates to SQL — rows aren't materialized into
    /// <typeparamref name="TSource"/> first.
    /// </summary>
    public ProjectedQuery<TSource, TResult> Select<TResult>(
        Expression<Func<IGrouping<TKey, TSource>, TResult>> projection)
    {
        var tableName = EntityMetadata<TSource>.QualifiedTableName;

        // Build the GROUP BY expression from the key selector. For composite anonymous
        // keys each member becomes its own GROUP BY term.
        // Captured values in the key and projection bind through one sink, so a value
        // used in both renders as the same parameter and GROUP BY still matches.
        var sink = new SqlParameterSink();
        var (groupBySql, _) = BuildGroupBy(sink);

        var projectionBody = projection.Body;
        var groupingParam = projection.Parameters[0];
        var keyExpr = _keySelector.Body;
        var rowParam = _keySelector.Parameters[0];

        // Collect (sql, alias, clrType) tuples from the projection body. Same shapes as
        // ProjectionCompiler.Compile, but we use SqlExpressionTranslator so SqlFn/aggregates work.
        var columns = new List<(string Sql, string Alias, Type ClrType)>();

        switch (projectionBody)
        {
            case NewExpression ne:
                for (var i = 0; i < ne.Arguments.Count; i++)
                {
                    var (sql, clrType) = SqlExpressionTranslator.Translate(
                        ne.Arguments[i], rowParam, groupingParam, keyExpr, sink);
                    var alias = ne.Members is { } m && i < m.Count ? m[i].Name : $"col{i}";
                    columns.Add((sql, alias, clrType));
                }
                break;

            case MemberInitExpression mi:
            {
                var innerNew = (NewExpression)mi.NewExpression;
                for (var i = 0; i < innerNew.Arguments.Count; i++)
                {
                    var (sql, clrType) = SqlExpressionTranslator.Translate(
                        innerNew.Arguments[i], rowParam, groupingParam, keyExpr, sink);
                    var alias = innerNew.Members is { } m && i < m.Count ? m[i].Name : $"col{i}";
                    columns.Add((sql, alias, clrType));
                }
                foreach (var binding in mi.Bindings)
                {
                    if (binding is MemberAssignment ma)
                    {
                        var (sql, clrType) = SqlExpressionTranslator.Translate(
                            ma.Expression, rowParam, groupingParam, keyExpr, sink);
                        columns.Add((sql, ma.Member.Name, clrType));
                    }
                }
                break;
            }

            default:
            {
                var (sql, clrType) = SqlExpressionTranslator.Translate(
                    projectionBody, rowParam, groupingParam, keyExpr, sink);
                columns.Add((sql, "value", clrType));
                break;
            }
        }

        var compiled = ProjectionCompiler.CompileFromColumns<TSource, TResult>(projectionBody, columns);

        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;
        if (_offset.HasValue && orderBySql.Length == 0) orderBySql = " ORDER BY (SELECT 1)";
        var topSql = _limit.HasValue && !_offset.HasValue ? $"TOP ({_limit.Value}) " : string.Empty;
        var pagingSql = _offset.HasValue ? $" OFFSET {_offset.Value} ROWS" + (_limit.HasValue ? $" FETCH NEXT {_limit.Value} ROWS ONLY" : "") : "";

        var sql_out = $"SELECT {topSql}{compiled.SelectList} FROM {tableName}{_whereClause} GROUP BY {groupBySql}{orderBySql}{pagingSql}";

        var parameters = new Dictionary<string, object?>(_parameters);

        sink.CopyTo(parameters);


        return new ProjectedQuery<TSource, TResult>(
            _connectionManager, _logger, _connectionId, _transactionScope,
            _slowQueryThresholdMs, sql_out, parameters, compiled, _cacheTtl,
            cacheDisallowed: _cacheDisallowed);
    }

    /// <summary>
    /// Build the GROUP BY list from the key selector. For composite anonymous-type keys
    /// (<c>x =&gt; new { x.A, x.B }</c>) each member becomes a separate group expression.
    /// </summary>
    private (string GroupBySql, List<(string Alias, string Sql, Type ClrType)> KeyMembers) BuildGroupBy(SqlParameterSink sink)
    {
        var rowParam = _keySelector.Parameters[0];
        var body = _keySelector.Body;

        // Composite key: new { A = x.A, B = SqlFn.Hour(x.T) }
        if (body is NewExpression ne)
        {
            var members = new List<(string Alias, string Sql, Type ClrType)>();
            for (var i = 0; i < ne.Arguments.Count; i++)
            {
                var (colSql, clrType) = SqlExpressionTranslator.Translate(ne.Arguments[i], rowParam, null, null, sink);
                var alias = ne.Members is { } m && i < m.Count ? m[i].Name : $"key{i}";
                members.Add((alias, colSql, clrType));
            }
            return (string.Join(", ", members.Select(k => k.Sql)), members);
        }

        // Scalar key: x => x.Col
        var (sql, type) = SqlExpressionTranslator.Translate(body, rowParam, null, null, sink);
        return (sql, new List<(string, string, Type)> { ("Key", sql, type) });
    }

}
