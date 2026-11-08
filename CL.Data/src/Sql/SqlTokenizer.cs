namespace CL.Data.Sql;

/// <summary>Kind of a <see cref="SqlTokenizer"/> token.</summary>
internal enum SqlTokenKind
{
    /// <summary>A bare word: a letter or underscore, then letters, digits, <c>_</c> or <c>$</c>.</summary>
    Word,

    /// <summary>A string literal, quoted identifier (<c>"</c>, <c>`</c>, <c>[</c>) or PostgreSQL dollar-quoted string.</summary>
    Quoted,

    /// <summary>Any other single character (operators, parentheses, digits, <c>@</c>, ...).</summary>
    Symbol,

    /// <summary>A statement-ending <c>;</c>.</summary>
    Semicolon,

    /// <summary>A MySQL executable comment <c>/*! ... */</c>, whose content the server runs.</summary>
    ExecutableComment,

    /// <summary>An unterminated literal, quoted identifier or comment; nothing follows.</summary>
    Invalid,
}

/// <summary>
/// Splits SQL text into words, literals and symbols, skipping whitespace and comments the way <paramref name="lexicon"/>'s
/// dialect does. Used by the conservative raw-SQL classifiers; it allocates nothing. A <c>'...'</c> or <c>"..."</c> literal
/// containing a backslash is <see cref="SqlTokenKind.Invalid"/>: whether the backslash escapes depends on server settings.
/// </summary>
internal ref struct SqlTokenizer(ReadOnlySpan<char> sql, SqlLexicon lexicon)
{
    private readonly ReadOnlySpan<char> _sql = sql;
    private readonly SqlLexicon _lexicon = lexicon;
    private int _pos;

    /// <summary>Kind of the current token.</summary>
    public SqlTokenKind Kind { get; private set; }

    /// <summary>Text of the current token.</summary>
    public ReadOnlySpan<char> Text { get; private set; }

    /// <summary>Index of the current token in the text.</summary>
    public int Start { get; private set; }

    /// <summary>The whole text being tokenized.</summary>
    public readonly ReadOnlySpan<char> Source => _sql;

    /// <summary>Moves past the next <paramref name="count"/> characters (they were looked at through <see cref="Source"/>).</summary>
    public void Skip(int count) => _pos = Math.Min(_sql.Length, _pos + count);

    /// <summary>True when the current word directly follows a <c>.</c> (a qualified name such as <c>t.update</c>).</summary>
    public bool AfterDot { get; private set; }

    /// <summary>Moves to the next token; false at the end of the text (and after an <see cref="SqlTokenKind.Invalid"/> token).</summary>
    public bool Next()
    {
        var span = _sql;
        var i = _pos;
        while (i < span.Length)
        {
            var c = span[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if ((c == '-' && i + 1 < span.Length && span[i + 1] == '-'
                    && (!_lexicon.DashCommentNeedsSpace || i + 2 >= span.Length || char.IsWhiteSpace(span[i + 2]) || char.IsControl(span[i + 2])))
                || (c == '#' && _lexicon.HashComments))
            {
                // Line comment.
                while (i < span.Length && span[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < span.Length && span[i + 1] == '*')
            {
                var close = span[(i + 2)..].IndexOf("*/");
                if (close < 0) return Emit(SqlTokenKind.Invalid, i, span.Length);
                var end = i + close + 4;
                if (i + 2 < span.Length && span[i + 2] == '!') return Emit(SqlTokenKind.ExecutableComment, i, end);
                i = end;
                continue;
            }
            if (c == ';') return Emit(SqlTokenKind.Semicolon, i, i + 1);
            if (c is '\'' or '"' or '`' || (c == '[' && _lexicon.BracketIdentifiers))
            {
                var closing = c == '[' ? ']' : c;
                var start = i++;
                while (true)
                {
                    if (i >= span.Length) return Emit(SqlTokenKind.Invalid, start, span.Length);
                    if (span[i] == '\\' && c is '\'' or '"') return Emit(SqlTokenKind.Invalid, start, span.Length);
                    if (span[i] == closing)
                    {
                        if (i + 1 < span.Length && span[i + 1] == closing) { i += 2; continue; }   // doubled quote
                        i++;
                        break;
                    }
                    i++;
                }
                return Emit(SqlTokenKind.Quoted, start, i);
            }
            if (_lexicon.DollarQuotes && c == '$' && i + 1 < span.Length && (span[i + 1] == '$' || char.IsLetter(span[i + 1])))
            {
                // PostgreSQL dollar quoting ($$...$$ or $tag$...$tag$); $1 placeholders fall through to a symbol.
                var tagEnd = span[(i + 1)..].IndexOf('$');
                if (tagEnd >= 0)
                {
                    var tag = span.Slice(i, tagEnd + 2);
                    var tagIsWord = true;
                    foreach (var t in tag[1..^1]) tagIsWord &= char.IsLetterOrDigit(t) || t == '_';
                    if (tagIsWord)
                    {
                        var body = span[(i + tag.Length)..].IndexOf(tag);
                        if (body < 0) return Emit(SqlTokenKind.Invalid, i, span.Length);
                        return Emit(SqlTokenKind.Quoted, i, i + tag.Length * 2 + body);
                    }
                }
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] is '_' or '$')) i++;
                return Emit(SqlTokenKind.Word, start, i);
            }
            return Emit(SqlTokenKind.Symbol, i, i + 1);
        }
        _pos = i;
        return false;
    }

    private bool Emit(SqlTokenKind kind, int start, int end)
    {
        Kind = kind;
        Start = start;
        Text = _sql[start..end];
        AfterDot = start > 0 && _sql[start - 1] == '.';
        _pos = kind == SqlTokenKind.Invalid ? _sql.Length : end;
        return true;
    }
}
