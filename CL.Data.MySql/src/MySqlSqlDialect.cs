using CL.Data.Sql;

namespace CL.Data.MySql;

/// <summary>
/// MySQL 8 dialect: backtick identifiers, <c>CONCAT</c> (<c>||</c> means OR in MySQL), row-alias upserts
/// (<c>INSERT ... AS excluded ON DUPLICATE KEY UPDATE</c>), native <c>DELETE ... LIMIT</c>, and <c>!</c> as the
/// LIKE escape so <c>NO_BACKSLASH_ESCAPES</c> cannot change its meaning.
/// </summary>
public sealed class MySqlSqlDialect : SqlDialect
{
    private const string ExcludedAlias = "excluded";

    /// <inheritdoc />
    protected override char OpenQuote => '`';

    /// <inheritdoc />
    protected override char CloseQuote => '`';

    /// <inheritdoc />
    public override char LikeEscape => '!';

    /// <inheritdoc />
    public override void RenderPaging(SqlWriter w, SelectStatement select)
    {
        // MySQL has no OFFSET without LIMIT; the documented idiom is the largest unsigned BIGINT.
        w.Append(" LIMIT ");
        if (select.Limit is not null) w.Expr(select.Limit);
        else w.Append("18446744073709551615");
        if (select.Offset is not null) w.Append(" OFFSET ").Expr(select.Offset);
    }

    /// <inheritdoc />
    public override void RenderConcat(SqlWriter w, SqlExpr left, SqlExpr right) =>
        w.Append("CONCAT(").Expr(left).Append(", ").Expr(right).Append(')');

    /// <inheritdoc />
    public override void RenderFunction(SqlWriter w, FunctionExpr function)
    {
        var a = function.Arguments;
        switch (function.Function)
        {
            case SqlFunction.Year: Call(w, "YEAR", a); break;
            case SqlFunction.Month: Call(w, "MONTH", a); break;
            case SqlFunction.Day: Call(w, "DAY", a); break;
            case SqlFunction.Hour: Call(w, "HOUR", a); break;
            case SqlFunction.Minute: Call(w, "MINUTE", a); break;
            case SqlFunction.Second: Call(w, "SECOND", a); break;
            case SqlFunction.DayOfWeek: w.Append("(DAYOFWEEK(").Expr(a[0]).Append(") - 1)"); break;
            case SqlFunction.Date: Call(w, "DATE", a); break;
            case SqlFunction.BucketUtc:
                w.Append("FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP(").Expr(a[0]).Append(") / ").Expr(a[1]).Append(") * ").Expr(a[1]).Append(')');
                break;
            case SqlFunction.UtcNow: w.Append("UTC_TIMESTAMP(6)"); break;
            case SqlFunction.JsonValue: w.Append("JSON_UNQUOTE(JSON_EXTRACT(").Expr(a[0]).Append(", ").Expr(a[1]).Append("))"); break;
            case SqlFunction.JsonExists: w.Append("JSON_CONTAINS_PATH(").Expr(a[0]).Append(", 'one', ").Expr(a[1]).Append(')'); break;
            case SqlFunction.JsonContains: Call(w, "JSON_CONTAINS", a); break;
            case SqlFunction.FullTextMatch:
                w.Append("MATCH(").Expr(a[0]).Append(") AGAINST (").Expr(a[1]).Append(" IN NATURAL LANGUAGE MODE)");
                break;
            default: base.RenderFunction(w, function); break;
        }
    }

    /// <inheritdoc />
    public override void RenderReturning(SqlWriter w, IReadOnlyList<string> columns) =>
        throw new SqlRenderException("MySQL has no RETURNING; generated keys come from LAST_INSERT_ID().");

    /// <inheritdoc />
    public override void RenderDelete(SqlWriter w, DeleteStatement delete)
    {
        // MySQL rejects a subquery on the table being deleted from, but supports DELETE ... LIMIT directly.
        w.Append("DELETE FROM ").Table(delete.Table, withAlias: false);
        if (delete.Where is not null) w.Append(" WHERE ").Predicate(delete.Where);
        if (delete.Limit is not null) w.Append(" LIMIT ").Expr(delete.Limit);
    }

    /// <inheritdoc />
    public override void RenderExcluded(SqlWriter w, string column) => w.Identifier(ExcludedAlias).Append('.').Identifier(column);

    /// <inheritdoc />
    public override void RenderUpsert(SqlWriter w, UpsertStatement upsert)
    {
        var insert = upsert.Insert;
        w.Append("INSERT INTO ").Table(insert.Table, withAlias: false).Append(" (").Identifiers(insert.Columns).Append(')');
        RenderInsertValues(w, insert);
        w.Append(" AS ").Identifier(ExcludedAlias).Append(" ON DUPLICATE KEY UPDATE ");
        // The row alias has the same column names as the table, so target columns are qualified.
        var table = insert.Table.Name;
        if (upsert.IgnoreOnConflict || upsert.UpdateSet.Count == 0)
        {
            // A no-op assignment ignores only duplicate keys (INSERT IGNORE would also hide truncation and other errors).
            var key = upsert.ConflictColumns.Count > 0 ? upsert.ConflictColumns[0] : insert.Columns[0];
            w.Identifier(table).Append('.').Identifier(key).Append(" = ").Identifier(table).Append('.').Identifier(key);
            return;
        }
        w.List(upsert.UpdateSet, (writer, s) => writer.Identifier(table).Append('.').Identifier(s.Column).Append(" = ")
            .Expr(QualifyColumns(s.Value, table)));
    }
}
