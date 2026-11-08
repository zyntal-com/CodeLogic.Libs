using CL.Data.Sql;

namespace Data.Tests.Sql;

public sealed class SessionSqlRulesTests
{
    private static readonly SessionSqlRules Rules = new(["SELECT", "INSERT", "UPDATE", "DELETE", "CREATE"], ["GET_LOCK", "TEMPORARY"]);

    [Theory]
    [InlineData("")]
    [InlineData("SELECT * FROM t WHERE id = ?")]
    [InlineData("select a from t;")]                              // one terminating semicolon
    [InlineData("/* tag */ SELECT 1")]
    [InlineData("-- why\nSELECT 1")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2")]
    [InlineData("DELETE FROM t WHERE id = 2")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("SELECT 'SET @x = 1; GET_LOCK(''a'', 0)' AS s")]  // inside a literal
    [InlineData("SELECT `temporary`, \"get_lock\" FROM t")]       // quoted identifiers
    [InlineData("SELECT t.temporary FROM t")]                     // a qualified column
    [InlineData("SELECT 'a@b.c' AS mail")]
    public void Neutral(string sql) => Assert.False(Rules.MayChangeSession(sql));

    [Theory]
    [InlineData("SET @x = 1")]
    [InlineData("SET SESSION sql_mode = ''")]
    [InlineData("set autocommit = 0")]
    [InlineData("START TRANSACTION")]
    [InlineData("BEGIN")]
    [InlineData("CALL p()")]
    [InlineData("USE other")]
    [InlineData("LOCK TABLES t WRITE")]
    [InlineData("SELECT @x := 1")]                               // user variable
    [InlineData("SELECT a INTO @v FROM t")]
    [InlineData("SELECT GET_LOCK('a', 0)")]
    [InlineData("CREATE TEMPORARY TABLE x (id INT)")]
    [InlineData("SELECT 1; SELECT 2")]                           // several statements
    [InlineData("SELECT 1; SET @x = 1")]
    [InlineData("SELECT /*!40101 1 */ 2")]                       // MySQL executable comment
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT 1 /* unterminated")]
    [InlineData("'text' SELECT")]
    [InlineData(";")]
    [InlineData("= 1")]
    public void May_change_the_session(string sql) => Assert.True(Rules.MayChangeSession(sql));

    [Fact]
    public void Word_sets_are_case_insensitive()
    {
        var rules = new SessionSqlRules(["select"], ["get_lock"]);
        Assert.False(rules.MayChangeSession("SELECT 1"));
        Assert.True(rules.MayChangeSession("SELECT GET_LOCK('a', 0)"));
        Assert.Contains("SELECT", rules.NeutralStarts);
    }
}
