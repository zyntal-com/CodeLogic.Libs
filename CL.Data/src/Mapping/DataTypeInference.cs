namespace CL.Data.Mapping;

/// <summary>Infers the neutral <see cref="DataType"/> of a CLR type.</summary>
public static class DataTypeInference
{
    /// <summary>
    /// The neutral type for <paramref name="clrType"/> (nullable types are unwrapped; enums map to their
    /// integer size). <see cref="DataType.Unspecified"/> when CL.Data has no built-in mapping, in which case
    /// a value converter must supply one.
    /// </summary>
    public static DataType Infer(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type.IsEnum) type = Enum.GetUnderlyingType(type);

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => DataType.Boolean,
            TypeCode.Byte or TypeCode.SByte => DataType.TinyInt,
            TypeCode.Int16 or TypeCode.UInt16 => DataType.SmallInt,
            TypeCode.Int32 or TypeCode.UInt32 => DataType.Int,
            TypeCode.Int64 or TypeCode.UInt64 => DataType.BigInt,
            TypeCode.Decimal => DataType.Decimal,
            TypeCode.Single => DataType.Real,
            TypeCode.Double => DataType.Double,
            TypeCode.String => DataType.String,
            TypeCode.Char => DataType.FixedString,
            TypeCode.DateTime => DataType.DateTime,
            _ when type == typeof(byte[]) => DataType.Blob,
            _ when type == typeof(Guid) => DataType.Guid,
            _ when type == typeof(DateTimeOffset) => DataType.DateTimeOffset,
            _ when type == typeof(DateOnly) => DataType.Date,
            _ when type == typeof(TimeOnly) => DataType.Time,
            _ when type == typeof(TimeSpan) => DataType.Interval,
            _ => DataType.Unspecified,
        };
    }

    /// <summary>True when values of <paramref name="clrType"/> can be bound and read without a value converter.</summary>
    public static bool IsBuiltIn(Type clrType) => Infer(clrType) != DataType.Unspecified;
}
