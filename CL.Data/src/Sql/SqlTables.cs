namespace CL.Data.Sql;

/// <summary>The tables a statement reads or writes (FROM, joins, subqueries), for cache tags.</summary>
internal static class SqlTables
{
    public static IReadOnlyList<string> Of(SqlStatement statement)
    {
        HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);
        Statement(statement, tables);
        return [.. tables];
    }

    private static void Statement(SqlStatement statement, HashSet<string> tables)
    {
        switch (statement)
        {
            case SelectStatement select:
                tables.Add(select.From.Name);
                foreach (var join in select.Joins)
                {
                    tables.Add(join.Table.Name);
                    Expr(join.On, tables);
                }
                Expr(select.Where, tables);
                Expr(select.Having, tables);
                foreach (var item in select.Projection) Expr(item.Expression, tables);
                break;
            case UpdateStatement update:
                tables.Add(update.Table.Name);
                Expr(update.Where, tables);
                break;
            case DeleteStatement delete:
                tables.Add(delete.Table.Name);
                Expr(delete.Where, tables);
                break;
            case InsertStatement insert:
                tables.Add(insert.Table.Name);
                break;
            default:
                break;   // other statements carry their table in the write command's tags
        }
    }

    private static void Expr(SqlExpr? expr, HashSet<string> tables)
    {
        switch (expr)
        {
            case null:
                return;
            case InSubqueryExpr sub:
                Expr(sub.Operand, tables);
                Statement(sub.Subquery, tables);
                return;
            case ExistsExpr exists:
                Statement(exists.Subquery, tables);
                return;
            case SubqueryExpr subquery:
                Statement(subquery.Query, tables);
                return;
            case BinaryExpr b:
                Expr(b.Left, tables);
                Expr(b.Right, tables);
                return;
            case UnaryExpr u:
                Expr(u.Operand, tables);
                return;
            case IsNullExpr n:
                Expr(n.Operand, tables);
                return;
            case CaseExpr c:
                foreach (var when in c.Whens)
                {
                    Expr(when.When, tables);
                    Expr(when.Then, tables);
                }
                Expr(c.Else, tables);
                return;
            case FunctionExpr f:
                foreach (var a in f.Arguments) Expr(a, tables);
                return;
            default:
                return;
        }
    }
}
