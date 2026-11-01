using System.Security.Cryptography;

namespace CL.Common.Generators;

/// <summary>
/// Provides a variety of ID and token generation strategies for use in applications.
/// All methods are thread-safe and use cryptographically random sources where appropriate.
/// </summary>
public static class IdGenerator
{
    private static readonly char[] AlphanumericChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private static readonly char[] NanoIdChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".ToCharArray();

    private static readonly char[] UrlSafeChars =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".ToCharArray();

    private static long _sequenceCounter = 0;

    /// <summary>
    /// Generates a new <see cref="Guid"/> as a standard formatted string (e.g., <c>"a1b2c3d4-e5f6-..."</c>).
    /// </summary>
    /// <returns>A new uppercase GUID string with dashes.</returns>
    public static string NewGuid() => Guid.NewGuid().ToString();

    /// <summary>
    /// Generates a new <see cref="Guid"/> as a compact 32-character hexadecimal string with no dashes.
    /// </summary>
    /// <returns>A 32-character lowercase hexadecimal string.</returns>
    public static string NewGuidNoDashes() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Generates a sequential integer ID using an atomic counter.
    /// Safe for use within a single process lifetime; resets on restart.
    /// </summary>
    /// <returns>A monotonically increasing <see cref="long"/> value.</returns>
    public static long Sequential() =>
        Interlocked.Increment(ref _sequenceCounter);

    /// <summary>
    /// Generates a timestamp-based ID using the current UTC time as ticks.
    /// </summary>
    /// <returns>A string containing the UTC <see cref="DateTime.Ticks"/> value.</returns>
    public static string Timestamp() =>
        DateTime.UtcNow.Ticks.ToString();

    /// <summary>
    /// Generates a timestamp-based ID with a custom prefix.
    /// Format: <c>"PREFIX_ticks"</c>.
    /// </summary>
    /// <param name="prefix">The prefix to prepend to the timestamp. Must not be null.</param>
    /// <returns>A string in the format <c>"{prefix}_{ticks}"</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="prefix"/> is null.</exception>
    public static string TimestampWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return $"{prefix}_{DateTime.UtcNow.Ticks}";
    }

    /// <summary>
    /// Generates a cryptographically random alphanumeric string of the specified length.
    /// </summary>
    /// <param name="length">The number of characters to generate. Default: 16.</param>
    /// <returns>A random alphanumeric string of the specified length.</returns>
    public static string Random(int length = 16) =>
        GenerateRandom(AlphanumericChars, length);

    /// <summary>
    /// Generates a cryptographically random hexadecimal string of the specified length.
    /// </summary>
    /// <param name="length">The number of hex characters to generate. Default: 32.</param>
    /// <returns>A lowercase hexadecimal string of the specified length.</returns>
    public static string RandomHex(int length = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes((length + 1) / 2);
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }

    /// <summary>
    /// Generates a cryptographically random Base64-encoded string from the specified number of bytes.
    /// </summary>
    /// <param name="byteLength">The number of random bytes before Base64 encoding. Default: 24.</param>
    /// <returns>A Base64-encoded string (length will be approximately <c>byteLength * 4/3</c> characters).</returns>
    public static string RandomBase64(int byteLength = 24) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength));

    /// <summary>
    /// Generates a cryptographically random URL-safe string using Base64url characters (<c>A-Z a-z 0-9 - _</c>).
    /// </summary>
    /// <param name="length">The number of characters to generate. Default: 22.</param>
    /// <returns>A URL-safe random string of the specified length.</returns>
    public static string UrlSafe(int length = 22) =>
        GenerateRandom(UrlSafeChars, length);

    /// <summary>
    /// Generates a NanoID — a compact, URL-safe, cryptographically random identifier.
    /// Uses the NanoID alphabet (<c>A-Z a-z 0-9 _ -</c>).
    /// </summary>
    /// <param name="length">The number of characters to generate. Default: 21.</param>
    /// <returns>A NanoID string of the specified length.</returns>
    public static string NanoId(int length = 21) =>
        GenerateRandom(NanoIdChars, length);

    /// <summary>
    /// Generates a sortable ID combining a UTC timestamp with a random suffix.
    /// IDs generated later will sort lexicographically after earlier ones.
    /// Format: <c>"{ticks:D20}_{randomHex8}"</c>.
    /// </summary>
    /// <returns>A sortable string ID.</returns>
    public static string Sortable()
    {
        var ticks = DateTime.UtcNow.Ticks;
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"{ticks:D20}_{random}";
    }

    private static string GenerateRandom(char[] alphabet, int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        // GetString draws each character uniformly (rejection sampling), so there is no modulo bias.
        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
