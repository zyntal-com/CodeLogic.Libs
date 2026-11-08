using CL.Data.Sql;

namespace Data.Tests.Sql;

public sealed class SqlClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("  select a, b from t where x = @p0;  ")]
    [InlineData("/* tag */ SELECT 1")]
    [InlineData("-- why\nSELECT 1")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("SELECT 'insert into t' AS s")]                 // keywords inside literals do not count
    [InlineData("SELECT \"update\" FROM t")]                    // nor inside quoted identifiers
    [InlineData("SELECT [delete], `into` FROM t")]
    [InlineData("SELECT t.update FROM t")]                      // a qualified column, not the keyword
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("VALUES (1), (2)")]
    [InlineData("SHOW TABLES")]
    [InlineData("SELECT $$insert$$")]
    [InlineData("SELECT updated_at, created FROM t")]
    public void Plain_reads(string sql) => Assert.True(SqlClassifier.IsPlainRead(sql));

    [Theory]
    [InlineData("")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("INSERT INTO t (v) VALUES (1) RETURNING id")]
    [InlineData("UPDATE t SET v = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("MERGE t USING s ON 1 = 1 WHEN MATCHED THEN DELETE;")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("SELECT * INTO copy FROM t")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("SELECT * FROM t FOR SHARE")]
    [InlineData("SELECT * FROM t LOCK IN SHARE MODE")]
    [InlineData("SELECT 1; DELETE FROM t")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("EXEC sp_who")]
    [InlineData("CALL p()")]
    [InlineData("VACUUM")]
    [InlineData("OPTIMIZE TABLE t")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT 1 /* unterminated")]
    [InlineData("PRAGMA user_version = 3")]
    public void Not_plain_reads(string sql) => Assert.False(SqlClassifier.IsPlainRead(sql));

    // Lexing follows the dialect: code the server runs must never look like a comment or a literal.
    [Theory]
    [InlineData("SELECT 1--1, 2; DELETE FROM t", "mysql", false)]          // MySQL: --1 is arithmetic, not a comment
    [InlineData("SELECT 1 -- note; DELETE FROM t", "mysql", true)]         // -- plus a space is a comment
    [InlineData("SELECT 1--note; DELETE FROM t", "standard", true)]        // elsewhere -- always starts a comment
    [InlineData("SELECT 1 # x; DELETE FROM t", "mysql", true)]             // MySQL # comment
    [InlineData("SELECT 1 # 2; DELETE FROM t", "postgresql", false)]       // PostgreSQL: # is an operator
    [InlineData("SELECT $x$, 1; DELETE FROM t; SELECT $x$", "mysql", false)]       // MySQL: $x$ is an identifier
    [InlineData("SELECT $x$, 1; DELETE FROM t; SELECT $x$", "postgresql", true)]   // PostgreSQL: one dollar-quoted string
    [InlineData("SELECT $$insert$$", "postgresql", true)]
    [InlineData("SELECT [x; DELETE FROM t]", "mysql", false)]              // MySQL has no [identifiers]
    [InlineData("SELECT [x; DELETE FROM t]", "standard", true)]
    public void Lexing_follows_the_dialect(string sql, string dialect, bool plainRead) =>
        Assert.Equal(plainRead, SqlClassifier.IsPlainRead(sql, dialect switch
        {
            "mysql" => SqlLexicon.MySql,
            "postgresql" => SqlLexicon.PostgreSql,
            _ => SqlLexicon.Standard,
        }));

    [Theory]
    [InlineData(@"SELECT 'a\'', 1; DELETE FROM t; SELECT ''")]   // MySQL ends the first string after the escaped quote
    [InlineData(@"SELECT 'C:\path'")]
    [InlineData(@"SELECT ""a\""b""")]
    public void Literals_with_a_backslash_are_never_plain_reads(string sql)
    {
        Assert.False(SqlClassifier.IsPlainRead(sql, SqlLexicon.MySql));
        Assert.False(SqlClassifier.IsPlainRead(sql, SqlLexicon.PostgreSql));
        Assert.False(SqlClassifier.IsPlainRead(sql, SqlLexicon.Standard));
    }

    [Fact]
    public void Explicit_intents_override_the_text()
    {
        Assert.False(SqlClassifier.IsWrite(SqlIntent.Read, "INSERT INTO t VALUES (1)"));
        Assert.False(SqlClassifier.IsWrite(SqlIntent.ReadPrimary, "DELETE FROM t"));
        Assert.True(SqlClassifier.IsWrite(SqlIntent.Write, "SELECT 1"));
        Assert.True(SqlClassifier.IsWrite(SqlIntent.SideEffect, "SELECT pg_notify('c', '')"));
        Assert.True(SqlClassifier.IsWrite(SqlIntent.Auto, "UPDATE t SET v = 1"));
        Assert.False(SqlClassifier.IsWrite(SqlIntent.Auto, "SELECT 1"));

        Assert.True(SqlClassifier.Invalidates(SqlIntent.Write, "SELECT 1"));
        Assert.False(SqlClassifier.Invalidates(SqlIntent.SideEffect, "VACUUM t"));
        Assert.True(SqlClassifier.Invalidates(SqlIntent.Auto, "DELETE FROM t"));
        Assert.False(SqlClassifier.Invalidates(SqlIntent.Read, "DELETE FROM t"));
    }
}
