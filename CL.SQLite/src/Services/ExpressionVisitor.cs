using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.SQLite.Models;

namespace CL.SQLite.Services;

/// <summary>
/// Translates LINQ expression trees into SQLite WHERE clause fragments and parameter dictionaries.
/// </summary>
internal static class SQLiteExpressionVisitor
{
    public static (string Clause, Dictionary<string, object?> Parameters) Parse<T>(
        Expression<Func<T, bool>> predicate)
    {
        var parameters = new Dictionary<string, object?>();
        var clause = Visit(predicate.Body, parameters);
        return (clause, parameters);
    }

    public static string ParseOrderBy<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        return GetMemberName(keySelector.Body);
    }

    /// <summary>
    /// Builds the SELECT column list for a projection. Each projected member is resolved back to
    /// the <em>source entity's</em> mapped column — the members of an anonymous type carry no
    /// <see cref="SQLiteColumnAttribute"/> of their own — and every name is double-quoted, so a
    /// renamed column or a reserved word is emitted correctly.
    /// </summary>
    public static string ParseSelect<T>(Expression<Func<T, object?>> selector)
    {
        if (selector.Body is NewExpression newExpr)
            return string.Join(", ", newExpr.Arguments.Select(GetQuotedColumn));

        return GetQuotedColumn(selector.Body);
    }

    public static string ParseGroupBy<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        return GetMemberName(keySelector.Body);
    }

    // ── Private visit methods ────────────────────────────────────────────────

    private static string Visit(Expression expr, Dictionary<string, object?> parameters)
    {
        return expr switch
        {
            BinaryExpression bin   => VisitBinary(bin, parameters),
            UnaryExpression  uni   => VisitUnary(uni, parameters),
            MemberExpression mem   => VisitMember(mem, parameters),
            ConstantExpression con => VisitConstant(con, parameters),
            MethodCallExpression mc => VisitMethodCall(mc, parameters),
            _ => throw new NotSupportedException($"Expression type '{expr.NodeType}' is not supported.")
        };
    }

    private static string VisitBinary(BinaryExpression expr, Dictionary<string, object?> parameters)
    {
        // Handle null comparisons
        if (expr.Right is ConstantExpression { Value: null } or DefaultExpression)
        {
            var leftSide = GetQuotedColumn(expr.Left);
            return expr.NodeType == ExpressionType.Equal
                ? $"{leftSide} IS NULL"
                : $"{leftSide} IS NOT NULL";
        }

        if (expr.Left is ConstantExpression { Value: null } or DefaultExpression)
        {
            var rightSide = GetQuotedColumn(expr.Right);
            return expr.NodeType == ExpressionType.Equal
                ? $"{rightSide} IS NULL"
                : $"{rightSide} IS NOT NULL";
        }

        var op = expr.NodeType switch
        {
            ExpressionType.Equal              => "=",
            ExpressionType.NotEqual           => "!=",
            ExpressionType.LessThan           => "<",
            ExpressionType.LessThanOrEqual    => "<=",
            ExpressionType.GreaterThan        => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.AndAlso            => "AND",
            ExpressionType.OrElse             => "OR",
            ExpressionType.And                => "AND",
            ExpressionType.Or                 => "OR",
            _ => throw new NotSupportedException($"Binary operator '{expr.NodeType}' is not supported.")
        };

        if (op is "AND" or "OR")
        {
            var left = Visit(expr.Left, parameters);
            var right = Visit(expr.Right, parameters);
            return $"({left} {op} {right})";
        }
        else
        {
            var colName = GetQuotedColumn(expr.Left);
            var value = GetValue(expr.Right);
            var paramName = $"@p{parameters.Count}";
            parameters[paramName] = value;
            return $"{colName} {op} {paramName}";
        }
    }

    private static string VisitUnary(UnaryExpression expr, Dictionary<string, object?> parameters)
    {
        if (expr.NodeType == ExpressionType.Not)
            return $"NOT ({Visit(expr.Operand, parameters)})";
        if (expr.NodeType == ExpressionType.Convert)
            return Visit(expr.Operand, parameters);
        throw new NotSupportedException($"Unary operator '{expr.NodeType}' is not supported.");
    }

    private static string VisitMember(MemberExpression expr, Dictionary<string, object?> parameters)
    {
        // Boolean property used directly as condition
        if (expr.Type == typeof(bool))
        {
            var colName = $"\"{GetColumnNameFromMember(expr.Member)}\"";
            var paramName = $"@p{parameters.Count}";
            parameters[paramName] = true;
            return $"{colName} = {paramName}";
        }

        // Captured variable / closure field
        var value = GetValue(expr);
        var pName = $"@p{parameters.Count}";
        parameters[pName] = value;
        return pName;
    }

    private static string VisitConstant(ConstantExpression expr, Dictionary<string, object?> parameters)
    {
        var paramName = $"@p{parameters.Count}";
        parameters[paramName] = expr.Value;
        return paramName;
    }

    private static string VisitMethodCall(MethodCallExpression expr, Dictionary<string, object?> parameters)
    {
        if (expr.Method.DeclaringType == typeof(string))
        {
            switch (expr.Method.Name)
            {
                case "Contains":
                {
                    var col = GetQuotedColumn(expr.Object!);
                    var val = GetValue(expr.Arguments[0])?.ToString() ?? "";
                    var p = $"@p{parameters.Count}";
                    parameters[p] = $"%{val}%";
                    return $"{col} LIKE {p}";
                }
                case "StartsWith":
                {
                    var col = GetQuotedColumn(expr.Object!);
                    var val = GetValue(expr.Arguments[0])?.ToString() ?? "";
                    var p = $"@p{parameters.Count}";
                    parameters[p] = $"{val}%";
                    return $"{col} LIKE {p}";
                }
                case "EndsWith":
                {
                    var col = GetQuotedColumn(expr.Object!);
                    var val = GetValue(expr.Arguments[0])?.ToString() ?? "";
                    var p = $"@p{parameters.Count}";
                    parameters[p] = $"%{val}";
                    return $"{col} LIKE {p}";
                }
            }
        }

        if (expr.Method.Name == "Contains" && expr.Method.DeclaringType != typeof(string))
        {
            // Two shapes reach here, with the operands in opposite positions:
            //   static  Enumerable.Contains(collection, item) -> Arguments[0], Arguments[1]
            //   instance collection.Contains(item)            -> Object,       Arguments[0]
            // Reading the collection from Arguments[0] in both cases fed the instance form
            // the item expression, i.e. a lambda parameter, which the compiler rejects.
            var isStatic = expr.Arguments.Count > 1;
            var collection = GetValue(UnwrapSpanConversion(isStatic ? expr.Arguments[0] : expr.Object!));
            var col = GetQuotedColumn(isStatic ? expr.Arguments[1] : expr.Arguments[0]);
            if (collection is System.Collections.IEnumerable items)
            {
                var inParams = new List<string>();
                foreach (var item in items)
                {
                    var p = $"@p{parameters.Count}";
                    parameters[p] = item;
                    inParams.Add(p);
                }
                return inParams.Count == 0 ? "1=0" : $"{col} IN ({string.Join(", ", inParams)})";
            }
        }

        throw new NotSupportedException($"Method '{expr.Method.Name}' is not supported.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
    private static string GetMemberName(Expression expr)
    {
        if (expr is UnaryExpression uni && uni.NodeType == ExpressionType.Convert)
            return GetMemberName(uni.Operand);

        if (expr is MemberExpression mem)
            return GetColumnNameFromMember(mem.Member);

        throw new NotSupportedException($"Cannot extract member name from expression: {expr}");
    }

    private static string GetColumnNameFromMember(MemberInfo member)
    {
        var colAttr = member.GetCustomAttribute<SQLiteColumnAttribute>();
        return colAttr?.ColumnName ?? member.Name;
    }

    /// <summary>
    /// Returns a member's column name wrapped in double quotes so reserved words
    /// (e.g. <c>Order</c>, <c>Group</c>, <c>Index</c>) are valid SQL identifiers.
    /// Used at the WHERE and SELECT emission sites; ORDER BY quotes separately.
    /// </summary>
    private static string GetQuotedColumn(Expression expr) => $"\"{GetMemberName(expr)}\"";

    private static object? GetValue(Expression expr)
    {
        switch (expr)
        {
            case ConstantExpression con:
                return con.Value;

            case MemberExpression mem:
            {
                var obj = mem.Expression is not null ? GetValue(mem.Expression) : null;
                return mem.Member switch
                {
                    PropertyInfo pi => pi.GetValue(obj),
                    FieldInfo    fi => fi.GetValue(obj),
                    _ => throw new NotSupportedException($"Member type '{mem.Member.MemberType}' is not supported.")
                };
            }

            case UnaryExpression uni when uni.NodeType == ExpressionType.Convert:
                return GetValue(uni.Operand);

            default:
                // Compile and invoke for complex expressions
                var lambda = Expression.Lambda(expr);
                return lambda.Compile().DynamicInvoke();
        }
    }
}
