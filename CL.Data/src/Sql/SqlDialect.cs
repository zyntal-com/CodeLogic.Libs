using System.Text;

namespace CL.Data.Sql;

/// <summary>
/// How a provider writes SQL. CL.Data builds statements as a tree and renders them through the dialect,
/// which overrides only what differs from standard SQL. This base class holds the ANSI defaults; rendering
/// hooks for paging, locking, upserts and functions are added with the SQL tree.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Character that opens a quoted identifier.</summary>
    protected virtual char OpenQuote => '"';

    /// <summary>Character that closes a quoted identifier.</summary>
    protected virtual char CloseQuote => '"';

    /// <summary>Prefix of parameter placeholders.</summary>
    public virtual char ParameterPrefix => '@';

    /// <summary>Escape character used in LIKE patterns.</summary>
    public virtual char LikeEscape => '\\';

    /// <summary>Quotes an identifier, doubling any closing quote inside it.</summary>
    public virtual string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        var close = CloseQuote.ToString();
        return string.Concat(OpenQuote.ToString(), identifier.Replace(close, close + close, StringComparison.Ordinal), close);
    }

    /// <summary>Appends a quoted identifier.</summary>
    public void AppendIdentifier(StringBuilder sb, string identifier) => sb.Append(QuoteIdentifier(identifier));

    /// <summary>Quotes a possibly schema-qualified table name.</summary>
    public virtual string QuoteTable(string table, string? schema) =>
        schema is null ? QuoteIdentifier(table) : QuoteIdentifier(schema) + "." + QuoteIdentifier(table);

    /// <summary>Placeholder for the parameter at <paramref name="index"/> (<c>@p0</c>).</summary>
    public virtual string ParameterName(int index) => string.Create(null, stackalloc char[16], $"{ParameterPrefix}p{index}");

    /// <summary>Escapes LIKE wildcards in <paramref name="value"/> so it matches literally.</summary>
    public virtual string EscapeLike(string value)
    {
        if (value.AsSpan().IndexOfAny(['%', '_', LikeEscape, '[']) < 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c == '%' || c == '_' || c == LikeEscape || (c == '[' && EscapesBracketInLike)) sb.Append(LikeEscape);
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>True when <c>[</c> is a LIKE wildcard (SQL Server).</summary>
    protected virtual bool EscapesBracketInLike => false;
}
