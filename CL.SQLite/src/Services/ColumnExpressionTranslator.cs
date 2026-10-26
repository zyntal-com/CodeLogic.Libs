using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using CL.SQLite.Models;

namespace CL.SQLite.Services;

/// <summary>
/// Translates column-level C# expressions into SQLite SQL fragments — the value side of a
/// typed <c>UpdateAsync</c> setter or an expression upsert, as opposed to the WHERE predicates
/// <see cref="SQLiteExpressionVisitor"/> handles. Covers column access, arithmetic
/// (<c>+ - * / %</c>), comparisons, <c>&amp;&amp;</c> / <c>||</c> / <c>!</c>, the ternary
/// operator (<c>CASE WHEN</c>), negation, conversions, and two-argument
/// <c>Math.Min</c> / <c>Math.Max</c> (SQLite's scalar <c>min(a, b)</c> / <c>max(a, b)</c>,
/// which return NULL when either argument is NULL).
/// <para>Captured values are bound as parameters through a <see cref="SQLiteParameterSink"/>;
/// only <c>NULL</c> and finite primitive numbers are written inline, because their
/// invariant-culture text cannot carry SQL.</para>
/// </summary>
internal static class SQLiteColumnExpressionTranslator
{
    /// <summary>Renders a column name as a double-quoted SQLite identifier.</summary>
    public static string Quote(string column) => $"\"{column.Replace("\"", "\"\"")}\"";

    /// <summary>
    /// Translates <paramref name="expr"/>, rendering members of <paramref name="rowParam"/> as
    /// plain quoted columns.
    /// </summary>
    public static (string Sql, Type ClrType) Translate(
        Expression expr,
        ParameterExpression rowParam,
        SQLiteParameterSink parameters)
    {
        var columns = new Dictionary<ParameterExpression, Func<string, string>> { [rowParam] = Quote };
        return new Visitor(columns, parameters).Visit(expr);
    }

    /// <summary>
    /// Translates an expression whose lambda parameters each render columns their own way,
    /// e.g. an upsert's <c>(current, incoming)</c> pair where <c>incoming.Col</c> becomes
    /// <c>excluded."col"</c>. Each renderer receives the bare column name.
    /// </summary>
    public static (string Sql, Type ClrType) Translate(
        Expression expr,
        IReadOnlyDictionary<ParameterExpression, Func<string, string>> columnRenderers,
        SQLiteParameterSink parameters)
        => new Visitor(columnRenderers, parameters).Visit(expr);

    /// <summary>
    /// Evaluates an expression that does not read any lambda parameter (a captured variable,
    /// a constant, a member chain off a closure, or anything else that compiles) to its value.
    /// </summary>
    public static object? Evaluate(Expression expr)
    {
        switch (expr)
        {
            case ConstantExpression con:
                return con.Value;

            case MemberExpression { Expression: null or ConstantExpression or MemberExpression } mem
                when mem.Member is FieldInfo or PropertyInfo:
            {
                var target = mem.Expression is null ? null : Evaluate(mem.Expression);
                return mem.Member switch
                {
                    FieldInfo fi => fi.GetValue(target),
                    PropertyInfo pi => pi.GetValue(target),
                    _ => throw new NotSupportedException()
                };
            }

            default:
                return Expression.Lambda(expr).Compile().DynamicInvoke();
        }
    }

    /// <summary>Whether <paramref name="expr"/> reads any of <paramref name="parameters"/>.</summary>
    public static bool References(Expression expr, params ParameterExpression[] parameters)
    {
        var finder = new ParameterFinder(parameters);
        finder.Visit(expr);
        return finder.Found;
    }

    /// <summary>The mapped column name of a member: its <see cref="SQLiteColumnAttribute.ColumnName"/>, or the member name.</summary>
    public static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<SQLiteColumnAttribute>();
        return string.IsNullOrWhiteSpace(attr?.ColumnName) ? member.Name : attr.ColumnName;
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

    private sealed class Visitor(
        IReadOnlyDictionary<ParameterExpression, Func<string, string>> columns,
        SQLiteParameterSink parameters)
    {
        public (string Sql, Type ClrType) Visit(Expression node)
        {
            return node switch
            {
                MemberExpression m => VisitMember(m),
                BinaryExpression b => VisitBinary(b),
                ConditionalExpression c => VisitConditional(c),
                MethodCallExpression mc => VisitMethodCall(mc),
                UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
                    => (Visit(u.Operand).Sql, u.Type),
                UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } u
                    => ($"-({Visit(u.Operand).Sql})", u.Type),
                UnaryExpression { NodeType: ExpressionType.Not } u when u.Type == typeof(bool)
                    => ($"(NOT {Visit(u.Operand).Sql})", u.Type),
                ConstantExpression ce => (FormatValue(ce.Value), node.Type),
                _ when !References(node, columns.Keys.ToArray()) => (FormatValue(Evaluate(node)), node.Type),
                _ => throw new NotSupportedException($"Unsupported column expression: {node.NodeType} ({node})")
            };
        }

        private (string Sql, Type ClrType) VisitMember(MemberExpression m)
        {
            // x.Col on a row parameter → column reference
            if (m.Expression is ParameterExpression pe && columns.TryGetValue(pe, out var render))
            {
                if (m.Member.GetCustomAttribute<SQLiteColumnAttribute>() is null)
                    throw new NotSupportedException(
                        $"'{m.Member.Name}' is not a mapped column ([SQLiteColumn]) and cannot be read in a column expression.");
                var clrType = (m.Member as PropertyInfo)?.PropertyType
                              ?? (m.Member as FieldInfo)?.FieldType
                              ?? typeof(object);
                return (render(GetColumnName(m.Member)), clrType);
            }

            if (References(m, columns.Keys.ToArray()))
                throw new NotSupportedException($"Unsupported member access in column expression: {m}");

            // Captured closure member — evaluate it and bind the value as a parameter.
            return (FormatValue(Evaluate(m)), m.Type);
        }

        private (string Sql, Type ClrType) VisitBinary(BinaryExpression b)
        {
            // A string concatenation compiles to a string.Concat method, not to Add, so Add
            // here is always arithmetic.
            var op = b.NodeType switch
            {
                ExpressionType.Add or ExpressionType.AddChecked           => "+",
                ExpressionType.Subtract or ExpressionType.SubtractChecked => "-",
                ExpressionType.Multiply or ExpressionType.MultiplyChecked => "*",
                ExpressionType.Divide             => "/",
                ExpressionType.Modulo             => "%",
                ExpressionType.Equal              => "=",
                ExpressionType.NotEqual           => "!=",
                ExpressionType.GreaterThan        => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan           => "<",
                ExpressionType.LessThanOrEqual    => "<=",
                ExpressionType.AndAlso            => "AND",
                ExpressionType.OrElse             => "OR",
                _ => throw new NotSupportedException($"Unsupported binary operator in column expression: {b.NodeType}")
            };

            var left = Visit(b.Left);
            var right = Visit(b.Right);

            // "= NULL" is never true in SQL; compare with IS / IS NOT instead.
            if (b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                && (left.Sql == "NULL" || right.Sql == "NULL"))
            {
                var isOp = b.NodeType == ExpressionType.Equal ? "IS" : "IS NOT";
                return ($"({left.Sql} {isOp} {right.Sql})", b.Type);
            }

            return ($"({left.Sql} {op} {right.Sql})", b.Type);
        }

        // x.Flag ? 100.0 : 0.0  →  (CASE WHEN "flag" THEN 100 ELSE 0 END)
        private (string Sql, Type ClrType) VisitConditional(ConditionalExpression c)
        {
            var cond = Visit(c.Test);
            var thenExpr = Visit(c.IfTrue);
            var elseExpr = Visit(c.IfFalse);
            return ($"(CASE WHEN {cond.Sql} THEN {thenExpr.Sql} ELSE {elseExpr.Sql} END)", c.Type);
        }

        private (string Sql, Type ClrType) VisitMethodCall(MethodCallExpression mc)
        {
            // Math.Min(a, b) / Math.Max(a, b) → SQLite's multi-argument scalar min / max.
            if (mc.Method.DeclaringType == typeof(Math) && mc.Arguments.Count == 2
                && mc.Method.Name is nameof(Math.Min) or nameof(Math.Max))
            {
                var fn = mc.Method.Name == nameof(Math.Min) ? "min" : "max";
                var a = Visit(mc.Arguments[0]).Sql;
                var b = Visit(mc.Arguments[1]).Sql;
                return ($"{fn}({a}, {b})", mc.Type);
            }

            // A call that reads no row (e.g. DateTime.UtcNow.AddDays(1)) is just a value.
            if (!References(mc, columns.Keys.ToArray()))
                return (FormatValue(Evaluate(mc)), mc.Type);

            throw new NotSupportedException(
                $"Unsupported method in column expression: {mc.Method.DeclaringType?.Name}.{mc.Method.Name}");
        }

        // NULL and finite primitive numbers are written inline (invariant culture, so the text
        // is only digits, sign, point and exponent). Everything else — strings, dates, GUIDs,
        // enums, bools, blobs — is bound as a parameter, so no value is ever spliced into the
        // SQL text and dates keep the exact form the repository stores.
        private string FormatValue(object? v)
        {
            switch (v)
            {
                case null:
                    return "NULL";
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Number(((IFormattable)v).ToString(null, CultureInfo.InvariantCulture));
                case decimal m:
                    return Number(m.ToString(CultureInfo.InvariantCulture));
                case double d when double.IsFinite(d):
                    return Number(d.ToString("R", CultureInfo.InvariantCulture));
                case float f when float.IsFinite(f):
                    return Number(f.ToString("R", CultureInfo.InvariantCulture));
                default:
                    return parameters.Add(v);
            }
        }

        // A negative literal is parenthesized so it can never meet a preceding minus sign
        // and form "--", which SQL reads as the start of a comment.
        private static string Number(string text) => text.StartsWith('-') ? $"({text})" : text;
    }
}
