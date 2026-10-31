using CL.Data.Sql;

namespace CL.Data.PostgreSql;

/// <summary>
/// PostgreSQL dialect: ANSI quoting, <c>ON CONFLICT</c> upserts with <c>RETURNING</c>, integer date parts, UTC-correct
/// dates for <c>timestamptz</c>, <c>jsonb</c> path functions and full-text search with <c>websearch_to_tsquery</c>.
/// </summary>
public sealed class PostgreSqlSqlDialect : SqlDialect
{
    /// <inheritdoc />
    public override void RenderFunction(SqlWriter w, FunctionExpr function)
    {
        var a = function.Arguments;
        switch (function.Function)
        {
            case SqlFunction.Year: Part(w, "YEAR", a[0]); break;
            case SqlFunction.Month: Part(w, "MONTH", a[0]); break;
            case SqlFunction.Day: Part(w, "DAY", a[0]); break;
            case SqlFunction.Hour: Part(w, "HOUR", a[0]); break;
            case SqlFunction.Minute: Part(w, "MINUTE", a[0]); break;
            case SqlFunction.Second: Part(w, "SECOND", a[0]); break;
            case SqlFunction.DayOfWeek: Part(w, "DOW", a[0]); break;
            case SqlFunction.Date: w.Append("CAST((").Expr(a[0]).Append(" AT TIME ZONE 'UTC') AS date)"); break;
            case SqlFunction.BucketUtc:
                w.Append("to_timestamp(floor(extract(epoch FROM ").Expr(a[0]).Append(") / ").Expr(a[1]).Append(") * ").Expr(a[1]).Append(')');
                break;
            case SqlFunction.UtcNow: w.Append("now()"); break;
            case SqlFunction.JsonValue:
                w.Append("(jsonb_path_query_first(CAST(").Expr(a[0]).Append(" AS jsonb), CAST(").Expr(a[1]).Append(" AS jsonpath)) #>> '{}')");
                break;
            case SqlFunction.JsonExists:
                w.Append("jsonb_path_exists(CAST(").Expr(a[0]).Append(" AS jsonb), CAST(").Expr(a[1]).Append(" AS jsonpath))");
                break;
            case SqlFunction.JsonContains:
                w.Append("(CAST(").Expr(a[0]).Append(" AS jsonb) @> CAST(").Expr(a[1]).Append(" AS jsonb))");
                break;
            case SqlFunction.FullTextMatch:
                w.Append("(to_tsvector(").Expr(a[0]).Append(") @@ websearch_to_tsquery(").Expr(a[1]).Append("))");
                break;
            default: base.RenderFunction(w, function); break;
        }
    }

    private static void Part(SqlWriter w, string part, SqlExpr value) =>
        w.Append("CAST(EXTRACT(").Append(part).Append(" FROM ").Expr(value).Append(") AS integer)");
}
