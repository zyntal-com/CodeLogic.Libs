using System.Linq.Expressions;
using CL.Data.Errors;
using CL.Data.Mapping;
using CL.Data.Sql;
using CodeLogic.Core.Results;

namespace CL.Data.Translation;

/// <summary>
/// Translates LINQ lambdas to the SQL tree. Parts that do not depend on the lambda's parameters are evaluated
/// once and bound as parameters; parts that do must translate, or the whole translation fails with
/// <c>data.translation_failed</c>. Nothing that touches a column is ever evaluated on the client.
/// </summary>
public static class SqlTranslator
{
    private const int PredicateMode = 1;
    private const int ValueMode = 2;

    /// <summary>Translates a condition (WHERE, HAVING, ON).</summary>
    public static Result<SqlExpr> TranslatePredicate(LambdaExpression lambda, TranslationScope scope)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        return Run(lambda.Body, scope, PredicateMode);
    }

    /// <summary>Translates a value (projection, ORDER BY, SET).</summary>
    public static Result<SqlExpr> TranslateValue(LambdaExpression lambda, TranslationScope scope)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        return Run(lambda.Body, scope, ValueMode);
    }

    /// <summary>Translates a value expression whose parameters are bound in <paramref name="scope"/> (a part of a larger lambda).</summary>
    public static Result<SqlExpr> TranslateValue(Expression body, TranslationScope scope) =>
        Run(body, scope, ValueMode);

    private static Result<SqlExpr> Run(Expression body, TranslationScope scope, int mode)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(scope);

        // Plan cache: the same shape with new captured values is bound, not translated.
        UInt128? key = null;
        ShapeHasher? hasher = null;
        if (QueryPlanCache.Enabled)
        {
            hasher = ShapeHasher.ForThread();
            key = hasher.Compute(body, scope, mode);
            if (key is { } k && QueryPlanCache.TryGet(k, hasher.Slots, out var cached)) return cached;
            if (key is null) QueryPlanCache.CountMiss();
        }

        var recorder = key is null ? null : new PlanRecorder();
        IReadOnlyList<ConstantExpression> slots = key is null ? [] : [.. hasher!.Slots];   // the hasher is reused by nested translations
        try
        {
            var translator = new ExpressionTranslator(scope) { Recorder = recorder };
            var result = mode == PredicateMode ? translator.Predicate(body) : translator.Value(body);
            if (key is { } k && recorder!.Build(result, slots) is { } variant) QueryPlanCache.Add(k, variant);
            return result;
        }
        catch (TranslationException ex)
        {
            return DataErrors.TranslationFailed(ex.Message, ex.Details ?? body.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Evaluating a captured value threw (for example a null reference in the caller's code).
            return DataErrors.TranslationFailed($"Evaluating a value in the expression failed: {ex.GetType().Name}.", $"{body} — {ex.Message}");
        }
    }
}

/// <summary>The recursive translator. One instance per translation.</summary>
internal sealed partial class ExpressionTranslator(TranslationScope scope)
{
    /// <summary>Most values in one IN list before it is split into OR'ed lists.</summary>
    public int MaxInListValues { get; init; } = 1000;

    /// <summary>Records captured-value reads for the plan cache (null when the shape is not cached).</summary>
    public PlanRecorder? Recorder { get; init; }

    // ── Captured values (every client-side evaluation goes through these, so cached plans can replay it) ──

    // Guard facts: compact numbers, so checking a cached plan allocates nothing.
    private static readonly Func<object?, long> IsNullFact = static v => v is null ? 1 : 0;
    private static readonly Func<object?, long> IsStringFact = static v => v is string ? 1 : 0;
    private static readonly Func<object?, long> InShapeFact = static v => v is InValues i ? i.Values.Count * 2L + (i.HasNull ? 1 : 0) : -1;
    private static readonly Func<object?, long> NullPatternFact = static v =>
    {
        // Which items of a params array are null: a bit mask plus the count (up to 56 items; longer lists are not cached).
        if (v is not List<object?> list) return -1;
        if (list.Count > 56) return PlanRecorder.Uncacheable;
        long mask = list.Count;
        for (var i = 0; i < list.Count; i++)
            if (list[i] is null) mask |= 1L << (i + 7);
        return mask;
    };

    private (object? Value, int Root) Evaluate(Expression expression, RootKind kind = RootKind.Raw) =>
        Recorder is { } recorder ? recorder.Evaluate(expression, kind) : (PlanRecorder.Shape(ValueEvaluator.Evaluate(expression), kind), -1);

    private void Guard(int root, Func<object?, long> fact, object? value)
    {
        if (root >= 0) Recorder!.Guard(root, fact, fact(value));
    }

    /// <summary>A parameter computed from a root value by <paramref name="binder"/> (given the whole shaped root value).</summary>
    private ParameterExpr Slot(int root, object? rootValue, Func<object?, SqlParameterValue> binder)
    {
        var parameter = new ParameterExpr(binder(rootValue));
        if (root >= 0) Recorder!.Slot(parameter, root, binder);
        return parameter;
    }

    // ── Entry points ────────────────────────────────────────────────────────

    /// <summary>Translates an expression used as a condition: bare boolean values become <c>value = TRUE</c>.</summary>
    public SqlExpr Predicate(Expression expression) => AsPredicate(Value(expression));

    /// <summary>Translates an expression used as a value.</summary>
    public SqlExpr Value(Expression expression) => Value(expression, null);

    private SqlExpr Value(Expression expression, ColumnMap? context)
    {
        if (!DependsOnParameters(expression)) return Evaluated(expression, context);

        return expression switch
        {
            MemberExpression member => Member(member),
            BinaryExpression binary => Binary(binary),
            UnaryExpression unary => Unary(unary, context),
            ConditionalExpression conditional => new CaseExpr(
                [new CaseWhen(Predicate(conditional.Test), Value(conditional.IfTrue, context))],
                Value(conditional.IfFalse, context)),
            MethodCallExpression call => Method(call),
            _ => throw Unsupported($"Expression '{expression.NodeType}' cannot be translated to SQL."),
        };
    }

    // ── Values and columns ──────────────────────────────────────────────────

    /// <summary>A value computed on the client: numeric/bool code constants inline, null as NULL, everything else a parameter.</summary>
    private SqlExpr Evaluated(Expression expression, ColumnMap? context)
    {
        if (expression is ConstantExpression { Value: { } constant } && IsInlineLiteral(constant) && context?.Converter is null)
            return new LiteralExpr(constant);

        var (value, root) = Evaluate(expression);
        Guard(root, IsNullFact, value);
        return value is null ? NullExpr.Instance : Slot(root, value, v => SqlParameterValue.ForColumn(v, context));
    }

    private static bool IsInlineLiteral(object value) =>
        value is bool or int or long or short or byte or sbyte or uint or ulong or ushort or decimal or double or float;

    private SqlExpr Member(MemberExpression member)
    {
        if (GroupMember(member) is { } groupMember) return groupMember;
        if (ColumnOf(member) is { } column) return column.Sql;

        // Nullable<T>.Value and .HasValue on a column.
        if (member.Expression is not null && Nullable.GetUnderlyingType(member.Expression.Type) is not null)
        {
            if (member.Member.Name == nameof(Nullable<int>.Value)) return Value(member.Expression);
            if (member.Member.Name == nameof(Nullable<int>.HasValue)) return new IsNullExpr(Value(member.Expression), Negated: true);
        }

        if (member.Expression is not null && MemberFunction(member) is { } function) return function;

        throw Unsupported($"Member '{member.Member.DeclaringType?.Name}.{member.Member.Name}' cannot be translated to SQL.");
    }

    /// <summary>Property access on a date/time or string column (<c>x.CreatedAt.Year</c>, <c>x.Name.Length</c>).</summary>
    private SqlExpr? MemberFunction(MemberExpression member)
    {
        var owner = member.Expression!;
        var type = Nullable.GetUnderlyingType(owner.Type) ?? owner.Type;
        if (type == typeof(string) && member.Member.Name == nameof(string.Length))
            return Function(SqlFunction.Length, owner);
        if (type != typeof(DateTime) && type != typeof(DateTimeOffset) && type != typeof(DateOnly)) return null;

        return member.Member.Name switch
        {
            nameof(DateTime.Year) => Function(SqlFunction.Year, owner),
            nameof(DateTime.Month) => Function(SqlFunction.Month, owner),
            nameof(DateTime.Day) => Function(SqlFunction.Day, owner),
            nameof(DateTime.Hour) => Function(SqlFunction.Hour, owner),
            nameof(DateTime.Minute) => Function(SqlFunction.Minute, owner),
            nameof(DateTime.Second) => Function(SqlFunction.Second, owner),
            nameof(DateTime.DayOfWeek) => Function(SqlFunction.DayOfWeek, owner),
            nameof(DateTime.Date) => Function(SqlFunction.Date, owner),
            _ => null,
        };
    }

    private FunctionExpr Function(SqlFunction function, params ReadOnlySpan<Expression> arguments)
    {
        var args = new SqlExpr[arguments.Length];
        for (var i = 0; i < args.Length; i++) args[i] = Value(arguments[i]);
        return new FunctionExpr(function, args);
    }

    /// <summary>A column reference for <paramref name="expression"/> (through conversions), or null.</summary>
    private (SqlExpr Sql, ColumnMap Column)? ColumnOf(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            expression = convert.Operand;
        if (expression is not MemberExpression { Expression: ParameterExpression parameter } member) return null;
        if (scope.TableOf(parameter) is not { } table) return null;

        var column = table.Map.FindProperty(member.Member.Name)
            ?? throw Unsupported($"'{table.Map.ClrType.Name}.{member.Member.Name}' is not a mapped column.");
        var name = table.Map.GetNames(scope.Policy).Of(column);
        return (new ColumnExpr(name, table.Alias), column);
    }

    private ColumnMap? ContextColumn(Expression expression) => ColumnOf(expression)?.Column;

    // ── Operators ───────────────────────────────────────────────────────────

    private SqlExpr Binary(BinaryExpression binary)
    {
        var left = binary.Left;
        var right = binary.Right;

        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso:
                return new BinaryExpr(SqlBinaryOperator.And, Predicate(left), Predicate(right));
            case ExpressionType.OrElse:
                return new BinaryExpr(SqlBinaryOperator.Or, Predicate(left), Predicate(right));
            case ExpressionType.And when IsBool(left.Type):
                return new BinaryExpr(SqlBinaryOperator.And, Predicate(left), Predicate(right));
            case ExpressionType.Or when IsBool(left.Type):
                return new BinaryExpr(SqlBinaryOperator.Or, Predicate(left), Predicate(right));
            case ExpressionType.Coalesce:
            {
                var context = ContextColumn(left) ?? ContextColumn(right);
                return new FunctionExpr(SqlFunction.Coalesce, [Value(left, context), Value(right, context)]);
            }
            case ExpressionType.Equal or ExpressionType.NotEqual:
                return Equality(left, right, binary.NodeType == ExpressionType.NotEqual);
        }

        var op = binary.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked when binary.Type == typeof(string) => SqlBinaryOperator.Concat,
            ExpressionType.Add or ExpressionType.AddChecked => SqlBinaryOperator.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => SqlBinaryOperator.Subtract,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => SqlBinaryOperator.Multiply,
            ExpressionType.Divide => SqlBinaryOperator.Divide,
            ExpressionType.Modulo => SqlBinaryOperator.Modulo,
            ExpressionType.LessThan => SqlBinaryOperator.LessThan,
            ExpressionType.LessThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
            ExpressionType.GreaterThan => SqlBinaryOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
            _ => throw Unsupported($"Operator '{binary.NodeType}' cannot be translated to SQL."),
        };

        var ctx = ContextColumn(left) ?? ContextColumn(right);
        return new BinaryExpr(op, Value(left, ctx), Value(right, ctx));
    }

    private SqlExpr Equality(Expression left, Expression right, bool negated)
    {
        // Comparing with null (a literal, or a captured value that is null now) is IS [NOT] NULL.
        if (IsNullValue(right)) return new IsNullExpr(Value(left), negated);
        if (IsNullValue(left)) return new IsNullExpr(Value(right), negated);

        var ctx = ContextColumn(left) ?? ContextColumn(right);
        var l = Value(left, ctx);
        var r = Value(right, ctx);
        if (IsBool(left.Type)) { l = AsBooleanValue(l); r = AsBooleanValue(r); }
        return new BinaryExpr(negated ? SqlBinaryOperator.NotEqual : SqlBinaryOperator.Equal, l, r);
    }

    private bool IsNullValue(Expression expression)
    {
        if (expression is ConstantExpression { Value: null }) return true;
        if (expression.Type.IsValueType && Nullable.GetUnderlyingType(expression.Type) is null) return false;
        if (DependsOnParameters(expression)) return false;
        var (value, root) = Evaluate(expression);
        Guard(root, IsNullFact, value);
        return value is null;
    }

    private SqlExpr Unary(UnaryExpression unary, ColumnMap? context) => unary.NodeType switch
    {
        ExpressionType.Not when IsBool(unary.Operand.Type) => Negate(Predicate(unary.Operand)),
        ExpressionType.Negate or ExpressionType.NegateChecked => new UnaryExpr(SqlUnaryOperator.Negate, Value(unary.Operand, context)),
        ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs => Value(unary.Operand, context),
        ExpressionType.UnaryPlus => Value(unary.Operand, context),
        _ => throw Unsupported($"Operator '{unary.NodeType}' cannot be translated to SQL."),
    };

    private static SqlExpr Negate(SqlExpr predicate) => predicate switch
    {
        IsNullExpr isNull => isNull with { Negated = !isNull.Negated },
        InListExpr inList => inList with { Negated = !inList.Negated },
        InSubqueryExpr inSub => inSub with { Negated = !inSub.Negated },
        ExistsExpr exists => exists with { Negated = !exists.Negated },
        LikeExpr like => like with { Negated = !like.Negated },
        _ => new UnaryExpr(SqlUnaryOperator.Not, predicate),
    };

    // ── Boolean positions ───────────────────────────────────────────────────

    /// <summary>Makes a boolean value usable as a condition: <c>col</c> → <c>col = TRUE</c>.</summary>
    internal static SqlExpr AsPredicate(SqlExpr expression) => IsPredicateNode(expression)
        ? expression
        : new BinaryExpr(SqlBinaryOperator.Equal, expression, new LiteralExpr(true));

    /// <summary>A predicate used where a boolean value is compared (<c>(a &gt; b) == flag</c>) becomes a CASE.</summary>
    private static SqlExpr AsBooleanValue(SqlExpr expression) => IsPredicateNode(expression)
        ? new CaseExpr([new CaseWhen(expression, new LiteralExpr(true))], new LiteralExpr(false))
        : expression;

    internal static bool IsPredicateNode(SqlExpr expression) => SqlPredicates.IsPredicate(expression);

    private static bool IsBool(Type type) => (Nullable.GetUnderlyingType(type) ?? type) == typeof(bool);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private bool DependsOnParameters(Expression expression) => ParameterDependency.DependsOn(expression, scope.Parameters);

    private static TranslationException Unsupported(string message) => new(message);
}
