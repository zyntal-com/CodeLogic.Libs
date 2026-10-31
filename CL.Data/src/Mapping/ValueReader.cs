using System.Data.Common;
using System.Globalization;
using CL.Data.Mapping.Conversion;

namespace CL.Data.Mapping;

/// <summary>
/// Reads a column as <typeparamref name="T"/>, converting between the representations drivers return
/// (SQLite integers for booleans, bytes or text for GUIDs, <see cref="DateTime"/> for
/// <see cref="DateTimeOffset"/>, ...). When the reader already returns <typeparamref name="T"/> it reads
/// without boxing.
/// </summary>
/// <typeparam name="T">Target type (may be nullable).</typeparam>
public static class ValueReader<T>
{
    private static readonly Type Target = typeof(T);
    private static readonly Type ValueType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
    private static readonly bool AcceptsNull = !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null;

    /// <summary>Reads column <paramref name="ordinal"/> of the current row.</summary>
    public static T Read(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return AcceptsNull
                ? default!
                : throw new InvalidCastException($"Column {reader.GetName(ordinal)} is NULL but {Target.Name} is not nullable.");
        }

        if (ValueConverters.TryGet(ValueType, out var converter))
            return (T)converter.FromStoreObject(ValueConversion.FromDb(reader.GetValue(ordinal), converter.StoreType)!)!;

        if (reader.GetFieldType(ordinal) == ValueType && ValueType == Target)
            return reader.GetFieldValue<T>(ordinal);

        return (T)ValueConversion.FromDb(reader.GetValue(ordinal), ValueType)!;
    }
}

/// <summary>Conversions between driver values and CLR values, shared by every read path.</summary>
public static class ValueConversion
{
    /// <summary>
    /// Converts a non-null driver value to <paramref name="target"/> (a non-nullable type). Date/time values
    /// without a kind are treated as UTC.
    /// </summary>
    public static object? FromDb(object? value, Type target)
    {
        if (value is null || value is DBNull) return null;
        var source = value.GetType();
        if (source == target || target.IsAssignableFrom(source)) return Normalize(value);

        if (target.IsEnum)
        {
            return value is string text
                ? Enum.Parse(target, text, ignoreCase: true)
                : Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture));
        }

        if (target == typeof(Guid))
        {
            return value switch
            {
                byte[] { Length: 16 } bytes => new Guid(bytes),
                string text => Guid.Parse(text),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(DateTimeOffset))
        {
            return value switch
            {
                DateTime dt => new DateTimeOffset(AsUtc(dt)),
                string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                long ticks => new DateTimeOffset(ticks, TimeSpan.Zero),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(DateTime))
        {
            return value switch
            {
                DateTimeOffset dto => dto.UtcDateTime,
                string text => DateTime.Parse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(DateOnly))
        {
            return value switch
            {
                DateTime dt => DateOnly.FromDateTime(dt),
                string text => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(TimeOnly))
        {
            return value switch
            {
                TimeSpan ts => TimeOnly.FromTimeSpan(ts),
                DateTime dt => TimeOnly.FromDateTime(dt),
                string text => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(TimeSpan))
        {
            return value switch
            {
                string text => TimeSpan.Parse(text, CultureInfo.InvariantCulture),
                long ticks => new TimeSpan(ticks),
                TimeOnly t => t.ToTimeSpan(),
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(bool))
        {
            return value switch
            {
                string text => text is "1" || bool.Parse(text),
                IConvertible c => c.ToInt64(CultureInfo.InvariantCulture) != 0,
                _ => throw Unsupported(source, target),
            };
        }

        if (target == typeof(decimal) && value is double or float)
        {
            // .NET 11 converts double → decimal exactly (0.1 → 0.1000000000000000055511151231…); go through the
            // shortest round-trip text instead, which is what the value meant.
            var text = value is float single
                ? single.ToString("R", CultureInfo.InvariantCulture)
                : ((double)value).ToString("R", CultureInfo.InvariantCulture);
            return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        if (target == typeof(string))
        {
            return value switch
            {
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
        }

        if (target == typeof(byte[]) && value is string s)
            return System.Text.Encoding.UTF8.GetBytes(s);

        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

        throw Unsupported(source, target);
    }

    private static object Normalize(object value) => value is DateTime dt ? AsUtc(dt) : value;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static InvalidCastException Unsupported(Type source, Type target) =>
        new($"Cannot convert a database value of type {source.Name} to {target.Name}. Register a value converter for {target.Name}.");
}
