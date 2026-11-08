namespace CL.Data.Sql;

/// <summary>
/// Conservative rules a provider uses to tell whether a raw SQL statement may leave session state on its connection (user
/// variables, session settings, temporary tables, locks, open transactions). Providers that run their pool without a reset on
/// every open (MySQL with <c>ConnectionReset = WhenDirty</c>) reset a connection before it returns to the pool when one of its
/// statements may have changed the session. A statement is session-neutral only when it is a single statement starting with
/// one of <see cref="NeutralStarts"/>, uses no user or system variable (<c>@</c>, other than CL.Data's numbered parameters), no executable comment (<c>/*! */</c>) and none of
/// <see cref="DirtyWords"/> outside literals, quoted identifiers and comments.
/// </summary>
public sealed class SessionSqlRules
{
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _starts;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _dirty;
    private readonly char? _parameterPrefix;

    /// <summary>Creates rules.</summary>
    /// <param name="neutralStarts">First words of statements that leave no session state (<c>SELECT</c>, <c>INSERT</c>, ...).</param>
    /// <param name="dirtyWords">Words that make any statement session-changing (<c>GET_LOCK</c>, <c>TEMPORARY</c>, ...).</param>
    /// <param name="parameterPrefix">
    /// The dialect's parameter prefix when it is <c>@</c> (MySQL, SQL Server): CL.Data's numbered parameters (<c>@p0</c>,
    /// <c>@p1</c>, ...) are then not taken for user variables.
    /// </param>
    public SessionSqlRules(IEnumerable<string> neutralStarts, IEnumerable<string> dirtyWords, char? parameterPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(neutralStarts);
        ArgumentNullException.ThrowIfNull(dirtyWords);
        _parameterPrefix = parameterPrefix;
        NeutralStarts = new HashSet<string>(neutralStarts, StringComparer.OrdinalIgnoreCase);
        DirtyWords = new HashSet<string>(dirtyWords, StringComparer.OrdinalIgnoreCase);
        _starts = ((HashSet<string>)NeutralStarts).GetAlternateLookup<ReadOnlySpan<char>>();
        _dirty = ((HashSet<string>)DirtyWords).GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>First words of statements that leave no session state.</summary>
    public IReadOnlySet<string> NeutralStarts { get; }

    /// <summary>Words that make a statement session-changing wherever they appear.</summary>
    public IReadOnlySet<string> DirtyWords { get; }

    /// <summary>
    /// True unless <paramref name="sql"/> is certainly session-neutral. Unknown or ambiguous text (several statements,
    /// unterminated literals, procedure calls, <c>SET</c>, transaction control) counts as session-changing.
    /// </summary>
    public bool MayChangeSession(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return false;
        var first = true;
        var ended = false;
        var tokens = new SqlTokenizer(sql);
        while (tokens.Next())
        {
            if (tokens.Kind is SqlTokenKind.Invalid or SqlTokenKind.ExecutableComment) return true;
            if (ended) return true;   // a second statement
            switch (tokens.Kind)
            {
                case SqlTokenKind.Semicolon:
                    if (first) return true;
                    ended = true;
                    break;
                case SqlTokenKind.Word when first:
                    if (!_starts.Contains(tokens.Text)) return true;
                    first = false;
                    break;
                case SqlTokenKind.Word:
                    if (_dirty.Contains(tokens.Text) && !tokens.AfterDot) return true;
                    break;
                case SqlTokenKind.Quoted when first:
                    return true;
                case SqlTokenKind.Symbol when first && tokens.Text[0] != '(':
                    return true;
                case SqlTokenKind.Symbol when tokens.Text[0] == '@':
                    if (IsParameter(ref tokens)) break;
                    return true;   // a user or system variable
            }
        }
        return false;
    }

    // A CL.Data parameter: the prefix, 'p', digits, then no further word character. Skips it when it is one.
    private bool IsParameter(ref SqlTokenizer tokens)
    {
        if (_parameterPrefix != '@') return false;
        var rest = tokens.Source[(tokens.Start + 1)..];
        if (rest.Length < 2 || rest[0] != 'p' || !char.IsAsciiDigit(rest[1])) return false;
        var length = 2;
        while (length < rest.Length && char.IsAsciiDigit(rest[length])) length++;
        if (length < rest.Length && (char.IsLetterOrDigit(rest[length]) || rest[length] is '_' or '$' or '.' or '@')) return false;
        tokens.Skip(length);
        return true;
    }
}