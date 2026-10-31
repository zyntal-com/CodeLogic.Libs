using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using CL.Data.Mapping.Conversion;
using CL.Data.Providers;

namespace CL.Data.Sql;

/// <summary>A value bound as a command parameter, kept typed so value types are not boxed.</summary>
public abstract class SqlParameterValue
{
    /// <summary>The value as an object (diagnostics, cache keys).</summary>
    public abstract object? BoxedValue { get; }

    /// <summary>Creates the driver parameter through the provider's type mapper.</summary>
    public abstract DbParameter Create(ITypeMapper types, string name);

    /// <summary>A typed parameter value.</summary>
    public static SqlParameterValue Of<T>(T value) => new Typed<T>(value);

    private sealed class Typed<T>(T value) : SqlParameterValue
    {
        public override object? BoxedValue => value;
        public override DbParameter Create(ITypeMapper types, string name) =>
            value is not null && ValueConverters.TryGet(typeof(T), out var converter)
                ? types.CreateParameter(name, converter.ToStoreObject(value), null)
                : types.CreateParameter(name, value, null);
    }
}

/// <summary>An identifier to splice into raw SQL, quoted by the dialect: <c>{Sql.Identifier(table)}</c>.</summary>
/// <param name="Name">The identifier.</param>
public readonly record struct SqlIdentifier(string Name);

/// <summary>Helpers for raw SQL.</summary>
public static class Sql
{
    /// <summary>An identifier quoted by the dialect when spliced into an interpolated SQL string.</summary>
    public static SqlIdentifier Identifier(string name) => new(name);
}

/// <summary>
/// Raw SQL with its parameters. Build it from an interpolated string: every hole becomes a bound parameter,
/// except <see cref="SqlIdentifier"/> holes, which become quoted identifiers. Values are never spliced into
/// the SQL text.
/// </summary>
/// <example><c>db.SqlQueryAsync&lt;User&gt;($"SELECT * FROM users WHERE email = {email}")</c></example>
[InterpolatedStringHandler]
public struct SqlText
{
    private List<Segment>? _segments;
    private List<SqlParameterValue>? _parameters;

    /// <summary>Creates the handler (called by the compiler).</summary>
    public SqlText(int literalLength, int formattedCount)
    {
        _segments = new List<Segment>(formattedCount * 2 + 1);
        _parameters = formattedCount == 0 ? null : new List<SqlParameterValue>(formattedCount);
    }

    /// <summary>Raw SQL with no parameters.</summary>
    public static SqlText Raw(string sql)
    {
        var text = new SqlText(sql.Length, 0);
        text.AppendLiteral(sql);
        return text;
    }

    /// <summary>Appends literal SQL (called by the compiler).</summary>
    public void AppendLiteral(string value) => (_segments ??= []).Add(new Segment(value, -1, null));

    /// <summary>Appends a value as a parameter (called by the compiler).</summary>
    public void AppendFormatted<T>(T value)
    {
        if (value is SqlIdentifier identifier)
        {
            (_segments ??= []).Add(new Segment(null, -1, identifier.Name));
            return;
        }
        (_parameters ??= []).Add(SqlParameterValue.Of(value));
        (_segments ??= []).Add(new Segment(null, _parameters.Count - 1, null));
    }

    /// <summary>Parameters in order.</summary>
    public readonly IReadOnlyList<SqlParameterValue> Parameters => _parameters ?? [];

    /// <summary>Renders the SQL text for <paramref name="dialect"/>, with placeholders <c>@p0</c>, <c>@p1</c>, ...</summary>
    public readonly string Render(SqlDialect dialect)
    {
        if (_segments is null) return "";
        if (_segments.Count == 1 && _segments[0].Literal is { } only) return only;
        var sb = new StringBuilder();
        foreach (var segment in _segments)
        {
            if (segment.Literal is not null) sb.Append(segment.Literal);
            else if (segment.Identifier is not null) sb.Append(dialect.QuoteIdentifier(segment.Identifier));
            else sb.Append(dialect.ParameterName(segment.Parameter));
        }
        return sb.ToString();
    }

    private readonly record struct Segment(string? Literal, int Parameter, string? Identifier);
}
