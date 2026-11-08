namespace CL.Data.Sql;

/// <summary>
/// What a raw SQL statement run through <c>SqlQueryAsync</c>, <c>SqlReadAsync</c>, <c>SqlScalarAsync</c> or
/// <c>DataBatch.Sql</c> does, which decides where it runs, whether it may be retried and what it does to the result cache.
/// </summary>
public enum SqlIntent
{
    /// <summary>
    /// Worked out from the text, conservatively: a single plain <c>SELECT</c>, <c>WITH ... SELECT</c>, <c>VALUES</c> or
    /// <c>SHOW</c> statement is a <see cref="Read"/>; anything else (DML, <c>SELECT ... INTO</c>, <c>FOR UPDATE</c>,
    /// <c>EXEC</c>/<c>CALL</c>, more than one statement, ...) is a <see cref="Write"/>. A <c>SELECT</c> that calls a
    /// function with side effects (<c>nextval</c>, <c>pg_notify</c>, <c>GET_LOCK</c>) cannot be recognized: mark it
    /// <see cref="Write"/> or <see cref="SideEffect"/>.
    /// </summary>
    Auto = 0,

    /// <summary>A read: routed by the read preference (replicas allowed) and retried after transient failures.</summary>
    Read,

    /// <summary>A read that must see the latest data: always the primary, retried after transient failures.</summary>
    ReadPrimary,

    /// <summary>
    /// Changes data: always the primary, retried only when the statement certainly did not run, and every cached result of
    /// the connection is dropped afterwards (raw SQL may change any table).
    /// </summary>
    Write,

    /// <summary>
    /// Has side effects but changes no cached rows (notifications, maintenance such as <c>VACUUM</c>, <c>OPTIMIZE TABLE</c>,
    /// <c>UPDATE STATISTICS</c>): always the primary, retried only when the statement certainly did not run, cache kept.
    /// </summary>
    SideEffect,
}

/// <summary>Conservative classification of raw SQL text (<see cref="SqlIntent.Auto"/>).</summary>
internal static class SqlClassifier
{
    // Whole words that make a SELECT/WITH more than a plain read: data-modifying CTEs (INSERT/UPDATE/DELETE/MERGE),
    // SELECT ... INTO (a new table, variables, OUTFILE) and locking reads (FOR UPDATE, FOR SHARE, LOCK IN SHARE MODE).
    private static readonly HashSet<string> WriteWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "INTO", "SHARE", "LOCK",
    };

    private static readonly HashSet<string> ReadStarts = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "VALUES", "SHOW" };

    // Span lookups: classifying a statement allocates nothing.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> WriteWordLookup = WriteWords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ReadStartLookup = ReadStarts.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>True when <paramref name="intent"/> sends the statement to the primary as a write (no retry once sent).</summary>
    public static bool IsWrite(SqlIntent intent, string sql, SqlLexicon? lexicon = null) => intent switch
    {
        SqlIntent.Read or SqlIntent.ReadPrimary => false,
        SqlIntent.Write or SqlIntent.SideEffect => true,
        _ => !IsPlainRead(sql, lexicon),
    };

    /// <summary>True when the statement is a write that may change cached rows.</summary>
    public static bool Invalidates(SqlIntent intent, string sql, SqlLexicon? lexicon = null) => intent switch
    {
        SqlIntent.Write => true,
        SqlIntent.Auto => !IsPlainRead(sql, lexicon),
        _ => false,
    };

    /// <summary>
    /// True for one plain read statement: it starts with SELECT, WITH, VALUES or SHOW, contains none of the words above outside
    /// literals, quoted identifiers and comments, and nothing follows a terminating semicolon.
    /// </summary>
    /// <param name="sql">The statement.</param>
    /// <param name="lexicon">How the dialect lexes comments and literals (<see cref="SqlDialect.Lexicon"/>); null is standard SQL.</param>
    public static bool IsPlainRead(string sql, SqlLexicon? lexicon = null)
    {
        var first = true;
        var ended = false;
        var tokens = new SqlTokenizer(sql, lexicon ?? SqlLexicon.Standard);
        while (tokens.Next())
        {
            switch (tokens.Kind)
            {
                case SqlTokenKind.ExecutableComment:
                    continue;
                case SqlTokenKind.Invalid:
                    return false;
            }
            if (ended) return false;   // something after the statement's semicolon: a second statement
            switch (tokens.Kind)
            {
                case SqlTokenKind.Semicolon:
                    ended = true;
                    break;
                case SqlTokenKind.Quoted:
                    if (first) return false;
                    break;
                case SqlTokenKind.Word when first:
                    if (!ReadStartLookup.Contains(tokens.Text)) return false;
                    first = false;
                    break;
                case SqlTokenKind.Word:
                    if (WriteWordLookup.Contains(tokens.Text) && !tokens.AfterDot) return false;
                    break;
                default:
                    if (first && tokens.Text[0] != '(') return false;   // (SELECT ...) is fine; anything else first is not a read
                    break;
            }
        }
        return !first;
    }
}
