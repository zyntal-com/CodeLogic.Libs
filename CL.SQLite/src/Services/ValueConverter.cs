using System.Globalization;

namespace CL.SQLite.Services;

/// <summary>
/// Converts CLR values to and from the representations CL.SQLite stores in SQLite.
/// Shared by <see cref="Repository{T}"/> and <see cref="QueryBuilder{T}"/> so both
/// directions stay symmetrical.
/// </summary>
internal static class SQLiteValueConverter
{
    /// <summary>Format used for <see cref="DateTime"/> columns. Sortable as text.</summary>
    internal const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Format used for <see cref="DateTimeOffset"/> columns — the DateTime format plus the offset.</summary>
    internal const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss.fffzzz";

    /// <summary>Converts a CLR value to the value bound to a SQLite parameter.</summary>
    public static object? ToDbValue(object? value, Type type)
    {
        if (value is null) return null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool)) return (bool)value ? 1 : 0;
        if (underlying == typeof(DateTime)) return ((DateTime)value).ToString(DateTimeFormat);
        if (underlying == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString(DateTimeOffsetFormat);
        if (underlying == typeof(Guid)) return value.ToString();
        if (underlying.IsEnum) return Convert.ToInt64(value);

        return value;
    }

    /// <summary>
    /// SQLite returns NUMERIC/REAL values as <see cref="double"/>. Since .NET 11 a double → decimal cast is
    /// exact (123.45 becomes 123.45000000000000284217094304), so go through the shortest round-trip string
    /// instead: that yields the value that was written (123.45), as .NET 10 and earlier did.
    /// </summary>
    internal static decimal ToDecimal(object value) => value switch
    {
        double d => decimal.Parse(d.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        float f => decimal.Parse(f.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Converts a value read from SQLite to the CLR type of the target property.</summary>
    public static object? FromDbValue(object? value, Type targetType)
    {
        if (value is null || value is DBNull) return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(bool)) return Convert.ToInt64(value) != 0;
        if (underlying == typeof(int)) return Convert.ToInt32(value);
        if (underlying == typeof(long)) return Convert.ToInt64(value);
        if (underlying == typeof(double)) return Convert.ToDouble(value);
        if (underlying == typeof(float)) return Convert.ToSingle(value);
        if (underlying == typeof(decimal)) return ToDecimal(value);
        if (underlying == typeof(DateTime) && value is string dtStr)
            return DateTime.Parse(dtStr);
        if (underlying == typeof(DateTimeOffset) && value is string dtoStr)
            return ParseDateTimeOffset(dtoStr);
        if (underlying == typeof(Guid) && value is string guidStr)
            return Guid.Parse(guidStr);
        if (underlying.IsEnum)
            return Enum.ToObject(underlying, Convert.ToInt64(value));

        try { return Convert.ChangeType(value, underlying); }
        catch { return value; }
    }

    /// <summary>
    /// Parses the <c>yyyy-MM-dd HH:mm:ss.fffzzz</c> form written by <see cref="ToDbValue"/>,
    /// falling back to a general parse for values written by something else.
    /// </summary>
    private static DateTimeOffset ParseDateTimeOffset(string text) =>
        DateTimeOffset.TryParseExact(
            text, DateTimeOffsetFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var exact)
            ? exact
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
}
