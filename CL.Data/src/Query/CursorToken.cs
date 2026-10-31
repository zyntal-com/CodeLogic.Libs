using System.Buffers.Text;
using System.Globalization;
using System.Text;
using CL.Data.Errors;
using CodeLogic.Core.Results;

namespace CL.Data.Query;

/// <summary>A page of a keyset (cursor) paged query.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">Rows on this page.</param>
/// <param name="NextCursor">Pass to <c>ToCursorPageAsync(size, after)</c> for the next page; null on the last page.</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>True when there is a next page.</summary>
    public bool HasMore => NextCursor is not null;
}

/// <summary>
/// Opaque cursor: the sort values of the last row plus a fingerprint of the query's ordering, base64url-encoded.
/// A cursor from a differently ordered query is rejected with <c>data.invalid_cursor</c>.
/// </summary>
internal static class CursorToken
{
    private const char Separator = '\u001f';

    public static string Encode(string fingerprint, IReadOnlyList<object?> values)
    {
        var sb = new StringBuilder(fingerprint);
        foreach (var value in values) sb.Append(Separator).Append(EncodeValue(value));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static Result<object?[]> Decode(string token, string fingerprint, IReadOnlyList<Type> types)
    {
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException)
        {
            return DataErrors.InvalidCursor("The cursor is not valid.");
        }

        var parts = text.Split(Separator);
        if (parts[0] != fingerprint || parts.Length - 1 != types.Count)
            return DataErrors.InvalidCursor("The cursor belongs to a query with a different order.");

        var values = new object?[types.Count];
        for (var i = 0; i < types.Count; i++)
        {
            if (DecodeValue(parts[i + 1], Nullable.GetUnderlyingType(types[i]) ?? types[i]) is not { } decoded)
                return DataErrors.InvalidCursor("The cursor is not valid.");
            values[i] = decoded.Value;
        }
        return values;
    }

    private static string EncodeValue(object? value) => value switch
    {
        null => "n",
        string s => "s" + s,
        bool b => b ? "b1" : "b0",
        Guid g => "g" + g.ToString("N"),
        DateTimeOffset dto => "t" + dto.UtcTicks.ToString(CultureInfo.InvariantCulture),
        DateTime dt => "t" + (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).Ticks.ToString(CultureInfo.InvariantCulture),
        Enum e => "i" + Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        IFormattable f => "v" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => "s" + value,
    };

    private static Box? DecodeValue(string part, Type type)
    {
        if (part.Length == 0) return null;
        var body = part[1..];
        try
        {
            object? value = part[0] switch
            {
                'n' => null,
                's' => body,
                'b' => body == "1",
                'g' => Guid.ParseExact(body, "N"),
                't' when type == typeof(DateTime) => new DateTime(long.Parse(body, CultureInfo.InvariantCulture), DateTimeKind.Utc),
                't' => new DateTimeOffset(long.Parse(body, CultureInfo.InvariantCulture), TimeSpan.Zero),
                'i' when type.IsEnum => Enum.ToObject(type, long.Parse(body, CultureInfo.InvariantCulture)),
                'i' or 'v' => Convert.ChangeType(body, type, CultureInfo.InvariantCulture),
                _ => throw new FormatException(),
            };
            return new Box(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private sealed record Box(object? Value);
}
