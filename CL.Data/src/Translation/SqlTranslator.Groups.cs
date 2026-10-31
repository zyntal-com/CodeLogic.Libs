using System.Linq.Expressions;
using CL.Data.Sql;

namespace CL.Data.Translation;

internal sealed partial class ExpressionTranslator
{
    /// <summary><c>g.Key</c> or <c>g.Key.Member</c> of a grouped-query lambda, or null when the member is something else.</summary>
    private SqlExpr? GroupMember(MemberExpression member)
    {
        if (scope.Group is not { } group) return null;

        if (member.Expression == group.Parameter && member.Member.Name == "Key")
            return group.Key ?? throw Unsupported("The group key has several columns; use its members (g.Key.Name).");

        if (member.Expression is MemberExpression { Member.Name: "Key" } key && key.Expression == group.Parameter)
            return group.KeyMembers.TryGetValue(member.Member.Name, out var part)
                ? part
                : throw Unsupported($"'{member.Member.Name}' is not part of the group key.");

        return null;
    }

    /// <summary>An aggregate call on the group parameter (<c>g.Sum(o =&gt; o.Total)</c>), or null.</summary>
    private SqlExpr? GroupAggregate(MethodCallExpression call)
    {
        if (scope.Group is not { } group || call.Object != group.Parameter) return null;
        var args = call.Arguments;
        var name = call.Method.Name;

        switch (name)
        {
            case "Count" or "LongCount" when args.Count == 0:
            case "Any" when args.Count == 0:
                var count = new AggregateExpr(SqlAggregate.Count, null);
                return name == "Any" ? new BinaryExpr(SqlBinaryOperator.GreaterThan, count, new LiteralExpr(0)) : count;

            case "Count" or "Any" when args.Count == 1:
            {
                // COUNT of matching rows: SUM(CASE WHEN p THEN 1 ELSE 0 END).
                var matches = new AggregateExpr(SqlAggregate.Sum,
                    new CaseExpr([new CaseWhen(Predicate(Selector(args[0], group)), new LiteralExpr(1))], new LiteralExpr(0)));
                var total = new FunctionExpr(SqlFunction.Coalesce, [matches, new LiteralExpr(0)]);
                return name == "Any" ? new BinaryExpr(SqlBinaryOperator.GreaterThan, total, new LiteralExpr(0)) : total;
            }

            case "CountDistinct" when args.Count == 1:
                return new AggregateExpr(SqlAggregate.Count, Value(Selector(args[0], group)), Distinct: true);

            case "Sum" when args.Count == 1:
                return new FunctionExpr(SqlFunction.Coalesce,
                    [new AggregateExpr(SqlAggregate.Sum, Value(Selector(args[0], group))), new LiteralExpr(0)]);

            case "Average" when args.Count == 1:
            {
                var body = Selector(args[0], group);
                var operand = Value(body);
                // AVG over an integer column truncates on SQL Server; promote it first.
                if (CL.Data.Mapping.EntityMap.IsInteger(Nullable.GetUnderlyingType(body.Type) ?? body.Type))
                    operand = new BinaryExpr(SqlBinaryOperator.Multiply, operand, new LiteralExpr(1.0m));
                return new AggregateExpr(SqlAggregate.Average, operand);
            }

            case "Min" when args.Count == 1:
                return new AggregateExpr(SqlAggregate.Min, Value(Selector(args[0], group)));

            case "Max" when args.Count == 1:
                return new AggregateExpr(SqlAggregate.Max, Value(Selector(args[0], group)));
        }

        throw Unsupported($"Group method '{name}' with these arguments cannot be translated to SQL.", call.ToString());
    }

    /// <summary>The body of an aggregate's selector lambda, with its parameter bound to the grouped table.</summary>
    private Expression Selector(Expression argument, GroupScope group)
    {
        while (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote) argument = quote.Operand;
        if (argument is not LambdaExpression lambda || lambda.Parameters.Count != 1)
            throw Unsupported("A group aggregate needs an inline lambda (o => o.Total).", argument.ToString());
        scope.Bind(lambda.Parameters[0], group.Table.Map, group.Table.Alias);
        return lambda.Body;
    }
}
