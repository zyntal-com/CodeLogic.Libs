using System.Globalization;
using System.Text;

namespace CL.Data.Mapping;

/// <summary>
/// The primary-key value of a row: one value, or several for a composite key (in key order). Integral values
/// are normalized to <see cref="long"/>, so <c>EntityKey.Of(5)</c> equals <c>EntityKey.Of(5L)</c>.
/// Used by key lookups, cursors, cache tags and locking.
/// </summary>
public readonly struct EntityKey : IEquatable<EntityKey>
{
    private readonly object? _single;
    private readonly object?[]? _values;

    private EntityKey(object? single, object?[]? values)
    {
        _single = single;
        _values = values;
    }

    /// <summary>A single-column key.</summary>
    public static EntityKey Of(object? value) => new(Normalize(value), null);

    /// <summary>A key of one or more columns, in key order.</summary>
    public static EntityKey Of(params ReadOnlySpan<object?> values)
    {
        if (values.Length == 1) return Of(values[0]);
        var copy = new object?[values.Length];
        for (var i = 0; i < values.Length; i++) copy[i] = Normalize(values[i]);
        return new(null, copy);
    }

    /// <summary>Number of key values (0 for <c>default</c>).</summary>
    public int Count => _values?.Length ?? (IsDefault ? 0 : 1);

    /// <summary>True for <c>default(EntityKey)</c>.</summary>
    public bool IsDefault => _values is null && _single is null;

    /// <summary>Key value at <paramref name="index"/>.</summary>
    public object? this[int index] => _values is not null
        ? _values[index]
        : index == 0 && !IsDefault ? _single : throw new ArgumentOutOfRangeException(nameof(index));

    /// <inheritdoc />
    public bool Equals(EntityKey other)
    {
        if (Count != other.Count) return false;
        for (var i = 0; i < Count; i++)
            if (!ValueEquals(this[i], other[i])) return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EntityKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < Count; i++) hash.Add(ValueHash(this[i]));
        return hash.ToHashCode();
    }

    /// <summary>Equality.</summary>
    public static bool operator ==(EntityKey left, EntityKey right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(EntityKey left, EntityKey right) => !left.Equals(right);

    /// <summary>Invariant text form (<c>42</c> or <c>(7|abc)</c>), stable across processes; used in cache tags.</summary>
    public override string ToString()
    {
        if (Count == 1) return Format(this[0]);
        var sb = new StringBuilder("(");
        for (var i = 0; i < Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(Format(this[i]));
        }
        return sb.Append(')').ToString();
    }

    private static object? Normalize(object? value) => value switch
    {
        int v => (long)v,
        short v => (long)v,
        byte v => (long)v,
        sbyte v => (long)v,
        uint v => (long)v,
        ushort v => (long)v,
        _ => value,
    };

    private static bool ValueEquals(object? a, object? b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    private static int ValueHash(object? value)
    {
        if (value is not byte[] bytes) return value?.GetHashCode() ?? 0;
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        byte[] b => Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
