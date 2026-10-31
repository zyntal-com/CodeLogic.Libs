using CL.Data.Sql;

namespace CL.Data.SqlServer;

/// <summary>
/// SQL Server dialect (2019 and later):
/// <list type="bullet">
/// <item><c>[bracket]</c> identifiers.</item>
/// <item><c>1</c>/<c>0</c> booleans, with conditions turned into <c>CASE</c> in value positions.</item>
/// <item><c>TOP</c>, or <c>OFFSET ... FETCH</c> with an <c>ORDER BY</c>.</item>
/// <item>Locking table hints.</item>
/// <item><c>OUTPUT INSERTED</c>.</item>
/// <item><c>DELETE TOP</c>.</item>
/// <item>A serializable two-statement upsert.</item>
/// </list>
/// </summary>
public sealed class SqlServerSqlDialect : SqlDialect
{
    private const string ExcludedAlias = "excluded";

    /// <inheritdoc />
    protected override char OpenQuote => '[';

    /// <inheritdoc />
    protected override char CloseQuote => ']';

    /// <inheritdoc />
    protected override bool EscapesBracketInLike => true;

    /// <inheritdoc />
    public override bool UpsertRequiresTransaction => true;

    /// <inheritdoc />
    public override int UpsertParameterMultiplier => 2;

    /// <inheritdoc />
    public override void RenderLiteral(SqlWriter w, object value) =>
        w.Append(value is bool b ? (b ? "1" : "0") : SqlWriter.FormatNumber(value));

    /// <inheritdoc />
    public override void RenderConditionLiteral(SqlWriter w, bool value) => w.Append(value ? "1 = 1" : "1 = 0");

    /// <inheritdoc />
    public override void RenderProjection(SqlWriter w, SqlExpr expression)
    {
        if (SqlPredicates.IsPredicate(expression) && expression is not LiteralExpr)
            w.Append("CASE WHEN ").Condition(expression).Append(" THEN 1 ELSE 0 END");
        else
            w.Expr(expression);
    }

    /// <inheritdoc />
    public override void RenderSelectPrefix(SqlWriter w, SelectStatement select)
    {
        if (select.Limit is not null && select.Offset is null) w.Append("TOP (").Expr(select.Limit).Append(") ");
    }

    /// <inheritdoc />
    public override void RenderPaging(SqlWriter w, SelectStatement select)
    {
        if (select.Offset is null) return;   // a plain limit is TOP (n)
        if (select.OrderBy.Count == 0) w.Append(" ORDER BY (SELECT NULL)");
        w.Append(" OFFSET ").Expr(select.Offset).Append(" ROWS");
        if (select.Limit is not null) w.Append(" FETCH NEXT ").Expr(select.Limit).Append(" ROWS ONLY");
    }

    /// <inheritdoc />
    public override void RenderTableHints(SqlWriter w, SelectStatement select)
    {
        if (select.Lock is not { } clause) return;
        w.Append(clause.Mode == LockMode.Update ? " WITH (UPDLOCK, ROWLOCK" : " WITH (HOLDLOCK, ROWLOCK");
        w.Append(clause.Wait switch
        {
            LockWait.NoWait => ", NOWAIT)",
            LockWait.SkipLocked => ", READPAST)",
            _ => ")",
        });
    }

    /// <inheritdoc />
    public override void RenderLock(SqlWriter w, LockClause clause) { }   // expressed as table hints

    /// <inheritdoc />
    public override void RenderConcat(SqlWriter w, SqlExpr left, SqlExpr right) =>
        w.Append("CONCAT(").Expr(left).Append(", ").Expr(right).Append(')');

    /// <inheritdoc />
    public override void RenderFunction(SqlWriter w, FunctionExpr function)
    {
        var a = function.Arguments;
        switch (function.Function)
        {
            case SqlFunction.Length: Call(w, "LEN", a); break;
            case SqlFunction.Year: Call(w, "YEAR", a); break;
            case SqlFunction.Month: Call(w, "MONTH", a); break;
            case SqlFunction.Day: Call(w, "DAY", a); break;
            case SqlFunction.Hour: DatePart(w, "hour", a[0]); break;
            case SqlFunction.Minute: DatePart(w, "minute", a[0]); break;
            case SqlFunction.Second: DatePart(w, "second", a[0]); break;
            case SqlFunction.DayOfWeek:
                // 0 = Sunday whatever SET DATEFIRST says.
                w.Append("((DATEPART(weekday, ").Expr(a[0]).Append(") + @@DATEFIRST - 1) % 7)");
                break;
            case SqlFunction.Date: w.Append("CAST(").Expr(a[0]).Append(" AS date)"); break;
            case SqlFunction.BucketUtc:
                w.Append("DATEADD(second, (DATEDIFF_BIG(second, '1970-01-01', ").Expr(a[0]).Append(") / ").Expr(a[1])
                 .Append(") * ").Expr(a[1]).Append(", CAST('1970-01-01' AS datetime2))");
                break;
            case SqlFunction.UtcNow: w.Append("SYSUTCDATETIME()"); break;
            case SqlFunction.Least: MinMax(w, a, "<="); break;       // LEAST/GREATEST need SQL Server 2022
            case SqlFunction.Greatest: MinMax(w, a, ">="); break;
            case SqlFunction.JsonValue: Call(w, "JSON_VALUE", a); break;
            case SqlFunction.JsonExists:
                w.Append("(JSON_QUERY(").Expr(a[0]).Append(", ").Expr(a[1]).Append(") IS NOT NULL OR JSON_VALUE(")
                 .Expr(a[0]).Append(", ").Expr(a[1]).Append(") IS NOT NULL)");
                break;
            case SqlFunction.JsonContains:
                throw new SqlRenderException("SQL Server has no JSON containment test; use SqlFn.Json.Value or Exists.");
            case SqlFunction.FullTextMatch: Call(w, "CONTAINS", a); break;
            default: base.RenderFunction(w, function); break;
        }
    }

    private static void DatePart(SqlWriter w, string part, SqlExpr value) =>
        w.Append("DATEPART(").Append(part).Append(", ").Expr(value).Append(')');

    private static void MinMax(SqlWriter w, IReadOnlyList<SqlExpr> args, string op)
    {
        if (args.Count == 1)
        {
            w.Expr(args[0]);
            return;
        }
        // Fold pairwise: f(a, b, c) = f(f(a, b), c).
        SqlExpr folded = args[0];
        for (var i = 1; i < args.Count; i++)
        {
            var next = args[i];
            folded = new CaseExpr([new CaseWhen(new BinaryExpr(op == "<=" ? SqlBinaryOperator.LessThanOrEqual : SqlBinaryOperator.GreaterThanOrEqual,
                folded, next), folded)], next);
        }
        w.Expr(folded);
    }

    /// <inheritdoc />
    public override void RenderInsert(SqlWriter w, InsertStatement insert)
    {
        w.Append("INSERT INTO ").Table(insert.Table, withAlias: false).Append(" (").Identifiers(insert.Columns).Append(')');
        Output(w, insert.Returning);
        RenderInsertValues(w, insert);
    }

    /// <inheritdoc />
    public override void RenderUpdate(SqlWriter w, UpdateStatement update)
    {
        w.Append("UPDATE ").Table(update.Table, withAlias: false).Append(" SET ");
        w.List(update.Set, static (writer, s) => writer.Identifier(s.Column).Append(" = ").Expr(s.Value));
        Output(w, update.Returning);
        if (update.Where is not null) w.Append(" WHERE ").Predicate(update.Where);
    }

    /// <inheritdoc />
    public override void RenderReturning(SqlWriter w, IReadOnlyList<string> columns) => Output(w, columns);

    private static void Output(SqlWriter w, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) return;
        w.Append(" OUTPUT ");
        w.List(columns, static (writer, c) => writer.Append("INSERTED.").Identifier(c));
    }

    /// <inheritdoc />
    public override void RenderDelete(SqlWriter w, DeleteStatement delete)
    {
        w.Append("DELETE ");
        if (delete.Limit is not null) w.Append("TOP (").Expr(delete.Limit).Append(") ");
        w.Append("FROM ").Table(delete.Table, withAlias: false);
        if (delete.Where is not null) w.Append(" WHERE ").Predicate(delete.Where);
    }

    /// <inheritdoc />
    public override void RenderExcluded(SqlWriter w, string column) => w.Identifier(ExcludedAlias).Append('.').Identifier(column);

    /// <summary>
    /// Writes an UPDATE of the existing rows from a <c>VALUES</c> source, then an INSERT of the missing ones. Both take
    /// update/range locks (<c>UPDLOCK, SERIALIZABLE</c>), and the executor runs them in one transaction, so concurrent
    /// upserts of the same key cannot both insert.
    /// </summary>
    public override void RenderUpsert(SqlWriter w, UpsertStatement upsert)
    {
        var insert = upsert.Insert;
        var target = insert.Table with { Alias = null };
        var name = target.Name;

        if (!upsert.IgnoreOnConflict && upsert.UpdateSet.Count > 0)
        {
            w.Append("UPDATE ").Identifier(name).Append(" SET ");
            w.List(upsert.UpdateSet, (writer, s) => writer.Identifier(name).Append('.').Identifier(s.Column).Append(" = ")
                .Expr(QualifyColumns(s.Value, name)));
            w.Append(" FROM ").Table(target).Append(" WITH (UPDLOCK, SERIALIZABLE) INNER JOIN ");
            Source(w, insert);
            w.Append(" ON ");
            KeyMatch(w, upsert.ConflictColumns, name);
            w.Append(";\n");
        }

        w.Append("INSERT INTO ").Table(target).Append(" (").Identifiers(insert.Columns).Append(')');
        Output(w, insert.Returning);
        w.Append(" SELECT ");
        w.List(insert.Columns, static (writer, c) => writer.Identifier(ExcludedAlias).Append('.').Identifier(c));
        w.Append(" FROM ");
        Source(w, insert);
        w.Append(" WHERE NOT EXISTS (SELECT 1 FROM ").Table(target).Append(" WITH (UPDLOCK, SERIALIZABLE) WHERE ");
        KeyMatch(w, upsert.ConflictColumns, name);
        w.Append(");");
    }

    private static void Source(SqlWriter w, InsertStatement insert)
    {
        w.Append('(');
        RenderInsertValues(w, insert);
        w.Append(") AS ").Identifier(ExcludedAlias).Append(" (").Identifiers(insert.Columns).Append(')');
    }

    private static void KeyMatch(SqlWriter w, IReadOnlyList<string> keys, string table)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) w.Append(" AND ");
            w.Identifier(table).Append('.').Identifier(keys[i]).Append(" = ").Identifier(ExcludedAlias).Append('.').Identifier(keys[i]);
        }
    }
}
