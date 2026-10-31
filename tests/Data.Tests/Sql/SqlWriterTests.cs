using CL.Data.Sql;
using Data.Tests.Providers;

namespace Data.Tests.Sql;

public sealed class SqlWriterTests
{
    private static readonly SqlDialect Dialect = new FakeDialect();

    private static ParameterExpr P<T>(T value) => new(SqlParameterValue.Of(value));
    private static ColumnExpr C(string name, string? table = null) => new(name, table);
    private static BinaryExpr B(SqlBinaryOperator op, SqlExpr l, SqlExpr r) => new(op, l, r);

    private static RenderedSql Render(SqlStatement s) => SqlWriter.Render(s, Dialect);
    private static string Text(SqlExpr e) => SqlWriter.RenderExpression(e, Dialect).Text;

    [Fact]
    public void Full_select()
    {
        var select = new SelectStatement(new TableRef("orders", Alias: "o"))
        {
            Projection = [new(C("CustomerId", "o")), new(new AggregateExpr(SqlAggregate.Sum, C("Total", "o")), "Total")],
            Joins = [new SqlJoin(SqlJoinKind.Left, new TableRef("customers", "app", "c"), B(SqlBinaryOperator.Equal, C("Id", "c"), C("CustomerId", "o")))],
            Where = B(SqlBinaryOperator.And,
                B(SqlBinaryOperator.GreaterThan, C("Total", "o"), P(10m)),
                new IsNullExpr(C("DeletedAt", "o"))),
            GroupBy = [C("CustomerId", "o")],
            Having = B(SqlBinaryOperator.GreaterThan, new AggregateExpr(SqlAggregate.Count, null), P(2)),
            OrderBy = [new(C("Total"), Descending: true)],
            Limit = P(50),
            Offset = P(100),
            Lock = new LockClause(LockMode.Update, LockWait.SkipLocked),
        };

        var sql = Render(select);
        Assert.Equal(
            "SELECT \"o\".\"CustomerId\", SUM(\"o\".\"Total\") AS \"Total\" FROM \"orders\" \"o\" " +
            "LEFT JOIN \"app\".\"customers\" \"c\" ON \"c\".\"Id\" = \"o\".\"CustomerId\" " +
            "WHERE \"o\".\"Total\" > @p0 AND \"o\".\"DeletedAt\" IS NULL GROUP BY \"o\".\"CustomerId\" " +
            "HAVING COUNT(*) > @p1 ORDER BY \"Total\" DESC LIMIT @p2 OFFSET @p3 FOR UPDATE SKIP LOCKED",
            sql.Text);
        Assert.Equal([10m, 2, 50, 100], sql.Parameters.Select(p => p.BoxedValue));
    }

    [Fact]
    public void Precedence_adds_only_needed_parentheses()
    {
        var a = C("a"); var b = C("b"); var c = C("c");
        Assert.Equal("\"a\" + \"b\" * \"c\"", Text(B(SqlBinaryOperator.Add, a, B(SqlBinaryOperator.Multiply, b, c))));
        Assert.Equal("(\"a\" + \"b\") * \"c\"", Text(B(SqlBinaryOperator.Multiply, B(SqlBinaryOperator.Add, a, b), c)));
        Assert.Equal("\"a\" - (\"b\" - \"c\")", Text(B(SqlBinaryOperator.Subtract, a, B(SqlBinaryOperator.Subtract, b, c))));
        Assert.Equal("\"a\" - \"b\" - \"c\"", Text(B(SqlBinaryOperator.Subtract, B(SqlBinaryOperator.Subtract, a, b), c)));
        Assert.Equal("(\"a\" = 1 OR \"b\" = 2) AND \"c\" = 3", Text(B(SqlBinaryOperator.And,
            B(SqlBinaryOperator.Or, B(SqlBinaryOperator.Equal, a, new LiteralExpr(1)), B(SqlBinaryOperator.Equal, b, new LiteralExpr(2))),
            B(SqlBinaryOperator.Equal, c, new LiteralExpr(3)))));
        Assert.Equal("NOT (\"a\" = TRUE OR \"b\" IS NOT NULL)", Text(new UnaryExpr(SqlUnaryOperator.Not,
            B(SqlBinaryOperator.Or, B(SqlBinaryOperator.Equal, a, new LiteralExpr(true)), new IsNullExpr(b, true)))));
        Assert.Equal("-(\"a\" + 1.5)", Text(new UnaryExpr(SqlUnaryOperator.Negate, B(SqlBinaryOperator.Add, a, new LiteralExpr(1.5)))));
    }

    [Fact]
    public void In_lists_like_case_and_subqueries()
    {
        Assert.Equal("1 = 0", Text(new InListExpr(C("id"), [])));
        Assert.Equal("1 = 1", Text(new InListExpr(C("id"), [], Negated: true)));
        Assert.Equal("\"id\" NOT IN (@p0, @p1)", Text(new InListExpr(C("id"), [P(1), P(2)], Negated: true)));
        Assert.Equal("\"name\" LIKE @p0 ESCAPE '\\'", Text(new LikeExpr(C("name"), P("a%"))));
        Assert.Equal("CASE WHEN \"a\" > @p0 THEN @p1 ELSE NULL END",
            Text(new CaseExpr([new CaseWhen(B(SqlBinaryOperator.GreaterThan, C("a"), P(1)), P("big"))], NullExpr.Instance)));

        var inner = new SelectStatement(new TableRef("bans")) { Projection = [new(C("UserId"))], Where = B(SqlBinaryOperator.Equal, C("Active"), P(true)) };
        var outer = new SelectStatement(new TableRef("users"))
        {
            Where = B(SqlBinaryOperator.And, B(SqlBinaryOperator.Equal, C("Org"), P(7)), new InSubqueryExpr(C("Id"), inner, Negated: true)),
        };
        var sql = Render(outer);
        Assert.Equal("SELECT * FROM \"users\" WHERE \"Org\" = @p0 AND \"Id\" NOT IN (SELECT \"UserId\" FROM \"bans\" WHERE \"Active\" = @p1)", sql.Text);
        Assert.Equal([7, true], sql.Parameters.Select(p => p.BoxedValue));

        Assert.Equal("NOT EXISTS (SELECT * FROM \"bans\" WHERE \"Active\" = @p0)", Text(new ExistsExpr(inner with { Projection = [] }, Negated: true)));
        Assert.Equal("(\"first\") || (\"last\")", Text(B(SqlBinaryOperator.Concat, C("first"), C("last"))));
        Assert.Equal("EXTRACT(YEAR FROM \"at\")", Text(new FunctionExpr(SqlFunction.Year, [C("at")])));
        Assert.Throws<SqlRenderException>(() => Text(new FunctionExpr(SqlFunction.BucketUtc, [C("at"), new LiteralExpr(60)])));
    }

    [Fact]
    public void Insert_update_delete_upsert()
    {
        var table = new TableRef("users", "app", "u");
        var insert = new InsertStatement(table, ["Name", "Age"], [[P("a"), P(1)], [P("b"), P(2)]]) { Returning = ["Id"] };
        Assert.Equal("INSERT INTO \"app\".\"users\" (\"Name\", \"Age\") VALUES (@p0, @p1), (@p2, @p3) RETURNING \"Id\"", Render(insert).Text);

        var update = new UpdateStatement(table, [new("Age", B(SqlBinaryOperator.Add, C("Age"), new LiteralExpr(1)))])
        {
            Where = B(SqlBinaryOperator.Equal, C("Id"), P(5)),
        };
        Assert.Equal("UPDATE \"app\".\"users\" SET \"Age\" = \"Age\" + 1 WHERE \"Id\" = @p0", Render(update).Text);

        var delete = new DeleteStatement(table) { Where = B(SqlBinaryOperator.LessThan, C("At"), P(0L)), Limit = P(500), KeyColumns = ["Id"] };
        Assert.Equal("DELETE FROM \"app\".\"users\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"app\".\"users\" WHERE \"At\" < @p0 LIMIT @p1)", Render(delete).Text);

        var upsert = new UpsertStatement(new InsertStatement(table, ["Id", "Hits"], [[P(1), P(1)]]), ["Id"])
        {
            UpdateSet = [new("Hits", B(SqlBinaryOperator.Add, C("Hits"), new ExcludedExpr("Hits")))],
        };
        Assert.Equal("INSERT INTO \"app\".\"users\" (\"Id\", \"Hits\") VALUES (@p0, @p1) ON CONFLICT (\"Id\") DO UPDATE SET \"Hits\" = \"Hits\" + EXCLUDED.\"Hits\"",
            Render(upsert).Text);
        Assert.EndsWith("DO NOTHING", Render(upsert with { IgnoreOnConflict = true }).Text);
    }

    [Fact]
    public void Literals_never_accept_text()
    {
        Assert.Throws<SqlRenderException>(() => Text(new LiteralExpr("'; DROP TABLE users; --")));
        Assert.Equal("1.25", Text(new LiteralExpr(1.25m)));
    }
}
