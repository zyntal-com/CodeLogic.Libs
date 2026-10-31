using System.Text;

namespace CL.Data.Sql;

/// <summary>
/// How a provider writes SQL. CL.Data builds statements as a tree and renders them through the dialect,
/// which overrides only what differs from standard SQL. This base class holds the ANSI defaults; rendering
/// hooks for paging, locking, upserts and functions are added with the SQL tree.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Character that opens a quoted identifier.</summary>
    protected virtual char OpenQuote => '"';

    /// <summary>Character that closes a quoted identifier.</summary>
    protected virtual char CloseQuote => '"';

    /// <summary>Prefix of parameter placeholders.</summary>
    public virtual char ParameterPrefix => '@';

    /// <summary>Escape character used in LIKE patterns.</summary>
    public virtual char LikeEscape => '\\';

    /// <summary>Quotes an identifier, doubling any closing quote inside it.</summary>
    public virtual string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        var close = CloseQuote.ToString();
        return string.Concat(OpenQuote.ToString(), identifier.Replace(close, close + close, StringComparison.Ordinal), close);
    }

    /// <summary>Appends a quoted identifier.</summary>
    public void AppendIdentifier(StringBuilder sb, string identifier) => sb.Append(QuoteIdentifier(identifier));

    /// <summary>Quotes a possibly schema-qualified table name.</summary>
    public virtual string QuoteTable(string table, string? schema) =>
        schema is null ? QuoteIdentifier(table) : QuoteIdentifier(schema) + "." + QuoteIdentifier(table);

    /// <summary>Placeholder for the parameter at <paramref name="index"/> (<c>@p0</c>).</summary>
    public virtual string ParameterName(int index) => string.Create(null, stackalloc char[16], $"{ParameterPrefix}p{index}");

    /// <summary>Escapes LIKE wildcards in <paramref name="value"/> so it matches literally.</summary>
    public virtual string EscapeLike(string value)
    {
        if (value.AsSpan().IndexOfAny(['%', '_', LikeEscape, '[']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c == '%' || c == '_' || c == LikeEscape || (c == '[' && EscapesBracketInLike)) sb.Append(LikeEscape);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>True when <c>[</c> is a LIKE wildcard (SQL Server).</summary>
    protected virtual bool EscapesBracketInLike => false;

    // ── Rendering hooks (ANSI defaults; dialects override what differs) ─────

    /// <summary>Writes a literal (numbers and booleans only). Default: <c>TRUE</c>/<c>FALSE</c>, invariant numbers.</summary>
    public virtual void RenderLiteral(SqlWriter w, object value) =>
        w.Append(value is bool b ? (b ? "TRUE" : "FALSE") : SqlWriter.FormatNumber(value));

    /// <summary>Writes an expression in a condition (WHERE, ON, HAVING, WHEN). Default: unchanged.</summary>
    public virtual void RenderPredicate(SqlWriter w, SqlExpr expression) => w.Condition(expression);

    /// <summary>Writes a boolean literal used as a condition. Default: the literal.</summary>
    public virtual void RenderConditionLiteral(SqlWriter w, bool value) => RenderLiteral(w, value);

    /// <summary>Writes an expression in a value position (projection, THEN/ELSE). Default: unchanged.</summary>
    public virtual void RenderProjection(SqlWriter w, SqlExpr expression) => w.Expr(expression);

    /// <summary>Writes anything between <c>SELECT [DISTINCT]</c> and the projection (SQL Server <c>TOP</c>). Default: nothing.</summary>
    public virtual void RenderSelectPrefix(SqlWriter w, SelectStatement select) { }

    /// <summary>Writes table hints after the FROM table (SQL Server locking hints). Default: nothing.</summary>
    public virtual void RenderTableHints(SqlWriter w, SelectStatement select) { }

    /// <summary>Writes paging. Default: <c>LIMIT n OFFSET m</c>.</summary>
    public virtual void RenderPaging(SqlWriter w, SelectStatement select)
    {
        if (select.Limit is not null) w.Append(" LIMIT ").Expr(select.Limit);
        if (select.Offset is not null) w.Append(" OFFSET ").Expr(select.Offset);
    }

    /// <summary>Writes a locking clause. Default: <c>FOR UPDATE</c>/<c>FOR SHARE</c> with <c>NOWAIT</c>/<c>SKIP LOCKED</c>.</summary>
    public virtual void RenderLock(SqlWriter w, LockClause clause)
    {
        w.Append(clause.Mode == LockMode.Update ? " FOR UPDATE" : " FOR SHARE");
        w.Append(clause.Wait switch
        {
            LockWait.NoWait => " NOWAIT",
            LockWait.SkipLocked => " SKIP LOCKED",
            _ => "",
        });
    }

    /// <summary>Writes the ESCAPE clause after a LIKE pattern.</summary>
    public virtual void RenderLikeEscape(SqlWriter w) => w.Append(" ESCAPE '").Append(LikeEscape).Append('\'');

    /// <summary>Writes string concatenation. Default: <c>||</c>.</summary>
    public virtual void RenderConcat(SqlWriter w, SqlExpr left, SqlExpr right) =>
        w.Parenthesized(left).Append(" || ").Parenthesized(right);

    /// <summary>Writes an aggregate.</summary>
    public virtual void RenderAggregate(SqlWriter w, AggregateExpr aggregate)
    {
        w.Append(aggregate.Aggregate switch
        {
            SqlAggregate.Count => "COUNT(",
            SqlAggregate.Sum => "SUM(",
            SqlAggregate.Average => "AVG(",
            SqlAggregate.Min => "MIN(",
            SqlAggregate.Max => "MAX(",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate), aggregate.Aggregate, null),
        });
        if (aggregate.Distinct) w.Append("DISTINCT ");
        if (aggregate.Operand is null) w.Append('*');
        else w.Expr(aggregate.Operand);
        w.Append(')');
    }

    /// <summary>Writes a scalar function. Default: standard SQL names; date parts with <c>EXTRACT</c>.</summary>
    public virtual void RenderFunction(SqlWriter w, FunctionExpr function)
    {
        var a = function.Arguments;
        switch (function.Function)
        {
            case SqlFunction.Lower: Call(w, "LOWER", a); break;
            case SqlFunction.Upper: Call(w, "UPPER", a); break;
            case SqlFunction.Trim: Call(w, "TRIM", a); break;
            case SqlFunction.Length: Call(w, "CHAR_LENGTH", a); break;
            case SqlFunction.Substring: Call(w, "SUBSTRING", a); break;
            case SqlFunction.Replace: Call(w, "REPLACE", a); break;
            case SqlFunction.Coalesce: Call(w, "COALESCE", a); break;
            case SqlFunction.Abs: Call(w, "ABS", a); break;
            case SqlFunction.Round: Call(w, "ROUND", a); break;
            case SqlFunction.Floor: Call(w, "FLOOR", a); break;
            case SqlFunction.Ceiling: Call(w, "CEILING", a); break;
            case SqlFunction.Least: Call(w, "LEAST", a); break;
            case SqlFunction.Greatest: Call(w, "GREATEST", a); break;
            case SqlFunction.Year: Extract(w, "YEAR", a[0]); break;
            case SqlFunction.Month: Extract(w, "MONTH", a[0]); break;
            case SqlFunction.Day: Extract(w, "DAY", a[0]); break;
            case SqlFunction.Hour: Extract(w, "HOUR", a[0]); break;
            case SqlFunction.Minute: Extract(w, "MINUTE", a[0]); break;
            case SqlFunction.Second: Extract(w, "SECOND", a[0]); break;
            case SqlFunction.Date: w.Append("CAST(").Expr(a[0]).Append(" AS DATE)"); break;
            case SqlFunction.UtcNow: w.Append("CURRENT_TIMESTAMP"); break;
            case SqlFunction.DayOfWeek:
            case SqlFunction.BucketUtc:
            case SqlFunction.JsonValue:
            case SqlFunction.JsonExists:
            case SqlFunction.JsonContains:
            case SqlFunction.FullTextMatch:
                throw new SqlRenderException($"{GetType().Name} cannot render {function.Function}.");
            default:
                throw new ArgumentOutOfRangeException(nameof(function), function.Function, null);
        }
    }

    /// <summary>Writes <c>NAME(arg, ...)</c>.</summary>
    protected static void Call(SqlWriter w, string name, IReadOnlyList<SqlExpr> arguments)
    {
        w.Append(name).Append('(');
        w.List(arguments, static (writer, e) => writer.Expr(e));
        w.Append(')');
    }

    private static void Extract(SqlWriter w, string part, SqlExpr value) =>
        w.Append("EXTRACT(").Append(part).Append(" FROM ").Expr(value).Append(')');

    /// <summary>
    /// True when <see cref="RenderUpsert"/> writes more than one statement and must run inside a serializable
    /// transaction to be atomic (SQL Server). The executor opens one when the caller has none.
    /// </summary>
    public virtual bool UpsertRequiresTransaction => false;

    /// <summary>How many times an upsert binds each row's parameters (SQL Server's two-statement upsert binds them twice).</summary>
    public virtual int UpsertParameterMultiplier => 1;

    /// <summary>
    /// Qualifies bare column references in <paramref name="expression"/> with <paramref name="table"/>, for upserts whose
    /// incoming-row alias has the same column names as the target (MySQL row alias, SQL Server VALUES source).
    /// </summary>
    protected static SqlExpr QualifyColumns(SqlExpr expression, string table) => expression switch
    {
        ColumnExpr { Table: null } column => column with { Table = table },
        BinaryExpr b => b with { Left = QualifyColumns(b.Left, table), Right = QualifyColumns(b.Right, table) },
        UnaryExpr u => u with { Operand = QualifyColumns(u.Operand, table) },
        FunctionExpr f => f with { Arguments = f.Arguments.Select(a => QualifyColumns(a, table)).ToArray() },
        IsNullExpr n => n with { Operand = QualifyColumns(n.Operand, table) },
        CaseExpr c => new CaseExpr(c.Whens.Select(x => new CaseWhen(QualifyColumns(x.When, table), QualifyColumns(x.Then, table))).ToArray(),
            c.Else is null ? null : QualifyColumns(c.Else, table)),
        _ => expression,
    };

    /// <summary>Writes the incoming value of a column in an upsert's update arm. Default: <c>EXCLUDED.col</c>.</summary>
    public virtual void RenderExcluded(SqlWriter w, string column) => w.Append("EXCLUDED.").Identifier(column);

    /// <summary>Writes an INSERT. Default: <c>INSERT INTO t (cols) VALUES (...), (...) [RETURNING ...]</c>.</summary>
    public virtual void RenderInsert(SqlWriter w, InsertStatement insert)
    {
        w.Append("INSERT INTO ").Table(insert.Table, withAlias: false).Append(" (").Identifiers(insert.Columns).Append(')');
        RenderInsertValues(w, insert);
        if (insert.Returning.Count > 0) RenderReturning(w, insert.Returning);
    }

    /// <summary>Writes the <c>VALUES</c> rows of an INSERT.</summary>
    protected static void RenderInsertValues(SqlWriter w, InsertStatement insert)
    {
        w.Append(" VALUES ");
        w.List(insert.Rows, static (writer, row) =>
        {
            writer.Append('(');
            writer.List(row, static (inner, e) => inner.Expr(e));
            writer.Append(')');
        });
    }

    /// <summary>Writes a RETURNING clause. Default: <c> RETURNING a, b</c>.</summary>
    public virtual void RenderReturning(SqlWriter w, IReadOnlyList<string> columns) =>
        w.Append(" RETURNING ").Identifiers(columns);

    /// <summary>Writes an UPDATE.</summary>
    public virtual void RenderUpdate(SqlWriter w, UpdateStatement update)
    {
        w.Append("UPDATE ").Table(update.Table, withAlias: false).Append(" SET ");
        w.List(update.Set, static (writer, s) => writer.Identifier(s.Column).Append(" = ").Expr(s.Value));
        if (update.Where is not null) w.Append(" WHERE ").Predicate(update.Where);
        if (update.Returning.Count > 0) RenderReturning(w, update.Returning);
    }

    /// <summary>
    /// Writes a DELETE. A limited delete is written as a key subquery
    /// (<c>WHERE (keys) IN (SELECT keys ... LIMIT n)</c>); dialects with a native form override this.
    /// </summary>
    public virtual void RenderDelete(SqlWriter w, DeleteStatement delete)
    {
        w.Append("DELETE FROM ").Table(delete.Table, withAlias: false);
        if (delete.Limit is null)
        {
            if (delete.Where is not null) w.Append(" WHERE ").Predicate(delete.Where);
            return;
        }

        if (delete.KeyColumns.Count == 0)
            throw new SqlRenderException("A limited DELETE needs the key columns.");
        var keys = delete.KeyColumns;
        w.Append(" WHERE ");
        if (keys.Count == 1) w.Identifier(keys[0]);
        else w.Append('(').Identifiers(keys).Append(')');
        w.Append(" IN (");
        w.Select(new SelectStatement(delete.Table with { Alias = null })
        {
            Projection = keys.Select(k => new ProjectionItem(new ColumnExpr(k))).ToArray(),
            Where = delete.Where,
            Limit = delete.Limit,
        });
        w.Append(')');
    }

    /// <summary>Writes an upsert. Default: <c>INSERT ... ON CONFLICT (keys) DO UPDATE SET ... | DO NOTHING</c>.</summary>
    public virtual void RenderUpsert(SqlWriter w, UpsertStatement upsert)
    {
        var insert = upsert.Insert;
        w.Append("INSERT INTO ").Table(insert.Table, withAlias: false).Append(" (").Identifiers(insert.Columns).Append(')');
        RenderInsertValues(w, insert);
        w.Append(" ON CONFLICT (").Identifiers(upsert.ConflictColumns).Append(')');
        if (upsert.IgnoreOnConflict || upsert.UpdateSet.Count == 0) w.Append(" DO NOTHING");
        else
        {
            w.Append(" DO UPDATE SET ");
            w.List(upsert.UpdateSet, static (writer, s) => writer.Identifier(s.Column).Append(" = ").Expr(s.Value));
        }
        if (insert.Returning.Count > 0) RenderReturning(w, insert.Returning);
    }
}
