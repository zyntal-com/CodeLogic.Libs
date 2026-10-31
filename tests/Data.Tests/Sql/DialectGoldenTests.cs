using CL.Data.MySql;
using CL.Data.PostgreSql;
using CL.Data.Sql;
using CL.Data.Sqlite;
using CL.Data.SqlServer;

namespace Data.Tests.Sql;

/// <summary>Golden SQL: the same statements rendered by each dialect.</summary>
public sealed class DialectGoldenTests
{
    private static readonly SqlDialect MySql = new MySqlSqlDialect();
    private static readonly SqlDialect Pg = new PostgreSqlSqlDialect();
    private static readonly SqlDialect Mssql = new SqlServerSqlDialect();
    private static readonly SqlDialect Lite = new SqliteSqlDialect();

    private static ParameterExpr P<T>(T value) => new(SqlParameterValue.Of(value));
    private static ColumnExpr C(string name, string? table = null) => new(name, table);
    private static BinaryExpr B(SqlBinaryOperator op, SqlExpr l, SqlExpr r) => new(op, l, r);

    private static void Golden(SqlStatement statement, string mysql, string pg, string mssql, string sqlite)
    {
        Assert.Equal(mysql, SqlWriter.Render(statement, MySql).Text);
        Assert.Equal(pg, SqlWriter.Render(statement, Pg).Text);
        Assert.Equal(mssql, SqlWriter.Render(statement, Mssql).Text);
        Assert.Equal(sqlite, SqlWriter.Render(statement, Lite).Text);
    }

    private static readonly TableRef Users = new("users");

    [Fact]
    public void Paged_select_with_condition()
    {
        var select = new SelectStatement(Users)
        {
            Where = B(SqlBinaryOperator.And, B(SqlBinaryOperator.Equal, C("Active"), new LiteralExpr(true)), new LikeExpr(C("Name"), P("a%"))),
            OrderBy = [new(C("Id"))],
            Limit = P(10),
            Offset = P(20),
        };
        Golden(select,
            "SELECT * FROM `users` WHERE `Active` = TRUE AND `Name` LIKE @p0 ESCAPE '!' ORDER BY `Id` LIMIT @p1 OFFSET @p2",
            "SELECT * FROM \"users\" WHERE \"Active\" = TRUE AND \"Name\" LIKE @p0 ESCAPE '\\' ORDER BY \"Id\" LIMIT @p1 OFFSET @p2",
            "SELECT * FROM [users] WHERE [Active] = 1 AND [Name] LIKE @p0 ESCAPE '\\' ORDER BY [Id] OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            "SELECT * FROM \"users\" WHERE \"Active\" = TRUE AND \"Name\" LIKE @p0 ESCAPE '\\' ORDER BY \"Id\" LIMIT @p1 OFFSET @p2");
    }

    [Fact]
    public void Limit_only_and_offset_only()
    {
        var top = new SelectStatement(Users) { Limit = P(5) };
        Golden(top,
            "SELECT * FROM `users` LIMIT @p0",
            "SELECT * FROM \"users\" LIMIT @p0",
            "SELECT TOP (@p0) * FROM [users]",
            "SELECT * FROM \"users\" LIMIT @p0");

        var skip = new SelectStatement(Users) { Offset = P(5) };
        Golden(skip,
            "SELECT * FROM `users` LIMIT 18446744073709551615 OFFSET @p0",
            "SELECT * FROM \"users\" OFFSET @p0",
            "SELECT * FROM [users] ORDER BY (SELECT NULL) OFFSET @p0 ROWS",
            "SELECT * FROM \"users\" LIMIT -1 OFFSET @p0");
    }

    [Fact]
    public void Locking_reads()
    {
        var claim = new SelectStatement(new TableRef("jobs")) { Limit = P(1), Lock = new LockClause(LockMode.Update, LockWait.SkipLocked) };
        Golden(claim,
            "SELECT * FROM `jobs` LIMIT @p0 FOR UPDATE SKIP LOCKED",
            "SELECT * FROM \"jobs\" LIMIT @p0 FOR UPDATE SKIP LOCKED",
            "SELECT TOP (@p0) * FROM [jobs] WITH (UPDLOCK, ROWLOCK, READPAST)",
            "SELECT * FROM \"jobs\" LIMIT @p0");
    }

    [Fact]
    public void Boolean_values_in_projections()
    {
        var select = new SelectStatement(Users)
        {
            Projection = [new(B(SqlBinaryOperator.GreaterThan, C("Score"), new LiteralExpr(10)), "Big")],
            Where = new LiteralExpr(true),
        };
        Golden(select,
            "SELECT `Score` > 10 AS `Big` FROM `users` WHERE TRUE",
            "SELECT \"Score\" > 10 AS \"Big\" FROM \"users\" WHERE TRUE",
            "SELECT CASE WHEN [Score] > 10 THEN 1 ELSE 0 END AS [Big] FROM [users] WHERE 1 = 1",
            "SELECT \"Score\" > 10 AS \"Big\" FROM \"users\" WHERE TRUE");
    }

    [Fact]
    public void Functions()
    {
        var select = new SelectStatement(Users)
        {
            Projection =
            [
                new(new FunctionExpr(SqlFunction.Year, [C("At")])),
                new(new FunctionExpr(SqlFunction.DayOfWeek, [C("At")])),
                new(B(SqlBinaryOperator.Concat, C("First"), C("Last"))),
                new(new FunctionExpr(SqlFunction.Greatest, [C("A"), C("B")])),
            ],
        };
        Golden(select,
            "SELECT YEAR(`At`), (DAYOFWEEK(`At`) - 1), CONCAT(`First`, `Last`), GREATEST(`A`, `B`) FROM `users`",
            "SELECT CAST(EXTRACT(YEAR FROM \"At\") AS integer), CAST(EXTRACT(DOW FROM \"At\") AS integer), (\"First\") || (\"Last\"), GREATEST(\"A\", \"B\") FROM \"users\"",
            "SELECT YEAR([At]), ((DATEPART(weekday, [At]) + @@DATEFIRST - 1) % 7), CONCAT([First], [Last]), CASE WHEN [A] >= [B] THEN [A] ELSE [B] END FROM [users]",
            "SELECT CAST(strftime('%Y', \"At\") AS INTEGER), CAST(strftime('%w', \"At\") AS INTEGER), (\"First\") || (\"Last\"), MAX(\"A\", \"B\") FROM \"users\"");
    }

    [Fact]
    public void Insert_with_generated_key()
    {
        var insert = new InsertStatement(Users, ["Name"], [[P("a")]]) { Returning = ["Id"] };
        Assert.Throws<SqlRenderException>(() => SqlWriter.Render(insert, MySql));   // MySQL: LAST_INSERT_ID()
        Assert.Equal("INSERT INTO \"users\" (\"Name\") VALUES (@p0) RETURNING \"Id\"", SqlWriter.Render(insert, Pg).Text);
        Assert.Equal("INSERT INTO [users] ([Name]) OUTPUT INSERTED.[Id] VALUES (@p0)", SqlWriter.Render(insert, Mssql).Text);
        Assert.Equal("INSERT INTO \"users\" (\"Name\") VALUES (@p0) RETURNING \"Id\"", SqlWriter.Render(insert, Lite).Text);
    }

    [Fact]
    public void Limited_delete()
    {
        var delete = new DeleteStatement(new TableRef("logs")) { Where = B(SqlBinaryOperator.LessThan, C("At"), P(0)), Limit = P(500), KeyColumns = ["Id"] };
        Golden(delete,
            "DELETE FROM `logs` WHERE `At` < @p0 LIMIT @p1",
            "DELETE FROM \"logs\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"logs\" WHERE \"At\" < @p0 LIMIT @p1)",
            "DELETE TOP (@p0) FROM [logs] WHERE [At] < @p1",
            "DELETE FROM \"logs\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"logs\" WHERE \"At\" < @p0 LIMIT @p1)");
    }

    [Fact]
    public void Upserts()
    {
        var upsert = new UpsertStatement(new InsertStatement(new TableRef("counters"), ["Key", "Hits"], [[P("k"), P(1)]]), ["Key"])
        {
            UpdateSet = [new("Hits", B(SqlBinaryOperator.Add, C("Hits"), new ExcludedExpr("Hits")))],
        };
        Golden(upsert,
            "INSERT INTO `counters` (`Key`, `Hits`) VALUES (@p0, @p1) AS `excluded` ON DUPLICATE KEY UPDATE `counters`.`Hits` = `counters`.`Hits` + `excluded`.`Hits`",
            "INSERT INTO \"counters\" (\"Key\", \"Hits\") VALUES (@p0, @p1) ON CONFLICT (\"Key\") DO UPDATE SET \"Hits\" = \"Hits\" + EXCLUDED.\"Hits\"",
            "UPDATE [counters] SET [counters].[Hits] = [counters].[Hits] + [excluded].[Hits] FROM [counters] WITH (UPDLOCK, SERIALIZABLE) " +
            "INNER JOIN ( VALUES (@p0, @p1)) AS [excluded] ([Key], [Hits]) ON [counters].[Key] = [excluded].[Key];\n" +
            "INSERT INTO [counters] ([Key], [Hits]) SELECT [excluded].[Key], [excluded].[Hits] FROM ( VALUES (@p2, @p3)) AS [excluded] ([Key], [Hits]) " +
            "WHERE NOT EXISTS (SELECT 1 FROM [counters] WITH (UPDLOCK, SERIALIZABLE) WHERE [counters].[Key] = [excluded].[Key]);",
            "INSERT INTO \"counters\" (\"Key\", \"Hits\") VALUES (@p0, @p1) ON CONFLICT (\"Key\") DO UPDATE SET \"Hits\" = \"Hits\" + EXCLUDED.\"Hits\"");

        var ignore = upsert with { IgnoreOnConflict = true };
        Assert.EndsWith("ON DUPLICATE KEY UPDATE `counters`.`Key` = `counters`.`Key`", SqlWriter.Render(ignore, MySql).Text);
        Assert.StartsWith("INSERT INTO [counters]", SqlWriter.Render(ignore, Mssql).Text);
        Assert.Equal(2, SqlWriter.Render(ignore, Mssql).Parameters.Count);
    }

    [Fact]
    public void Identifier_quoting_escapes_the_closing_quote()
    {
        Assert.Equal("`a``b`", MySql.QuoteIdentifier("a`b"));
        Assert.Equal("[a]]b]", Mssql.QuoteIdentifier("a]b"));
        Assert.Equal("\"a\"\"b\"", Pg.QuoteIdentifier("a\"b"));
        Assert.Equal("100!%", MySql.EscapeLike("100%"));
        Assert.Equal("\\[x]", Mssql.EscapeLike("[x]"));   // only [ opens a LIKE character class in SQL Server
    }
}
