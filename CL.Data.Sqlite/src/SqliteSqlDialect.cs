using CL.Data.Sql;

namespace CL.Data.Sqlite;

/// <summary>
/// SQLite dialect:
/// <list type="bullet">
/// <item>ANSI quoting.</item>
/// <item><c>strftime</c> date parts over the UTC ISO-8601 text CL.Data stores.</item>
/// <item>Floor and ceiling built from casts (math functions are not always compiled in).</item>
/// <item>Multi-argument <c>MIN</c>/<c>MAX</c>, and <c>json_extract</c>/<c>json_type</c>.</item>
/// <item>No row locks: transactions take the write lock instead.</item>
/// </list>
/// </summary>
public sealed class SqliteSqlDialect : SqlDialect
{
    /// <inheritdoc />
    public override void RenderPaging(SqlWriter w, SelectStatement select)
    {
        w.Append(" LIMIT ");
        if (select.Limit is not null) w.Expr(select.Limit);
        else w.Append("-1");
        if (select.Offset is not null) w.Append(" OFFSET ").Expr(select.Offset);
    }

    /// <inheritdoc />
    public override void RenderLock(SqlWriter w, LockClause clause) { }   // BEGIN IMMEDIATE transactions serialize writers

    /// <inheritdoc />
    public override void RenderFunction(SqlWriter w, FunctionExpr function)
    {
        var a = function.Arguments;
        switch (function.Function)
        {
            case SqlFunction.Length: Call(w, "LENGTH", a); break;
            case SqlFunction.Substring: Call(w, "SUBSTR", a); break;
            case SqlFunction.Least: Call(w, "MIN", a); break;
            case SqlFunction.Greatest: Call(w, "MAX", a); break;
            case SqlFunction.Floor:
                w.Append("(CAST(").Expr(a[0]).Append(" AS INTEGER) - (").Expr(a[0]).Append(" < CAST(").Expr(a[0]).Append(" AS INTEGER)))");
                break;
            case SqlFunction.Ceiling:
                w.Append("(CAST(").Expr(a[0]).Append(" AS INTEGER) + (").Expr(a[0]).Append(" > CAST(").Expr(a[0]).Append(" AS INTEGER)))");
                break;
            case SqlFunction.Year: Part(w, "%Y", a[0]); break;
            case SqlFunction.Month: Part(w, "%m", a[0]); break;
            case SqlFunction.Day: Part(w, "%d", a[0]); break;
            case SqlFunction.Hour: Part(w, "%H", a[0]); break;
            case SqlFunction.Minute: Part(w, "%M", a[0]); break;
            case SqlFunction.Second: Part(w, "%S", a[0]); break;
            case SqlFunction.DayOfWeek: Part(w, "%w", a[0]); break;
            case SqlFunction.Date: w.Append("date(").Expr(a[0]).Append(')'); break;
            case SqlFunction.BucketUtc:
                w.Append("datetime((CAST(strftime('%s', ").Expr(a[0]).Append(") AS INTEGER) / ").Expr(a[1]).Append(") * ").Expr(a[1])
                 .Append(", 'unixepoch')");
                break;
            case SqlFunction.UtcNow: w.Append("strftime('%Y-%m-%d %H:%M:%f+00:00', 'now')"); break;
            case SqlFunction.JsonValue: Call(w, "json_extract", a); break;
            case SqlFunction.JsonExists: w.Append("(json_type(").Expr(a[0]).Append(", ").Expr(a[1]).Append(") IS NOT NULL)"); break;
            case SqlFunction.JsonContains:
                w.Append("EXISTS (SELECT 1 FROM json_each(").Expr(a[0]).Append(") WHERE json_each.value = ").Expr(a[1]).Append(')');
                break;
            case SqlFunction.FullTextMatch:
                throw new SqlRenderException("SQLite full-text search needs an FTS5 virtual table; query it with raw SQL.");
            default: base.RenderFunction(w, function); break;
        }
    }

    private static void Part(SqlWriter w, string format, SqlExpr value) =>
        w.Append("CAST(strftime('").Append(format).Append("', ").Expr(value).Append(") AS INTEGER)");
}
