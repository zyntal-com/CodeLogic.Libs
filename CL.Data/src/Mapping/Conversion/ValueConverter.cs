using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CL.Data.Mapping.Conversion;

/// <summary>
/// Converts between a model type and a type the database stores. Used everywhere a value crosses the
/// boundary: reading rows, binding parameters (queries, writes, raw SQL), bulk copy and cache keys.
/// </summary>
public abstract class ValueConverter
{
    /// <summary>The CLR type in the model.</summary>
    public abstract Type ModelType { get; }

    /// <summary>The CLR type sent to and read from the driver.</summary>
    public abstract Type StoreType { get; }

    /// <summary>Neutral column type of the stored value.</summary>
    public virtual DataType StoreDataType => DataTypeInference.Infer(StoreType);

    /// <summary>Converts a model value (never null) to its stored form.</summary>
    public abstract object? ToStoreObject(object value);

    /// <summary>Converts a stored value (never null) to the model form.</summary>
    public abstract object? FromStoreObject(object value);
}

/// <summary>A typed converter between <typeparamref name="TModel"/> and <typeparamref name="TStore"/>.</summary>
public abstract class ValueConverter<TModel, TStore> : ValueConverter
{
    /// <inheritdoc />
    public sealed override Type ModelType => typeof(TModel);

    /// <inheritdoc />
    public sealed override Type StoreType => typeof(TStore);

    /// <summary>Model → store.</summary>
    public abstract TStore ToStore(TModel value);

    /// <summary>Store → model.</summary>
    public abstract TModel FromStore(TStore value);

    /// <inheritdoc />
    public sealed override object? ToStoreObject(object value) => ToStore((TModel)value);

    /// <inheritdoc />
    public sealed override object? FromStoreObject(object value) =>
        FromStore(value is TStore typed ? typed : (TStore)ValueConversion.FromDb(value, typeof(TStore))!);
}

/// <summary>
/// A value type stored as one primitive, typically a strongly-typed id. Implementing it is enough: CL.Data
/// binds and reads the type through <see cref="ToStore"/> and <see cref="FromStore"/> without reflection.
/// </summary>
/// <example>
/// <code>
/// public readonly record struct UserId(long Value) : IDataValue&lt;UserId, long&gt;
/// {
///     public static UserId FromStore(long value) => new(value);
///     public long ToStore() => Value;
/// }
/// </code>
/// </example>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <typeparam name="TStore">The stored primitive.</typeparam>
public interface IDataValue<TSelf, TStore> where TSelf : IDataValue<TSelf, TStore>
{
    /// <summary>Creates the value from its stored form.</summary>
    static abstract TSelf FromStore(TStore value);

    /// <summary>The stored form.</summary>
    TStore ToStore();
}

/// <summary>Converter for an <see cref="IDataValue{TSelf, TStore}"/> type.</summary>
public sealed class DataValueConverter<TSelf, TStore> : ValueConverter<TSelf, TStore> where TSelf : IDataValue<TSelf, TStore>
{
    /// <inheritdoc />
    public override TStore ToStore(TSelf value) => value.ToStore();

    /// <inheritdoc />
    public override TSelf FromStore(TStore value) => TSelf.FromStore(value);
}

/// <summary>Stores an enum as its name instead of its number.</summary>
public sealed class EnumToStringConverter<TEnum> : ValueConverter<TEnum, string> where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override string ToStore(TEnum value) => value.ToString();

    /// <inheritdoc />
    public override TEnum FromStore(string value) => Enum.Parse<TEnum>(value, ignoreCase: true);

    /// <inheritdoc />
    public override DataType StoreDataType => DataType.String;
}

/// <summary>Stores a value as a JSON document, serialized with a source-generated <see cref="JsonTypeInfo{T}"/> (AOT-safe).</summary>
/// <param name="typeInfo">Serialization metadata, e.g. <c>MyJsonContext.Default.Settings</c>.</param>
public sealed class JsonValueConverter<T>(JsonTypeInfo<T> typeInfo) : ValueConverter<T, string>
{
    /// <inheritdoc />
    public override string ToStore(T value) => JsonSerializer.Serialize(value, typeInfo);

    /// <inheritdoc />
    public override T FromStore(string value) => JsonSerializer.Deserialize(value, typeInfo)!;

    /// <inheritdoc />
    public override DataType StoreDataType => DataType.Json;
}

/// <summary>
/// The converters known for model types. Built-in conversions (numbers, text, GUIDs, dates) need no converter;
/// register one for typed ids, enums stored as text, JSON documents and other custom types. A converter set on
/// a column with <c>[Column(Converter = ...)]</c> wins over a registered one.
/// </summary>
public static class ValueConverters
{
    private static readonly ConcurrentDictionary<Type, ValueConverter> Converters = new();

    /// <summary>Incremented on every registration, so caches can notice changes.</summary>
    public static int Version => Volatile.Read(ref _version);

    private static int _version;

    /// <summary>Registers <paramref name="converter"/> for its model type, replacing an earlier one.</summary>
    public static void Register(ValueConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        Converters[converter.ModelType] = converter;
        Interlocked.Increment(ref _version);
    }

    /// <summary>Registers the converter of a typed id or other <see cref="IDataValue{TSelf, TStore}"/>.</summary>
    public static void RegisterDataValue<TSelf, TStore>() where TSelf : IDataValue<TSelf, TStore> =>
        Register(new DataValueConverter<TSelf, TStore>());

    /// <summary>Stores <typeparamref name="TEnum"/> by name everywhere.</summary>
    public static void RegisterEnumAsString<TEnum>() where TEnum : struct, Enum => Register(new EnumToStringConverter<TEnum>());

    /// <summary>Stores <typeparamref name="T"/> as JSON everywhere.</summary>
    public static void RegisterJson<T>(JsonTypeInfo<T> typeInfo) => Register(new JsonValueConverter<T>(typeInfo));

    /// <summary>The converter for <paramref name="modelType"/> (nullable types are unwrapped), if any.</summary>
    public static bool TryGet(Type modelType, [NotNullWhen(true)] out ValueConverter? converter) =>
        Converters.TryGetValue(Nullable.GetUnderlyingType(modelType) ?? modelType, out converter);

    /// <summary>Removes the converter for <paramref name="modelType"/> (tests).</summary>
    public static bool Unregister(Type modelType)
    {
        var removed = Converters.TryRemove(Nullable.GetUnderlyingType(modelType) ?? modelType, out _);
        if (removed) Interlocked.Increment(ref _version);
        return removed;
    }
}
