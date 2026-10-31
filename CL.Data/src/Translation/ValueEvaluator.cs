using System.Linq.Expressions;
using System.Reflection;

namespace CL.Data.Translation;

/// <summary>
/// Evaluates the parts of a lambda that do not depend on its parameters (captured variables, constants,
/// <c>DateTime.UtcNow.AddDays(-7)</c>) to the values that become SQL parameters.
/// </summary>
internal static class ValueEvaluator
{
    /// <summary>Evaluates <paramref name="expression"/>. Member chains on closures are read directly; anything else is compiled (interpreted under AOT).</summary>
    public static object? Evaluate(Expression expression)
    {
        if (TryFast(expression, out var value)) return value;
        var body = expression.Type.IsValueType ? Expression.Convert(expression, typeof(object)) : expression;
        return Expression.Lambda<Func<object?>>(body).Compile()();
    }

    private static bool TryFast(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression member:
                object? target = null;
                if (member.Expression is not null && !TryFast(member.Expression, out target))
                {
                    value = null;
                    return false;
                }
                if (member.Expression is not null && target is null)
                {
                    value = null;
                    return false;   // let the compiled path raise the NullReferenceException with context
                }
                value = member.Member switch
                {
                    FieldInfo field => field.GetValue(target),
                    PropertyInfo property => property.GetValue(target),
                    _ => null,
                };
                return member.Member is FieldInfo or PropertyInfo;
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                when convert.Type == typeof(object) || convert.Type.IsAssignableFrom(convert.Operand.Type):
                return TryFast(convert.Operand, out value);
            default:
                value = null;
                return false;
        }
    }
}

/// <summary>Finds whether an expression depends on any of a set of lambda parameters.</summary>
internal sealed class ParameterDependency : ExpressionVisitor
{
    private readonly IReadOnlyCollection<ParameterExpression> _parameters;
    private bool _found;

    private ParameterDependency(IReadOnlyCollection<ParameterExpression> parameters) => _parameters = parameters;

    /// <summary>True when <paramref name="expression"/> reads any of <paramref name="parameters"/>.</summary>
    public static bool DependsOn(Expression expression, IReadOnlyCollection<ParameterExpression> parameters)
    {
        if (expression is ConstantExpression) return false;
        var finder = new ParameterDependency(parameters);
        finder.Visit(expression);
        return finder._found;
    }

    public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (_parameters.Contains(node)) _found = true;
        return node;
    }
}
