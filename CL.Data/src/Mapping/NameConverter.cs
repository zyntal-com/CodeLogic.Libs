using System.Buffers;

namespace CL.Data.Mapping;

/// <summary>Converts C# names to database names for a <see cref="NamingPolicy"/>.</summary>
public static class NameConverter
{
    /// <summary>Converts <paramref name="name"/> with <paramref name="policy"/>.</summary>
    public static string Convert(string name, NamingPolicy policy) => policy switch
    {
        NamingPolicy.AsIs => name,
        NamingPolicy.SnakeCase => ToSnakeCase(name),
        NamingPolicy.CamelCase => ToCamelCase(name),
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    /// <summary>
    /// Lower snake case that keeps acronyms together: <c>UserId</c> → <c>user_id</c>,
    /// <c>HTTPServerURL</c> → <c>http_server_url</c>, <c>Address2Line</c> → <c>address2_line</c>.
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        char[]? rented = null;
        Span<char> buffer = name.Length * 2 <= 256
            ? stackalloc char[name.Length * 2]
            : (rented = ArrayPool<char>.Shared.Rent(name.Length * 2));
        var length = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                // Word boundary: lower/digit → Upper ("userId"), or end of an acronym ("HTTPServer": P|S).
                var boundary = i > 0 && previous != '_' &&
                               (char.IsLower(previous) || char.IsDigit(previous) ||
                                (char.IsUpper(previous) && char.IsLower(next)));
                if (boundary) buffer[length++] = '_';
                buffer[length++] = char.ToLowerInvariant(c);
            }
            else
            {
                buffer[length++] = c;
            }
        }

        var result = new string(buffer[..length]);
        if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        return result;
    }

    /// <summary>Camel case: <c>CreatedAt</c> → <c>createdAt</c>, <c>ID</c> → <c>id</c>.</summary>
    public static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0])) return name;
        return string.Create(name.Length, name, static (span, source) =>
        {
            source.AsSpan().CopyTo(span);
            // Lower the leading run of capitals, except the last one when it starts a word ("IDValue" → "idValue").
            for (var i = 0; i < span.Length && char.IsUpper(span[i]); i++)
            {
                if (i > 0 && i + 1 < span.Length && char.IsLower(span[i + 1])) break;
                span[i] = char.ToLowerInvariant(span[i]);
            }
        });
    }
}
