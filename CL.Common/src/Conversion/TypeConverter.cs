using System.Globalization;
using CodeLogic.Core.Results;

namespace CL.Common.Conversion;

/// <summary>
/// Provides safe type conversion utilities that return <see cref="Result{T}"/>
/// instead of throwing exceptions.
/// </summary>
public static class TypeConverter
{
    /// <summary>
    /// Attempts to convert <paramref name="value"/> to type <typeparamref name="T"/>.
    /// Supports the types <c>Convert.ChangeType</c> handles (primitives,
    /// <see cref="decimal"/>, <see cref="DateTime"/>, <see cref="string"/>); enums and <c>TypeConverter</c> types are not supported.
    /// </summary>
    public static Result<T> Convert<T>(object? value)
    {
        try
        {
            if (value is null || value is DBNull)
                return Error.Validation(ErrorCode.InvalidArgument, $"Cannot convert null to {typeof(T).Name}");

            if (value is T direct)
                return direct;

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            var converted  = System.Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            return (T)converted;
        }
        catch (Exception ex)
        {
            return Error.Validation(ErrorCode.InvalidArgument,
                $"Cannot convert '{value}' ({value?.GetType().Name}) to {typeof(T).Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to convert <paramref name="value"/> to <typeparamref name="T"/>.
    /// Returns <c>false</c> and sets <paramref name="result"/> to <c>default</c> on failure.
    /// </summary>
    public static bool TryConvert<T>(object? value, out T? result)
    {
        var r = Convert<T>(value);
        result = r.IsSuccess ? r.Value : default;
        return r.IsSuccess;
    }

    /// <summary>Parses a string to <see cref="int"/>.</summary>
    public static Result<int> ToInt(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid integer");
    }

    /// <summary>Parses a string to <see cref="long"/>.</summary>
    public static Result<long> ToLong(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid long");
    }

    /// <summary>Parses a string to <see cref="double"/>.</summary>
    public static Result<double> ToDouble(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid double");
    }

    /// <summary>
    /// Parses a string to <see cref="bool"/>.
    /// Accepts: true/false, yes/no, 1/0, on/off (case-insensitive).
    /// </summary>
    public static Result<bool> ToBool(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true"  or "yes" or "1" or "on"  => true,
            "false" or "no"  or "0" or "off" => false,
            _ => Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid boolean")
        };
    }

    /// <summary>
    /// Parses an ISO 8601 or common date string to <see cref="DateTime"/>.
    /// </summary>
    public static Result<DateTime> ToDateTime(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid date/time");
    }

    /// <summary>
    /// Parses a string to an enum value of type <typeparamref name="T"/>.
    /// Case-insensitive. Also accepts numeric string values.
    /// </summary>
    public static Result<T> ToEnum<T>(string value) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat,
            $"'{value}' is not a valid value for {typeof(T).Name}. Valid values: {string.Join(", ", Enum.GetNames<T>())}");
    }

    /// <summary>Parses a string to a <see cref="Guid"/>.</summary>
    public static Result<Guid> ToGuid(string value)
    {
        if (Guid.TryParse(value, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid GUID");
    }

    /// <summary>Parses a string to a <see cref="decimal"/>.</summary>
    public static Result<decimal> ToDecimal(string value)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            return result;
        return Error.Validation(ErrorCode.InvalidFormat, $"'{value}' is not a valid decimal");
    }
}
