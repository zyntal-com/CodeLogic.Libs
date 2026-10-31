using System.Globalization;
using System.Text;

namespace CL.Data.Sql;

/// <summary>Rendered SQL: text with placeholders <c>p0</c>, <c>p1</c>, ... and the parameter values in that order.</summary>
/// <param name="Text">SQL text.</param>
/// <param name="Parameters">Parameter values.</param>
public sealed record RenderedSql(string Text, IReadOnlyList<SqlParameterValue> Parameters);

/// <summary>Thrown while rendering when a dialect cannot express a node; callers turn it into <c>data.unsupported</c>.</summary>
public sealed class SqlRenderException(string message) : Exception(message);

/// <summary>
/// Renders the SQL tree to text through a <see cref="SqlDialect"/>. Dialect hooks receive the writer and use
/// its methods to append text, identifiers, nested expressions and parameters.
/// </summary>
public sealed class SqlWriter
{
    private readonly StringBuilder _sb = new(256);
    private readonly List<SqlParameterValue> _parameters = [];

    private SqlWriter(SqlDialect dialect) => Dialect = dialect;

    /// <summary>The dialect being rendered.</summary>
    public SqlDialect Dialect { get; }

    /// <summary>Renders <paramref name="statement"/>.</summary>
    public static RenderedSql Render(SqlStatement statement, SqlDialect dialect)
    {
        var writer = new SqlWriter(dialect);
        writer.Statement(statement);
        return new RenderedSql(writer._sb.ToString(), writer._parameters.ToArray());
    }

    /// <summary>Renders a single expression (tests and diagnostics).</summary>
    public static RenderedSql RenderExpression(SqlExpr expression, SqlDialect dialect)
    {
        var writer = new SqlWriter(dialect);
        writer.Expr(expression);
        return new RenderedSql(writer._sb.ToString(), writer._parameters.ToArray());
    }

    // ── Primitives for dialect hooks ────────────────────────────────────────

    /// <summary>Appends raw SQL text.</summary>
    public SqlWriter Append(string sql)
    {
        _sb.Append(sql);
        return this;
    }

    /// <summary>Appends one character.</summary>
    public SqlWriter Append(char c)
    {
        _sb.Append(c);
        return this;
    }

    /// <summary>Appends a quoted identifier.</summary>
    public SqlWriter Identifier(string name)
    {
        _sb.Append(Dialect.QuoteIdentifier(name));
        return this;
    }

    /// <summary>Appends a (possibly qualified and aliased) table.</summary>
    public SqlWriter Table(TableRef table, bool withAlias = true)
    {
        _sb.Append(Dialect.QuoteTable(table.Name, table.Schema));
        if (withAlias && table.Alias is not null) _sb.Append(' ').Append(Dialect.QuoteIdentifier(table.Alias));
        return this;
    }

    /// <summary>Binds <paramref name="value"/> and appends its placeholder.</summary>
    public SqlWriter Parameter(SqlParameterValue value)
    {
        _sb.Append(Dialect.ParameterName(_parameters.Count));
        _parameters.Add(value);
        return this;
    }

    /// <summary>Appends a comma-separated list.</summary>
    public SqlWriter List<T>(IReadOnlyList<T> items, Action<SqlWriter, T> each)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            each(this, items[i]);
        }
        return this;
    }

    /// <summary>Appends a comma-separated list of quoted identifiers.</summary>
    public SqlWriter Identifiers(IReadOnlyList<string> names) => List(names, static (w, n) => w.Identifier(n));

    /// <summary>Appends an expression.</summary>
    public SqlWriter Expr(SqlExpr expression)
    {
        Expression(expression, 0, false);
        return this;
    }

    // ── Statements ──────────────────────────────────────────────────────────

    private void Statement(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select: Select(select); break;
            case InsertStatement insert: Dialect.RenderInsert(this, insert); break;
            case UpdateStatement update: Dialect.RenderUpdate(this, update); break;
            case DeleteStatement delete: Dialect.RenderDelete(this, delete); break;
            case UpsertStatement upsert: Dialect.RenderUpsert(this, upsert); break;
        }
    }

    /// <summary>Appends a full SELECT (used for statements and subqueries).</summary>
    public void Select(SelectStatement s)
    {
        _sb.Append("SELECT ");
        if (s.Distinct) _sb.Append("DISTINCT ");
        Dialect.RenderSelectPrefix(this, s);
        if (s.Projection.Count == 0) _sb.Append('*');
        else List(s.Projection, static (w, p) =>
        {
            w.Dialect.RenderProjection(w, p.Expression);
            if (p.Alias is not null) w.Append(" AS ").Identifier(p.Alias);
        });

        _sb.Append(" FROM ");
        Table(s.From);
        Dialect.RenderTableHints(this, s);
        foreach (var join in s.Joins)
        {
            _sb.Append(join.Kind switch
            {
                SqlJoinKind.Inner => " INNER JOIN ",
                SqlJoinKind.Left => " LEFT JOIN ",
                SqlJoinKind.Right => " RIGHT JOIN ",
                SqlJoinKind.Cross => " CROSS JOIN ",
                _ => throw new ArgumentOutOfRangeException(nameof(s), join.Kind, null),
            });
            Table(join.Table);
            if (join.On is not null && join.Kind != SqlJoinKind.Cross)
            {
                _sb.Append(" ON ");
                Predicate(join.On);
            }
        }

        if (s.Where is not null)
        {
            _sb.Append(" WHERE ");
            Predicate(s.Where);
        }
        if (s.GroupBy.Count > 0)
        {
            _sb.Append(" GROUP BY ");
            List(s.GroupBy, static (w, e) => w.Expr(e));
        }
        if (s.Having is not null)
        {
            _sb.Append(" HAVING ");
            Predicate(s.Having);
        }
        if (s.OrderBy.Count > 0)
        {
            _sb.Append(" ORDER BY ");
            List(s.OrderBy, static (w, o) =>
            {
                w.Expr(o.Expression);
                if (o.Descending) w.Append(" DESC");
            });
        }
        if (s.Limit is not null || s.Offset is not null) Dialect.RenderPaging(this, s);
        if (s.Lock is not null) Dialect.RenderLock(this, s.Lock);
    }

    /// <summary>Appends an expression used as a condition (dialects may need to adapt boolean values).</summary>
    public SqlWriter Predicate(SqlExpr expression)
    {
        Dialect.RenderPredicate(this, expression);
        return this;
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    private void Expression(SqlExpr expression, int parentPrecedence, bool rightOperand)
    {
        var precedence = Precedence(expression);
        var parenthesize = precedence < parentPrecedence || (rightOperand && precedence == parentPrecedence && precedence < 8);
        if (parenthesize) _sb.Append('(');

        switch (expression)
        {
            case ColumnExpr column:
                if (column.Table is not null) Identifier(column.Table).Append('.');
                Identifier(column.Name);
                break;
            case ParameterExpr parameter:
                Parameter(parameter.Value);
                break;
            case LiteralExpr literal:
                Dialect.RenderLiteral(this, literal.Value);
                break;
            case NullExpr:
                _sb.Append("NULL");
                break;
            case BinaryExpr binary:
                Binary(binary, precedence);
                break;
            case UnaryExpr { Operator: SqlUnaryOperator.Not } not:
                // Always parenthesize: MySQL's HIGH_NOT_PRECEDENCE mode makes NOT bind tighter than comparisons.
                _sb.Append("NOT ");
                Expression(not.Operand, 8, false);
                break;
            case UnaryExpr negate:
                _sb.Append('-');
                Expression(negate.Operand, precedence, false);
                break;
            case IsNullExpr isNull:
                Expression(isNull.Operand, precedence, false);
                _sb.Append(isNull.Negated ? " IS NOT NULL" : " IS NULL");
                break;
            case InListExpr { Values.Count: 0 } emptyIn:
                // x IN () is not valid SQL; an empty list matches nothing (everything when negated).
                _sb.Append(emptyIn.Negated ? "1 = 1" : "1 = 0");
                break;
            case InListExpr inList:
                Expression(inList.Operand, precedence, false);
                _sb.Append(inList.Negated ? " NOT IN (" : " IN (");
                List(inList.Values, static (w, v) => w.Expr(v));
                _sb.Append(')');
                break;
            case InSubqueryExpr inSubquery:
                Expression(inSubquery.Operand, precedence, false);
                _sb.Append(inSubquery.Negated ? " NOT IN (" : " IN (");
                Select(inSubquery.Subquery);
                _sb.Append(')');
                break;
            case ExistsExpr exists:
                _sb.Append(exists.Negated ? "NOT EXISTS (" : "EXISTS (");
                Select(exists.Subquery);
                _sb.Append(')');
                break;
            case LikeExpr like:
                Expression(like.Operand, precedence, false);
                _sb.Append(like.Negated ? " NOT LIKE " : " LIKE ");
                Expression(like.Pattern, precedence, true);
                Dialect.RenderLikeEscape(this);
                break;
            case FunctionExpr function:
                Dialect.RenderFunction(this, function);
                break;
            case AggregateExpr aggregate:
                Dialect.RenderAggregate(this, aggregate);
                break;
            case CaseExpr @case:
                _sb.Append("CASE");
                foreach (var arm in @case.Whens)
                {
                    _sb.Append(" WHEN ");
                    Predicate(arm.When);
                    _sb.Append(" THEN ");
                    Dialect.RenderProjection(this, arm.Then);
                }
                if (@case.Else is not null)
                {
                    _sb.Append(" ELSE ");
                    Dialect.RenderProjection(this, @case.Else);
                }
                _sb.Append(" END");
                break;
            case StarExpr star:
                if (star.Table is not null) Identifier(star.Table).Append('.');
                _sb.Append('*');
                break;
            case ExcludedExpr excluded:
                Dialect.RenderExcluded(this, excluded.Column);
                break;
            case SubqueryExpr subquery:
                _sb.Append('(');
                Select(subquery.Query);
                _sb.Append(')');
                break;
        }

        if (parenthesize) _sb.Append(')');
    }

    private void Binary(BinaryExpr binary, int precedence)
    {
        if (binary.Operator == SqlBinaryOperator.Concat)
        {
            Dialect.RenderConcat(this, binary.Left, binary.Right);
            return;
        }

        Expression(binary.Left, precedence, false);
        _sb.Append(binary.Operator switch
        {
            SqlBinaryOperator.Add => " + ",
            SqlBinaryOperator.Subtract => " - ",
            SqlBinaryOperator.Multiply => " * ",
            SqlBinaryOperator.Divide => " / ",
            SqlBinaryOperator.Modulo => " % ",
            SqlBinaryOperator.Equal => " = ",
            SqlBinaryOperator.NotEqual => " <> ",
            SqlBinaryOperator.LessThan => " < ",
            SqlBinaryOperator.LessThanOrEqual => " <= ",
            SqlBinaryOperator.GreaterThan => " > ",
            SqlBinaryOperator.GreaterThanOrEqual => " >= ",
            SqlBinaryOperator.And => " AND ",
            SqlBinaryOperator.Or => " OR ",
            SqlBinaryOperator.Concat => throw new InvalidOperationException(),
            _ => throw new ArgumentOutOfRangeException(nameof(binary), binary.Operator, null),
        });
        Expression(binary.Right, precedence, true);
    }

    /// <summary>Appends <paramref name="expression"/> wrapped in parentheses.</summary>
    public SqlWriter Parenthesized(SqlExpr expression)
    {
        _sb.Append('(');
        Expression(expression, 0, false);
        _sb.Append(')');
        return this;
    }

    /// <summary>Operator precedence: higher binds tighter; 8 = atomic.</summary>
    private static int Precedence(SqlExpr expression) => expression switch
    {
        BinaryExpr { Operator: SqlBinaryOperator.Or } => 1,
        BinaryExpr { Operator: SqlBinaryOperator.And } => 2,
        UnaryExpr { Operator: SqlUnaryOperator.Not } => 3,
        ExistsExpr => 8,
        BinaryExpr { Operator: SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.LessThan
            or SqlBinaryOperator.LessThanOrEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual } => 4,
        IsNullExpr or InListExpr or InSubqueryExpr or LikeExpr => 4,
        BinaryExpr { Operator: SqlBinaryOperator.Add or SqlBinaryOperator.Subtract or SqlBinaryOperator.Concat } => 5,
        BinaryExpr => 6,
        UnaryExpr => 7,
        _ => 8,
    };

    /// <summary>Formats a numeric literal invariantly.</summary>
    public static string FormatNumber(object value) => value switch
    {
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new SqlRenderException($"Literal of type {value.GetType().Name} cannot be written into SQL; bind it as a parameter."),
    };
}
