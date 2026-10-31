using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace CL.Common.Hashing;

/// <summary>
/// Fast, non-cryptographic hashing (XXH3 / XXH128) for cache keys, deduplication and change detection.
/// <para>
/// These hashes are <b>not</b> secure: an attacker can construct collisions on purpose. For passwords,
/// signatures or integrity against tampering use <see cref="Security.Hashing"/> instead.
/// </para>
/// Results are stable across processes, machines and versions for the same input and seed.
/// </summary>
public static class FastHash
{
    /// <summary>The 64-bit XXH3 hash of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to hash.</param>
    /// <param name="seed">Optional seed; different seeds give unrelated hashes.</param>
    /// <returns>The hash.</returns>
    public static ulong XxHash3(ReadOnlySpan<byte> data, long seed = 0) =>
        System.IO.Hashing.XxHash3.HashToUInt64(data, seed);

    /// <summary>The 128-bit XXH128 hash of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to hash.</param>
    /// <param name="seed">Optional seed; different seeds give unrelated hashes.</param>
    /// <returns>The hash.</returns>
    public static UInt128 XxHash128(ReadOnlySpan<byte> data, long seed = 0) =>
        System.IO.Hashing.XxHash128.HashToUInt128(data, seed);

    /// <summary>
    /// The 128-bit XXH128 hash of <paramref name="text"/>'s UTF-16 code units (little-endian bytes), without
    /// allocating or transcoding. The same string always hashes the same; it does not equal the hash of the
    /// string's UTF-8 bytes.
    /// </summary>
    /// <param name="text">The characters to hash.</param>
    /// <param name="seed">Optional seed.</param>
    /// <returns>The hash.</returns>
    public static UInt128 XxHash128(ReadOnlySpan<char> text, long seed = 0) =>
        System.IO.Hashing.XxHash128.HashToUInt128(CharBytes(text), seed);

    /// <summary>Formats a 128-bit hash as 32 lowercase hex characters, most significant byte first.</summary>
    /// <param name="hash">The hash.</param>
    /// <returns>The hex string.</returns>
    public static string ToHex(UInt128 hash) =>
        string.Create(32, hash, static (chars, value) =>
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt128BigEndian(bytes, value);
            Convert.TryToHexStringLower(bytes, chars, out _);
        });

    /// <summary>The bytes of <paramref name="text"/>, little-endian on every platform so hashes are portable.</summary>
    internal static ReadOnlySpan<byte> CharBytes(ReadOnlySpan<char> text)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("FastHash text hashing requires a little-endian platform.");
        return MemoryMarshal.AsBytes(text);
    }
}

/// <summary>
/// Incremental XXH128 hashing: append values piece by piece, then read the hash. Appending the same sequence of
/// bytes in any split gives the same hash as <see cref="FastHash.XxHash128(ReadOnlySpan{byte}, long)"/> over the
/// concatenation.
/// <para>
/// Create it with <see cref="Create"/>; a <c>default</c> instance is not usable. It is a mutable struct wrapping a
/// hasher object: don't copy it while appending, and don't share it between threads.
/// </para>
/// </summary>
public struct FastHasher
{
    private XxHash128? _hasher;

    private FastHasher(XxHash128 hasher) => _hasher = hasher;

    /// <summary>Starts a new incremental hash.</summary>
    /// <param name="seed">Optional seed.</param>
    /// <returns>The hasher.</returns>
    public static FastHasher Create(long seed = 0) => new(new XxHash128(seed));

    /// <summary>Appends raw bytes.</summary>
    /// <param name="data">The bytes.</param>
    public readonly void Append(ReadOnlySpan<byte> data) => Hasher.Append(data);

    /// <summary>Appends the UTF-16 code units of <paramref name="text"/> (no allocation, no length prefix).</summary>
    /// <param name="text">The characters.</param>
    public readonly void Append(ReadOnlySpan<char> text) => Hasher.Append(FastHash.CharBytes(text));

    /// <summary>
    /// Appends the in-memory bytes of an unmanaged value (an <see cref="int"/>, <see cref="Guid"/>, enum, or a
    /// struct of such fields). Padding inside custom structs is hashed too, so prefer primitives for portable hashes.
    /// </summary>
    /// <typeparam name="T">An unmanaged type.</typeparam>
    /// <param name="value">The value.</param>
    public readonly void Append<T>(T value) where T : unmanaged =>
        Hasher.Append(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>The hash of everything appended so far. Appending can continue afterwards.</summary>
    /// <returns>The 128-bit hash.</returns>
    public readonly UInt128 GetHash128() => Hasher.GetCurrentHashAsUInt128();

    private readonly XxHash128 Hasher =>
        _hasher ?? throw new InvalidOperationException("Use FastHasher.Create() to start a hash; default(FastHasher) is not usable.");
}
