using System.Linq.Expressions;
using System.Reflection;
using CL.MySQL2.Models;

namespace CL.MySQL2.Core;

/// <summary>
/// Translates column-level C# expressions into MySQL SQL fragments. This is the
/// read-side counterpart to <see cref="MySqlExpressionVisitor"/> (which handles WHERE
/// predicates). Covers what <c>Select</c> / grouped-<c>Select</c> / <c>GroupBy</c> keys
/// / <c>UpdateAsync</c> setters need: column access, arithmetic, <c>SqlFn.*</c>,
/// <c>Math.Min/Max</c>, ternary, aggregates.
/// <para>Captured values are bound as parameters through a <see cref="SqlParameterSink"/>;
/// only <c>NULL</c> and primitive numbers are written inline, because their invariant-culture
/// text cannot carry SQL.</para>
/// </summary>
internal static class SqlExpressionTranslator
{
    /// <summary>
    /// Translate an expression to SQL + its CLR result type, inside a context that may
    /// or may not be grouped. When <paramref name="groupingParam"/> is non-null the
    /// expression may reference <c>g.Key</c> and call <c>g.Sum/Avg/Min/Max/Count</c>.
    /// <paramref name="valueStorageType"/> is the storage type of the column the result is
    /// written to (UPDATE … SET); it applies to captured values that become that result.
    /// </summary>
    public static (string Sql, Type ClrType) Translate(
        Expression expr,
        ParameterExpression? rowParam,
        ParameterExpression? groupingParam,
        Expression? groupKeyExpr,
        SqlParameterSink parameters,
        StorageType valueStorageType = StorageType.Default)
    {
        var columns = new Dictionary<ParameterExpression, Func<string, string>>();
        if (rowParam is not null) columns[rowParam] = MySqlDialect.Quote;
        return new Visitor(columns, groupingParam, groupKeyExpr, parameters).Visit(expr, valueStorageType);
    }

    /// <summary>
    /// Translate an expression whose lambda parameters each render columns their own way,
    /// e.g. an upsert's <c>(current, incoming)</c> pair where <c>incoming.Col</c> becomes
    /// <c>VALUES(`col`)</c>. Each renderer receives the bare column name.
    /// </summary>
    public static (string Sql, Type ClrType) Translate(
        Expression expr,
        IReadOnlyDictionary<ParameterExpression, Func<string, string>> columnRenderers,
        SqlParameterSink parameters,
        StorageType valueStorageType = StorageType.Default)
    {
        return new Visitor(columnRenderers, null, null, parameters).Visit(expr, valueStorageType);
    }

    private sealed class Visitor
    {
        private readonly IReadOnlyDictionary<ParameterExpression, Func<string, string>> _columns;
        private readonly ParameterExpression? _group;
        private readonly Expression? _groupKey;
        private readonly SqlParameterSink _parameters;

        public Visitor(
            IReadOnlyDictionary<ParameterExpression, Func<string, string>> columns,
            ParameterExpression? group,
            Expression? groupKey,
            SqlParameterSink parameters)
        {
            _columns = columns;
            _group = group;
            _groupKey = groupKey;
            _parameters = parameters;
        }

        // A visitor for an aggregate's inner lambda: its row parameter renders as a plain column.
        private Visitor ForInnerRow(ParameterExpression row)
        {
            var columns = new Dictionary<ParameterExpression, Func<string, string>>(_columns)
            {
                [row] = MySqlDialect.Quote
            };
            return new Visitor(columns, _group, _groupKey, _parameters);
        }

        public (string Sql, Type ClrType) Visit(Expression node) => Visit(node, StorageType.Default);

        // `storage` is the storage type of the column this value is compared with or written
        // to. It only matters for captured values (binary-stored GUIDs and the like).
        public (string Sql, Type ClrType) Visit(Expression node, StorageType storage)
        {
            return node switch
            {
                MemberExpression m => VisitMember(m, storage),
                BinaryExpression b => VisitBinary(b),
                ConditionalExpression c => VisitConditional(c, storage),
                MethodCallExpression mc => VisitMethodCall(mc, storage),
                UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u
                    => (Visit(u.Operand, storage).Sql, u.Type),
                UnaryExpression { NodeType: ExpressionType.Negate } u
                    => ($"-({Visit(u.Operand).Sql})", u.Type),
                ConstantExpression ce => (FormatValue(ce.Value, storage), node.Type),
                NewExpression ne => VisitNew(ne),
                _ => throw new NotSupportedException($"Unsupported column expression: {node.NodeType} ({node})")
            };
        }

        // new { Dow = ..., Hour = ... } — used when a GroupBy key is composite; the
        // caller collects each member separately, so here we just pick the first arg
        // for single-expression contexts.
        private (string Sql, Type ClrType) VisitNew(NewExpression ne)
        {
            if (ne.Arguments.Count == 1) return Visit(ne.Arguments[0]);
            throw new NotSupportedException(
                "NewExpression with multiple members should be decomposed by the caller (see ProjectionCompiler / GroupedQuery key handling).");
        }

        private (string Sql, Type ClrType) VisitMember(MemberExpression m, StorageType storage)
        {
            // g.Key (reference to the grouping key itself — may be scalar or composite)
            if (_group is not null && m.Expression == _group && m.Member.Name == "Key")
            {
                if (_groupKey is null)
                    throw new NotSupportedException("g.Key used without a known group key expression.");
                return Visit(_groupKey);
            }

            // g.Key.Prop — reach into a composite anonymous key
            if (_group is not null && m.Expression is MemberExpression inner
                && inner.Expression == _group && inner.Member.Name == "Key"
                && _groupKey is NewExpression keyNew)
            {
                var idx = keyNew.Members!.ToList().FindIndex(mem => mem.Name == m.Member.Name);
                if (idx < 0) throw new NotSupportedException($"Group key member '{m.Member.Name}' not found.");
                return Visit(keyNew.Arguments[idx]);
            }

            // x.Col on a row parameter → column reference
            if (m.Expression is ParameterExpression pe && _columns.TryGetValue(pe, out var render))
            {
                var colName = GetColumnName(m.Member);
                var clrType = (m.Member as PropertyInfo)?.PropertyType
                              ?? (m.Member as FieldInfo)?.FieldType
                              ?? typeof(object);
                return (render(colName), clrType);
            }

            // Captured closure member — evaluate it and bind the value as a parameter.
            // Fast path skips Compile() for the common closure-field shape.
            var value = ClosureEvaluator.Evaluate(m);
            return (FormatValue(value, storage), m.Type);
        }

        private (string Sql, Type ClrType) VisitBinary(BinaryExpression b)
        {
            var op = b.NodeType switch
            {
                ExpressionType.Add                => "+",
                ExpressionType.Subtract           => "-",
                ExpressionType.Multiply           => "*",
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
                _ => throw new NotSupportedException($"Unsupported binary operator: {b.NodeType}")
            };

            // A comparison binds a captured value with the storage type of the column on the
            // other side; arithmetic operands are plain values.
            var storage = IsComparison(b.NodeType)
                ? ColumnStorageType(b.Left) ?? ColumnStorageType(b.Right) ?? StorageType.Default
                : StorageType.Default;

            var left = Visit(b.Left, storage);
            var right = Visit(b.Right, storage);
            return ($"({left.Sql} {op} {right.Sql})", b.Type);
        }

        // x.IsOnline ? 100.0 : 0.0  →  CASE WHEN is_online = 1 THEN 100 ELSE 0 END
        private (string Sql, Type ClrType) VisitConditional(ConditionalExpression c, StorageType storage)
        {
            var cond = Visit(c.Test);
            var thenExpr = Visit(c.IfTrue, storage);
            var elseExpr = Visit(c.IfFalse, storage);
            return ($"(CASE WHEN {cond.Sql} THEN {thenExpr.Sql} ELSE {elseExpr.Sql} END)", c.Type);
        }

        private (string Sql, Type ClrType) VisitMethodCall(MethodCallExpression mc, StorageType storage)
        {
            // SqlFn.* marker calls
            if (mc.Method.DeclaringType == typeof(SqlFn))
                return VisitSqlFn(mc);

            // Math.Min(a, b) / Math.Max(a, b) → LEAST / GREATEST. Unlike the .NET methods
            // these return NULL when either argument is NULL.
            if (mc.Method.DeclaringType == typeof(Math) && mc.Arguments.Count == 2
                && mc.Method.Name is nameof(Math.Min) or nameof(Math.Max))
            {
                var fn = mc.Method.Name == nameof(Math.Min) ? "LEAST" : "GREATEST";
                var a = Visit(mc.Arguments[0], storage).Sql;
                var b = Visit(mc.Arguments[1], storage).Sql;
                return ($"{fn}({a}, {b})", mc.Type);
            }

            // IGrouping<TKey, T>.Sum/Average/Min/Max/Count/Any aggregate calls
            if (_group is not null && mc.Arguments.Count > 0 && mc.Arguments[0] == _group)
                return VisitAggregate(mc);

            throw new NotSupportedException(
                $"Unsupported method in column expression: {mc.Method.DeclaringType?.Name}.{mc.Method.Name}");
        }

        private (string Sql, Type ClrType) VisitSqlFn(MethodCallExpression mc)
        {
            string Arg(int i) => Visit(mc.Arguments[i]).Sql;

            switch (mc.Method.Name)
            {
                case nameof(SqlFn.Year):       return ($"YEAR({Arg(0)})", typeof(int));
                case nameof(SqlFn.Month):      return ($"MONTH({Arg(0)})", typeof(int));
                case nameof(SqlFn.Day):        return ($"DAY({Arg(0)})", typeof(int));
                case nameof(SqlFn.Hour):       return ($"HOUR({Arg(0)})", typeof(int));
                case nameof(SqlFn.Minute):     return ($"MINUTE({Arg(0)})", typeof(int));
                // MySQL DAYOFWEEK is 1..7 starting Sunday; .NET DayOfWeek is 0..6 starting Sunday.
                case nameof(SqlFn.DayOfWeek):  return ($"(DAYOFWEEK({Arg(0)}) - 1)", typeof(int));
                case nameof(SqlFn.Date):       return ($"DATE({Arg(0)})", typeof(DateTime));
                case nameof(SqlFn.BucketUtc):  return ($"FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP({Arg(0)}) / {Arg(1)}) * {Arg(1)})", typeof(DateTime));
                case nameof(SqlFn.Coalesce):
                {
                    var args = ((NewArrayExpression)mc.Arguments[0]).Expressions
                        .Select(a => Visit(a).Sql);
                    return ($"COALESCE({string.Join(", ", args)})", mc.Method.ReturnType);
                }
                case nameof(SqlFn.IfNull):     return ($"IFNULL({Arg(0)}, {Arg(1)})", mc.Method.ReturnType);
                case nameof(SqlFn.Lower):      return ($"LOWER({Arg(0)})", typeof(string));
                case nameof(SqlFn.Upper):      return ($"UPPER({Arg(0)})", typeof(string));
                case nameof(SqlFn.Concat):
                {
                    var args = ((NewArrayExpression)mc.Arguments[0]).Expressions
                        .Select(a => Visit(a).Sql);
                    return ($"CONCAT({string.Join(", ", args)})", typeof(string));
                }
                case nameof(SqlFn.Like):       return ($"({Arg(0)} LIKE {Arg(1)})", typeof(bool));
                case nameof(SqlFn.Round):      return ($"ROUND({Arg(0)}, {Arg(1)})", typeof(double));
                case nameof(SqlFn.Floor):      return ($"FLOOR({Arg(0)})", typeof(double));
                case nameof(SqlFn.Ceiling):    return ($"CEILING({Arg(0)})", typeof(double));
                default:
                    throw new NotSupportedException($"Unsupported SqlFn: {mc.Method.Name}");
            }
        }

        /// <summary>
        /// Handle <c>g.Sum(x =&gt; x.Foo)</c>, <c>g.Average(...)</c>, <c>g.Min/Max(...)</c>,
        /// <c>g.Count()</c>, <c>g.Count(x =&gt; pred)</c>, <c>g.Any()</c>, <c>g.Any(pred)</c>.
        /// The inner lambda's row parameter rebinds to the row param of this visitor.
        /// </summary>
        private (string Sql, Type ClrType) VisitAggregate(MethodCallExpression mc)
        {
            var method = mc.Method.Name;

            switch (method)
            {
                case nameof(Enumerable.Count) when mc.Arguments.Count == 1:
                    return ("COUNT(*)", typeof(int));

                case nameof(Enumerable.Count) when mc.Arguments.Count == 2:
                {
                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = ForInnerRow(lambda.Parameters[0]);
                    var predicate = innerVisitor.Visit(lambda.Body);
                    return ($"SUM(CASE WHEN {predicate.Sql} THEN 1 ELSE 0 END)", typeof(int));
                }

                case nameof(Enumerable.Any) when mc.Arguments.Count == 1:
                    return ("(COUNT(*) > 0)", typeof(bool));

                case nameof(Enumerable.Any) when mc.Arguments.Count == 2:
                {
                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = ForInnerRow(lambda.Parameters[0]);
                    var predicate = innerVisitor.Visit(lambda.Body);
                    return ($"(SUM(CASE WHEN {predicate.Sql} THEN 1 ELSE 0 END) > 0)", typeof(bool));
                }

                case nameof(Enumerable.Sum):
                case nameof(Enumerable.Average):
                case nameof(Enumerable.Min):
                case nameof(Enumerable.Max):
                {
                    var sqlFn = method switch
                    {
                        nameof(Enumerable.Sum)     => "SUM",
                        nameof(Enumerable.Average) => "AVG",
                        nameof(Enumerable.Min)     => "MIN",
                        nameof(Enumerable.Max)     => "MAX",
                        _ => throw new NotSupportedException()
                    };

                    var lambda = (LambdaExpression)StripQuotes(mc.Arguments[1]);
                    var innerVisitor = ForInnerRow(lambda.Parameters[0]);
                    var inner = innerVisitor.Visit(lambda.Body);
                    // Type: Sum/Min/Max preserve inner type; AVG → double.
                    var outType = method == nameof(Enumerable.Average) ? typeof(double) : inner.ClrType;
                    return ($"{sqlFn}({inner.Sql})", outType);
                }
            }

            throw new NotSupportedException($"Unsupported aggregate: {method}");
        }

        private static Expression StripQuotes(Expression e)
        {
            while (e is UnaryExpression { NodeType: ExpressionType.Quote } u) e = u.Operand;
            return e;
        }

        private static bool IsComparison(ExpressionType t) =>
            t is ExpressionType.Equal or ExpressionType.NotEqual
              or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
              or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

        // NULL and primitive numbers are written inline (invariant culture, so the text is
        // only digits, sign, point and exponent). Everything else — strings, dates, GUIDs,
        // enums, bools, binary — is bound as a parameter, so no value is ever spliced into
        // the SQL text and dates keep their fractional seconds.
        private string FormatValue(object? v, StorageType storage)
        {
            switch (v)
            {
                case null:
                    return "NULL";
                case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                    return ((IFormattable)v).ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                case double d when double.IsFinite(d):
                    return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case float f when float.IsFinite(f):
                    return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return _parameters.Add(v, storage);
            }
        }

        // Storage type of a row column (through Convert nodes), or null when the expression
        // is not a row column.
        private StorageType? ColumnStorageType(Expression e)
        {
            while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u)
                e = u.Operand;
            if (e is MemberExpression { Expression: ParameterExpression pe } m && _columns.ContainsKey(pe))
                return m.Member.GetCustomAttribute<ColumnAttribute>()?.StorageType ?? StorageType.Default;
            return null;
        }

        private static string GetColumnName(MemberInfo member)
        {
            var attr = member.GetCustomAttribute<ColumnAttribute>();
            return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
        }
    }
}
