using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.MySQL2.Models;

namespace CL.MySQL2.Core;

/// <summary>
/// Translates LINQ lambda expressions into MySQL WHERE clause fragments and parameter dictionaries.
/// Supports binary comparisons, logical operators, method calls (Contains, StartsWith, EndsWith),
/// and null checks.
/// </summary>
internal sealed class MySqlExpressionVisitor : ExpressionVisitor
{
    private readonly StringBuilder _sql = new();
    private readonly Dictionary<string, object?> _parameters = new();
    private int _paramCounter;
    // Widest generated IN (...) list in this translation, compared once against the
    // configured MaxInClauseValues so an oversized list warns exactly once per build.
    private int _largestInClause;
    private readonly string _tableAlias;
    private readonly IReadOnlyDictionary<ParameterExpression, string>? _aliasMap;
    private StorageType _currentComparisonStorageType = StorageType.Default;

    public MySqlExpressionVisitor(string tableAlias = "")
    {
        _tableAlias = tableAlias;
    }

    private MySqlExpressionVisitor(IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        _tableAlias = string.Empty;
        _aliasMap = aliasMap;
    }

    /// <summary>
    /// Translates the body of a predicate lambda into a SQL WHERE fragment.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="predicate">The lambda expression to translate.</param>
    /// <param name="tableAlias">Optional table alias prefix for column references.</param>
    /// <returns>SQL fragment and parameter dictionary.</returns>
    public static (string Sql, Dictionary<string, object?> Parameters) Translate<T>(
        Expression<Func<T, bool>> predicate,
        string tableAlias = "")
    {
        var visitor = new MySqlExpressionVisitor(tableAlias);
        visitor.Visit(predicate.Body);
        visitor.WarnOnOversizedInClause(typeof(T).Name);
        return (visitor._sql.ToString(), visitor._parameters);
    }

    /// <summary>
    /// Translates a multi-source predicate (e.g. a join predicate
    /// <c>(l, r) =&gt; l.X == r.Y &amp;&amp; l.Active</c>) into a SQL fragment whose column
    /// references are qualified by the alias mapped to each lambda parameter.
    /// </summary>
    /// <param name="predicate">The predicate lambda body to translate.</param>
    /// <param name="aliasMap">Maps each lambda parameter to its table alias (e.g. l→t0, r→t1).</param>
    public static (string Sql, Dictionary<string, object?> Parameters) TranslateMulti(
        LambdaExpression predicate,
        IReadOnlyDictionary<ParameterExpression, string> aliasMap)
    {
        var visitor = new MySqlExpressionVisitor(aliasMap);
        visitor.Visit(predicate.Body);
        visitor.WarnOnOversizedInClause(
            predicate.Parameters.Count > 0 ? predicate.Parameters[0].Type.Name : "query");
        return (visitor._sql.ToString(), visitor._parameters);
    }

    /// <summary>
    /// Translates a member selector lambda into a column name string.
    /// </summary>
    public static string TranslateSelector<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        var expr = selector.Body;
        while (expr is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            expr = unary.Operand;
        }

        return expr switch
        {
            MemberExpression member => GetColumnName(member.Member),
            UnaryExpression { Operand: MemberExpression inner } => GetColumnName(inner.Member),
            _ => throw new NotSupportedException($"Unsupported selector: {selector}")
        };
    }

    protected override Expression VisitBinary(BinaryExpression node)
    {
        _sql.Append('(');

        // Resolve StorageType from whichever side is an entity member, so the
        // value side gets the correct binary conversion.
        var memberExpr = GetEntityMember(node.Left) ?? GetEntityMember(node.Right);
        var prevStorageType = _currentComparisonStorageType;
        if (memberExpr is not null)
            _currentComparisonStorageType = ResolveStorageType(memberExpr.Member);

        Visit(node.Left);

        var op = node.NodeType switch
        {
            ExpressionType.Equal              => " = ",
            ExpressionType.NotEqual           => " != ",
            ExpressionType.GreaterThan        => " > ",
            ExpressionType.GreaterThanOrEqual => " >= ",
            ExpressionType.LessThan           => " < ",
            ExpressionType.LessThanOrEqual    => " <= ",
            ExpressionType.AndAlso            => " AND ",
            ExpressionType.OrElse             => " OR ",
            ExpressionType.And                => " AND ",
            ExpressionType.Or                 => " OR ",
            _ => throw new NotSupportedException($"Unsupported binary operator: {node.NodeType}")
        };

        // Handle x.Prop == null → IS NULL
        if (node.NodeType == ExpressionType.Equal && IsNullConstant(node.Right))
        {
            _sql.Append(" IS NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }
        if (node.NodeType == ExpressionType.NotEqual && IsNullConstant(node.Right))
        {
            _sql.Append(" IS NOT NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }
        // Handle null == x.Prop
        if (node.NodeType == ExpressionType.Equal && IsNullConstant(node.Left))
        {
            // Already wrote left (null constant), rewrite
            _sql.Clear();
            _sql.Append('(');
            Visit(node.Right);
            _sql.Append(" IS NULL)");
            _currentComparisonStorageType = prevStorageType;
            return node;
        }

        _sql.Append(op);
        Visit(node.Right);
        _sql.Append(')');

        _currentComparisonStorageType = prevStorageType;
        return node;
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        // Nullable<T>.Value: pass through to the inner value column.
        if (node.Member.Name == "Value"
            && node.Expression is not null
            && Nullable.GetUnderlyingType(node.Expression.Type) is not null)
        {
            Visit(node.Expression);
            return node;
        }

        if (node.Expression is ParameterExpression)
        {
            // It's a property/field access on the entity parameter
            _sql.Append(QualifyColumn(node.Member, node.Expression));
        }
        else
        {
            // It's a captured variable / closure member — evaluate it
            var value = GetValue(node);
            AddParameter(value);
        }

        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        AddParameter(node.Value);
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.NodeType == ExpressionType.Not)
        {
            _sql.Append("NOT ");
            Visit(node.Operand);
            return node;
        }
        if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            return Visit(node.Operand);
        }
        return base.VisitUnary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // string.IsNullOrEmpty(x.Col) → (col IS NULL OR col = '')
        if (node.Method.DeclaringType == typeof(string)
            && node.Method.Name == nameof(string.IsNullOrEmpty)
            && node.Arguments.Count == 1
            && node.Arguments[0] is MemberExpression m
            && m.Expression is ParameterExpression)
        {
            var colRef = QualifyColumn(m.Member, m.Expression);
            _sql.Append($"({colRef} IS NULL OR {colRef} = '')");
            return node;
        }

        switch (node.Method.Name)
        {
            // Only a string receiver means LIKE. Any other single-argument instance
            // Contains is a collection membership test and is handled below.
            case "Contains" when node.Object is MemberExpression containsMember
                                  && containsMember.Type == typeof(string)
                                  && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(containsMember.Member, containsMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}%");
                break;
            }
            case "StartsWith" when node.Object is MemberExpression startMember
                                    && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(startMember.Member, startMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"{val}%");
                break;
            }
            case "EndsWith" when node.Object is MemberExpression endMember
                                  && node.Arguments.Count == 1:
            {
                _sql.Append(QualifyColumn(endMember.Member, endMember.Expression));
                _sql.Append(" LIKE ");
                var val = EscapeLikeValue(GetValue(node.Arguments[0]));
                AddParameter($"%{val}");
                break;
            }
            // A collection receiver: ids.Contains(x.Id) on List<T>, HashSet<T>, IList.
            // Excluded above by the string guard; a string receiver here would be a
            // char membership test, which is not what the caller means.
            case "Contains" when node.Object is not null
                                  && node.Object.Type != typeof(string)
                                  && node.Arguments.Count == 1:
                EmitInClause(node.Object, node.Arguments[0]);
                break;

            // Static Enumerable.Contains(collection, item).
            case "Contains" when node.Arguments.Count == 2:
                EmitInClause(node.Arguments[0], node.Arguments[1]);
                break;
            default:
                // Try to evaluate the method call as a constant
                try
                {
                    var value = GetValue(node);
                    AddParameter(value);
                }
                catch
                {
                    throw new NotSupportedException($"Unsupported method call: {node.Method.Name}");
                }
                break;
        }

        return node;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // C# 14+ ("first-class spans") binds array.Contains(x) to MemoryExtensions.Contains(ReadOnlySpan<T>, T),
    // so the collection arrives wrapped in an implicit array -> span conversion. A span can't be boxed, so
    // evaluating that expression through reflection throws NotSupportedException. Read the array instead.
    private static Expression UnwrapSpanConversion(Expression expr)
    {
        while (true)
        {
            switch (expr)
            {
                case MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } call when IsSpanType(call.Type):
                    expr = call.Arguments[0];
                    continue;
                case UnaryExpression { NodeType: ExpressionType.Convert } convert when IsSpanType(convert.Type):
                    expr = convert.Operand;
                    continue;
                case NewExpression { Arguments.Count: 1 } ctor when IsSpanType(ctor.Type):
                    expr = ctor.Arguments[0];
                    continue;
                default:
                    return expr;
            }
        }
    }

    private static bool IsSpanType(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) || type.GetGenericTypeDefinition() == typeof(Span<>));
    /// <summary>
    /// Logs one warning per translation when a generated <c>IN (...)</c> list exceeds the
    /// configured <c>MaxInClauseValues</c>. Deliberately advisory: throwing or chunking here
    /// would change the result of queries that exceed the limit today.
    /// </summary>
    private void WarnOnOversizedInClause(string entityName)
    {
        var limit = SqlGenerationOptions.MaxInClauseValues;
        if (_largestInClause <= limit) return;
        SqlGenerationOptions.Logger?.Warning(
            $"[MySQL2] Generated IN (...) list for '{entityName}' has {_largestInClause} values, " +
            $"over the configured MaxInClauseValues of {limit}. The query still runs with one " +
            $"parameter per value; consider a join or a temporary table.");
    }

    private void AddParameter(object? value)
    {
        var paramName = $"@p{_paramCounter++}";
        _parameters[paramName] = TypeConverter.ToDbValue(value, _currentComparisonStorageType);
        _sql.Append(paramName);
    }

    private static bool IsNullConstant(Expression expr) =>
        expr is ConstantExpression { Value: null };

    private static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
    }

    /// <summary>
    /// Builds a backtick-quoted column reference, qualified by the alias mapped to
    /// <paramref name="owner"/> when a multi-source alias map is in play, or by the single
    /// <c>_tableAlias</c> otherwise. With no alias the column is left unqualified — exactly
    /// the single-table behaviour that predates joins.
    /// </summary>
    private string QualifyColumn(MemberInfo member, Expression? owner)
    {
        var col = GetColumnName(member);
        var alias = ResolveAlias(owner);
        return string.IsNullOrEmpty(alias) ? $"{MySqlDialect.Quote(col)}" : $"{MySqlDialect.Quote(alias)}.{MySqlDialect.Quote(col)}";
    }

    private string ResolveAlias(Expression? owner)
    {
        if (_aliasMap is not null && owner is ParameterExpression pe
            && _aliasMap.TryGetValue(pe, out var alias))
            return alias;
        return _tableAlias;
    }

    private static MemberExpression? GetEntityMember(Expression expr)
    {
        var e = expr;
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
            e = u.Operand;
        return e is MemberExpression { Expression: ParameterExpression } me ? me : null;
    }

    private static StorageType ResolveStorageType(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return attr?.StorageType ?? StorageType.Default;
    }

    private static object? GetValue(Expression expression) => ClosureEvaluator.Evaluate(expression);

    /// <summary>
    /// Escapes LIKE special characters (%, _, \) in a user-supplied value so it is treated
    /// as a literal, not as a wildcard pattern. Non-string values pass through unchanged.
    /// </summary>
    /// <summary>
    /// Emits <c>column IN (...)</c> for a membership test, from either the static
    /// two-argument <c>Enumerable.Contains(collection, item)</c> or the one-argument
    /// instance form <c>collection.Contains(item)</c>. Both shapes share this body so
    /// they cannot drift apart.
    /// </summary>
    private void EmitInClause(Expression collectionExpr, Expression itemExpr)
    {
        // Static Enumerable.Contains(collection, item) or IList.Contains
        var collection = GetValue(UnwrapSpanConversion(collectionExpr));
        var entityMember = GetEntityMember(itemExpr);
        var prevStorageType = _currentComparisonStorageType;
        if (entityMember is not null)
            _currentComparisonStorageType = ResolveStorageType(entityMember.Member);

        // Materialise first: an empty set must not emit "IN ()", which is a syntax
        // error. An empty set matches nothing, so a false literal is the right SQL
        // and the column reference is skipped entirely.
        var items = collection is System.Collections.IEnumerable source
            ? source.Cast<object?>().ToList()
            : [];
        if (items.Count == 0)
        {
            _sql.Append("1 = 0");
        }
        else
        {
            if (items.Count > _largestInClause) _largestInClause = items.Count;
            Visit(itemExpr);
            _sql.Append(" IN (");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) _sql.Append(", ");
                AddParameter(items[i]);
            }
            _sql.Append(')');
        }
        _currentComparisonStorageType = prevStorageType;
    }

    private static object? EscapeLikeValue(object? value)
    {
        if (value is not string s) return value;
        return MySqlDialect.EscapeLike(s);
    }
}
