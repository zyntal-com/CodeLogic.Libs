namespace CL.Data.Sql;

/// <summary>
/// How a dialect splits SQL text into comments, literals and code, for the conservative raw-SQL classifiers
/// (<see cref="SqlIntent.Auto"/>, <see cref="SessionSqlRules"/>). Lexing must match the server exactly: a comment or literal
/// the classifier sees where the server sees code would hide statements from it.
/// </summary>
/// <param name="HashComments"><c>#</c> starts a line comment (MySQL). Elsewhere it is an operator.</param>
/// <param name="DashCommentNeedsSpace">
/// <c>--</c> starts a comment only when whitespace or the end of the text follows (MySQL: <c>1--1</c> is arithmetic).
/// </param>
/// <param name="DollarQuotes"><c>$$...$$</c> and <c>$tag$...$tag$</c> are string literals (PostgreSQL).</param>
/// <param name="BracketIdentifiers"><c>[name]</c> is a quoted identifier (SQL Server, SQLite).</param>
/// <remarks>
/// On every dialect a <c>'...'</c> or <c>"..."</c> literal that contains a backslash is ambiguous (MySQL escapes with it
/// unless <c>NO_BACKSLASH_ESCAPES</c> is set; PostgreSQL does inside <c>E'...'</c>), so the classifiers treat the statement
/// as unknown: a write that may change the session.
/// </remarks>
public sealed record SqlLexicon(bool HashComments, bool DashCommentNeedsSpace, bool DollarQuotes, bool BracketIdentifiers)
{
    /// <summary>Standard SQL as SQL Server and SQLite lex it: <c>--</c> and <c>/* */</c> comments, <c>[name]</c> identifiers.</summary>
    public static SqlLexicon Standard { get; } = new(HashComments: false, DashCommentNeedsSpace: false, DollarQuotes: false, BracketIdentifiers: true);

    /// <summary>MySQL / MariaDB: <c>#</c> comments, <c>-- </c> needs a space, no dollar quotes, no bracket identifiers.</summary>
    public static SqlLexicon MySql { get; } = new(HashComments: true, DashCommentNeedsSpace: true, DollarQuotes: false, BracketIdentifiers: false);

    /// <summary>PostgreSQL: dollar-quoted strings, no <c>#</c> comments, no bracket identifiers.</summary>
    public static SqlLexicon PostgreSql { get; } = new(HashComments: false, DashCommentNeedsSpace: false, DollarQuotes: true, BracketIdentifiers: false);
}
