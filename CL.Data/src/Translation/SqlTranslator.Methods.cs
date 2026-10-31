using System.Collections;
using System.Linq.Expressions;
using CL.Data.Sql;

namespace CL.Data.Translation;

internal sealed partial class ExpressionTranslator
{
    private SqlExpr Method(MethodCallExpression call)
    {
        var method = call.Method;
        var declaring = method.DeclaringType;
        var args = call.Arguments;

        // Collection membership: list.Contains(x.Col), Enumerable.Contains(list, x.Col),
        // and C# 14's MemoryExtensions.Contains(span, x.Col) for arrays.
        if (method.Name == nameof(Enumerable.Contains))
        {
            if (call.Object is { } receiver && receiver.Type != typeof(string) && args.Count == 1)
                return InList(receiver, args[0]);
            if (call.Object is null && args.Count == 2 && (declaring == typeof(Enumerable) || declaring == typeof(MemoryExtensions)
                    || declaring == typeof(Queryable)))
                return InList(args[0], args[1]);
            // C# 14+ binds some element types (nullable, enums) to Contains(span, value, IEqualityComparer? comparer = null).
            if (call.Object is null && args.Count == 3 && (declaring == typeof(MemoryExtensions) || declaring == typeof(Enumerable))
                && args[2] is ConstantExpression { Value: null })
                return InList(args[0], args[1]);
        }

        if (declaring == typeof(SqlFn) || declaring == typeof(SqlFn.Json) || declaring == typeof(SqlFn.FullText))
            return SqlFnMethod(call);
        if (declaring == typeof(string)) return StringMethod(call);
        if (declaring == typeof(Math) || declaring == typeof(MathF)) return MathMethod(call);

        // a.Equals(b) / object.Equals(a, b)
        if (method.Name == nameof(Equals) && method.ReturnType == typeof(bool))
        {
            if (call.Object is not null && args.Count == 1) return Equality(call.Object, args[0], negated: false);
            if (call.Object is null && args.Count == 2) return Equality(args[0], args[1], negated: false);
        }

        throw Unsupported(
            $"Method '{declaring?.Name}.{method.Name}' cannot be translated to SQL. Compute it before the query, or use a SqlFn function.",
            call.ToString());
    }

    private SqlExpr StringMethod(MethodCallExpression call)
    {
        var name = call.Method.Name;
        var args = call.Arguments;
        var target = call.Object;

        switch (name)
        {
            case nameof(string.Contains) when target is not null && args.Count == 1 && args[0].Type == typeof(string):
                return Like(target, args[0], prefix: true, suffix: true);
            case nameof(string.StartsWith) when target is not null && args.Count == 1 && args[0].Type == typeof(string):
                return Like(target, args[0], prefix: false, suffix: true);
            case nameof(string.EndsWith) when target is not null && args.Count == 1 && args[0].Type == typeof(string):
                return Like(target, args[0], prefix: true, suffix: false);
            case nameof(string.IsNullOrEmpty) when args.Count == 1:
            {
                var value = Value(args[0]);
                return new BinaryExpr(SqlBinaryOperator.Or, new IsNullExpr(value),
                    new BinaryExpr(SqlBinaryOperator.Equal, value, new ParameterExpr(SqlParameterValue.Of(""))));
            }
            case nameof(string.IsNullOrWhiteSpace) when args.Count == 1:
            {
                var value = Value(args[0]);
                return new BinaryExpr(SqlBinaryOperator.Or, new IsNullExpr(value),
                    new BinaryExpr(SqlBinaryOperator.Equal, new FunctionExpr(SqlFunction.Trim, [value]), new ParameterExpr(SqlParameterValue.Of(""))));
            }
            case nameof(string.ToLower) or nameof(string.ToLowerInvariant) when target is not null && args.Count == 0:
                return Function(SqlFunction.Lower, target);
            case nameof(string.ToUpper) or nameof(string.ToUpperInvariant) when target is not null && args.Count == 0:
                return Function(SqlFunction.Upper, target);
            case nameof(string.Trim) when target is not null && args.Count == 0:
                return Function(SqlFunction.Trim, target);
            case nameof(string.Replace) when target is not null && args.Count == 2 && args[0].Type == typeof(string):
                return Function(SqlFunction.Replace, target, args[0], args[1]);
            case nameof(string.Substring) when target is not null && args.Count is 1 or 2:
            {
                // SQL SUBSTRING is 1-based.
                var start = new BinaryExpr(SqlBinaryOperator.Add, Value(args[0]), new LiteralExpr(1));
                SqlExpr[] parts = args.Count == 2
                    ? [Value(target), start, Value(args[1])]
                    : [Value(target), start, new FunctionExpr(SqlFunction.Length, [Value(target)])];
                return new FunctionExpr(SqlFunction.Substring, parts);
            }
            case nameof(string.Concat) when target is null && args.All(a => a.Type == typeof(string)):
                return args.Select(a => Value(a)).Aggregate((l, r) => new BinaryExpr(SqlBinaryOperator.Concat, l, r));
            case nameof(string.Equals) when args.Count == 1 && target is not null:
                return Equality(target, args[0], negated: false);
            case nameof(string.Equals) when args.Count == 2 && target is null:
                return Equality(args[0], args[1], negated: false);
        }

        throw Unsupported($"String method '{name}' with these arguments cannot be translated to SQL.", call.ToString());
    }

    /// <summary><c>col LIKE '%value%'</c>: a client value is escaped and bound; a column operand is concatenated with the wildcards.</summary>
    private SqlExpr Like(Expression target, Expression argument, bool prefix, bool suffix)
    {
        var operand = Value(target);
        if (!DependsOnParameters(argument))
        {
            if (ValueEvaluator.Evaluate(argument) is not string text)
                return new LiteralExpr(false);   // x.Contains(null) is never true
            var pattern = (prefix ? "%" : "") + scope.Dialect.EscapeLike(text) + (suffix ? "%" : "");
            return new LikeExpr(operand, new ParameterExpr(SqlParameterValue.Of(pattern)));
        }

        SqlExpr patternExpr = Value(argument);
        if (prefix) patternExpr = new BinaryExpr(SqlBinaryOperator.Concat, new ParameterExpr(SqlParameterValue.Of("%")), patternExpr);
        if (suffix) patternExpr = new BinaryExpr(SqlBinaryOperator.Concat, patternExpr, new ParameterExpr(SqlParameterValue.Of("%")));
        return new LikeExpr(operand, patternExpr);
    }

    private SqlExpr MathMethod(MethodCallExpression call)
    {
        var args = call.Arguments;
        return call.Method.Name switch
        {
            nameof(Math.Abs) when args.Count == 1 => Function(SqlFunction.Abs, args[0]),
            nameof(Math.Floor) when args.Count == 1 => Function(SqlFunction.Floor, args[0]),
            nameof(Math.Ceiling) when args.Count == 1 => Function(SqlFunction.Ceiling, args[0]),
            nameof(Math.Round) when args.Count == 1 => new FunctionExpr(SqlFunction.Round, [Value(args[0]), new LiteralExpr(0)]),
            nameof(Math.Round) when args.Count == 2 && args[1].Type == typeof(int) => Function(SqlFunction.Round, args[0], args[1]),
            nameof(Math.Min) when args.Count == 2 => Function(SqlFunction.Least, args[0], args[1]),
            nameof(Math.Max) when args.Count == 2 => Function(SqlFunction.Greatest, args[0], args[1]),
            _ => throw Unsupported($"Math method '{call.Method.Name}' with these arguments cannot be translated to SQL.", call.ToString()),
        };
    }

    /// <summary><c>x.Col IN (@p0, @p1, ...)</c> from a client-side collection; long lists are split into OR'ed lists.</summary>
    private SqlExpr InList(Expression collection, Expression item)
    {
        collection = UnwrapSpanConversion(collection);
        if (DependsOnParameters(collection))
            throw Unsupported("Contains on a collection that depends on the row cannot be translated; use WhereIn with a subquery.");

        var context = ContextColumn(item);
        var operand = Value(item);
        var values = ValueEvaluator.Evaluate(collection) is IEnumerable source ? source.Cast<object?>().ToList() : [];

        // NULL never matches IN; test it separately so ids.Contains(x.ParentId) with a null entry still finds NULL rows.
        var hasNull = values.RemoveAll(v => v is null) > 0;
        SqlExpr result = values.Count == 0
            ? new InListExpr(operand, [])
            : values.Chunk(Math.Max(1, MaxInListValues))
                .Select(chunk => (SqlExpr)new InListExpr(operand,
                    chunk.Select(v => (SqlExpr)new ParameterExpr(SqlParameterValue.ForColumn(v, context))).ToArray()))
                .Aggregate((l, r) => new BinaryExpr(SqlBinaryOperator.Or, l, r));
        return hasNull ? new BinaryExpr(SqlBinaryOperator.Or, result, new IsNullExpr(operand)) : result;
    }

    /// <summary>
    /// C# 14+ binds <c>array.Contains(x)</c> to <c>MemoryExtensions.Contains(ReadOnlySpan&lt;T&gt;, T)</c>, wrapping the
    /// array in an implicit span conversion that cannot be evaluated (a span cannot be boxed). Read the array instead.
    /// </summary>
    internal static Expression UnwrapSpanConversion(Expression expression)
    {
        while (true)
        {
            switch (expression)
            {
                case MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } call when IsSpan(call.Type):
                    expression = call.Arguments[0];
                    continue;
                case UnaryExpression { NodeType: ExpressionType.Convert } convert when IsSpan(convert.Type):
                    expression = convert.Operand;
                    continue;
                case NewExpression { Arguments.Count: 1 } ctor when IsSpan(ctor.Type):
                    expression = ctor.Arguments[0];
                    continue;
                default:
                    return expression;
            }
        }

        static bool IsSpan(Type type) => type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) || type.GetGenericTypeDefinition() == typeof(Span<>));
    }

    private SqlExpr SqlFnMethod(MethodCallExpression call)
    {
        var a = call.Arguments;
        var json = call.Method.DeclaringType == typeof(SqlFn.Json);
        var fullText = call.Method.DeclaringType == typeof(SqlFn.FullText);
        return call.Method.Name switch
        {
            nameof(SqlFn.Json.Value) when json => Function(SqlFunction.JsonValue, a[0], a[1]),
            nameof(SqlFn.Json.Exists) when json => Function(SqlFunction.JsonExists, a[0], a[1]),
            nameof(SqlFn.Json.Contains) when json => Function(SqlFunction.JsonContains, a[0], a[1]),
            nameof(SqlFn.FullText.Match) when fullText => Function(SqlFunction.FullTextMatch, a[0], a[1]),
            nameof(SqlFn.Year) => Function(SqlFunction.Year, a[0]),
            nameof(SqlFn.Month) => Function(SqlFunction.Month, a[0]),
            nameof(SqlFn.Day) => Function(SqlFunction.Day, a[0]),
            nameof(SqlFn.Hour) => Function(SqlFunction.Hour, a[0]),
            nameof(SqlFn.Minute) => Function(SqlFunction.Minute, a[0]),
            nameof(SqlFn.DayOfWeek) => Function(SqlFunction.DayOfWeek, a[0]),
            nameof(SqlFn.Date) => Function(SqlFunction.Date, a[0]),
            nameof(SqlFn.BucketUtc) => Function(SqlFunction.BucketUtc, a[0], a[1]),
            nameof(SqlFn.UtcNow) => new FunctionExpr(SqlFunction.UtcNow, []),
            nameof(SqlFn.Coalesce) => new FunctionExpr(SqlFunction.Coalesce, Expand(a[0])),
            nameof(SqlFn.IfNull) => Function(SqlFunction.Coalesce, a[0], a[1]),
            nameof(SqlFn.Least) => new FunctionExpr(SqlFunction.Least, Expand(a[0])),
            nameof(SqlFn.Greatest) => new FunctionExpr(SqlFunction.Greatest, Expand(a[0])),
            nameof(SqlFn.Lower) => Function(SqlFunction.Lower, a[0]),
            nameof(SqlFn.Upper) => Function(SqlFunction.Upper, a[0]),
            nameof(SqlFn.Trim) => Function(SqlFunction.Trim, a[0]),
            nameof(SqlFn.Length) => Function(SqlFunction.Length, a[0]),
            nameof(SqlFn.Concat) => Expand(a[0]).Aggregate((l, r) => new BinaryExpr(SqlBinaryOperator.Concat, l, r)),
            nameof(SqlFn.Like) => new LikeExpr(Value(a[0]), Value(a[1])),
            nameof(SqlFn.Round) => Function(SqlFunction.Round, a[0], a[1]),
            nameof(SqlFn.Floor) => Function(SqlFunction.Floor, a[0]),
            nameof(SqlFn.Ceiling) => Function(SqlFunction.Ceiling, a[0]),
            nameof(SqlFn.Abs) => Function(SqlFunction.Abs, a[0]),
            _ => throw Unsupported($"SqlFn.{call.Method.Name} is not supported.", call.ToString()),
        };
    }

    /// <summary>Arguments of a <c>params</c> array: each element when written inline, else the evaluated array's items.</summary>
    private SqlExpr[] Expand(Expression array)
    {
        if (array is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } inline)
            return inline.Expressions.Select(e => Value(e)).ToArray();
        if (DependsOnParameters(array))
            throw Unsupported("A params array built from row values must be written inline.", array.ToString());
        return ValueEvaluator.Evaluate(array) is IEnumerable values
            ? values.Cast<object?>().Select(v => v is null ? (SqlExpr)NullExpr.Instance : new ParameterExpr(SqlParameterValue.ForColumn(v, null))).ToArray()
            : [];
    }

    private static TranslationException Unsupported(string message, string details) => new(message, details);
}
