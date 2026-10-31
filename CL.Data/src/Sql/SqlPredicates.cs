namespace CL.Data.Sql;

/// <summary>Helpers for boolean positions in SQL (conditions versus values).</summary>
public static class SqlPredicates
{
    /// <summary>
    /// True when <paramref name="expression"/> is a condition (comparison, logic, IS NULL, IN, EXISTS, LIKE, a
    /// boolean literal or a boolean function) rather than a plain value.
    /// </summary>
    public static bool IsPredicate(SqlExpr expression) => expression switch
    {
        BinaryExpr { Operator: SqlBinaryOperator.And or SqlBinaryOperator.Or or SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
            or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual or SqlBinaryOperator.GreaterThan
            or SqlBinaryOperator.GreaterThanOrEqual } => true,
        UnaryExpr { Operator: SqlUnaryOperator.Not } => true,
        IsNullExpr or InListExpr or InSubqueryExpr or ExistsExpr or LikeExpr => true,
        LiteralExpr { Value: bool } => true,
        FunctionExpr { Function: SqlFunction.JsonExists or SqlFunction.JsonContains or SqlFunction.FullTextMatch } => true,
        _ => false,
    };
}
