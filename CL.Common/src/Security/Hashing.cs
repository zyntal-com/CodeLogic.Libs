using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Security;

/// <summary>
/// Provides hashing utilities including SHA-256, SHA-512, MD5 (obsolete),
/// PBKDF2 password hashing, salt generation, and HMAC variants.
/// All methods are thread-safe and stateless.
/// </summary>
public static class Hashing
{
    /// <summary>
    /// Computes the SHA-256 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the SHA-256 hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
    public static string Sha256(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the SHA-512 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the SHA-512 hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> is null.</exception>
    public static string Sha512(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the MD5 hash of the given UTF-8 string.
    /// </summary>
    /// <param name="input">The string to hash. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string representing the MD5 hash.</returns>
    /// <remarks>
    /// MD5 is cryptographically broken and should not be used for security-sensitive purposes.
    /// Use <see cref="Sha256"/> or <see cref="Sha512"/> instead.
    /// </remarks>
    [Obsolete("MD5 is cryptographically broken. Use Sha256 or Sha512 instead.")]
    public static string Md5(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Default PBKDF2-SHA256 iteration count for new password hashes: 600 000, the current OWASP
    /// recommendation. Stored in each hash, so raising it later never invalidates existing hashes.
    /// </summary>
    public const int DefaultPasswordIterations = 600_000;

    /// <summary>
    /// Iteration count assumed for legacy hashes (plain Base64 salt + hash, written by CL.Common before 5.0)
    /// when <see cref="VerifyPassword"/> is called without an explicit count: 100 000, the old default.
    /// </summary>
    public const int LegacyPasswordIterations = 100_000;

    /// <summary>Prefix of the self-describing password hash format produced by <see cref="HashPassword"/>.</summary>
    public const string PasswordHashPrefix = "$pbkdf2-sha256$v1$";

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MaxStoredIterations = 100_000_000; // refuse absurd counts read from storage (DoS guard)

    /// <summary>
    /// Hashes a password using PBKDF2-SHA256 with a random 16-byte salt and returns a self-describing string
    /// <c>$pbkdf2-sha256$v1${iterations}${base64 salt}${base64 hash}</c>. The iteration count travels with the
    /// hash, so <see cref="VerifyPassword"/> needs no extra parameter and the default can be raised later.
    /// </summary>
    /// <param name="password">The plaintext password to hash. Must not be null.</param>
    /// <param name="iterations">The number of PBKDF2 iterations. Default: <see cref="DefaultPasswordIterations"/> (600 000).</param>
    /// <returns>The self-describing hash string, suitable for storage in a database.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="password"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="iterations"/> is less than 1.</exception>
    public static string HashPassword(string password, int iterations = DefaultPasswordIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxStoredIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return $"{PasswordHashPrefix}{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifies a plaintext password against a hash produced by <see cref="HashPassword"/>.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    /// <remarks>
    /// Self-describing hashes (<see cref="PasswordHashPrefix"/>) carry their own iteration count and
    /// <paramref name="iterations"/> is ignored. Legacy hashes (plain Base64 of salt + hash, from releases
    /// before 5.0) are verified with <paramref name="iterations"/>; call <see cref="NeedsRehash"/> after a
    /// successful login and store a fresh <see cref="HashPassword"/> result when it returns true.
    /// </remarks>
    /// <param name="password">The plaintext password to verify. Must not be null.</param>
    /// <param name="hashedPassword">The stored hash. Must not be null.</param>
    /// <param name="iterations">Iterations for legacy hashes only. Default: <see cref="LegacyPasswordIterations"/> (100 000).</param>
    /// <returns><c>true</c> if the password matches the hash; otherwise <c>false</c> (including malformed hashes).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="password"/> or <paramref name="hashedPassword"/> is null.</exception>
    public static bool VerifyPassword(string password, string hashedPassword, int iterations = LegacyPasswordIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(hashedPassword);

        if (!TryParsePasswordHash(hashedPassword, iterations, out var storedIterations, out var salt, out var storedHash, out _))
            return false;

        var computedHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            storedIterations,
            HashAlgorithmName.SHA256,
            storedHash.Length);

        return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
    }

    /// <summary>
    /// Returns true when a stored hash should be replaced with a fresh <see cref="HashPassword"/> result:
    /// it is in the legacy format, uses fewer than <paramref name="iterations"/> iterations, or cannot be parsed.
    /// Call it after a successful <see cref="VerifyPassword"/>, while the plaintext is at hand.
    /// </summary>
    /// <param name="hashedPassword">The stored hash. Must not be null.</param>
    /// <param name="iterations">The iteration count new hashes should have. Default: <see cref="DefaultPasswordIterations"/>.</param>
    public static bool NeedsRehash(string hashedPassword, int iterations = DefaultPasswordIterations)
    {
        ArgumentNullException.ThrowIfNull(hashedPassword);
        if (!TryParsePasswordHash(hashedPassword, LegacyPasswordIterations, out var storedIterations, out _, out _, out var legacy))
            return true;
        return legacy || storedIterations < iterations;
    }

    private static bool TryParsePasswordHash(string stored, int legacyIterations, out int iterations,
        out byte[] salt, out byte[] hash, out bool legacy)
    {
        iterations = 0;
        salt = [];
        hash = [];
        legacy = false;
        try
        {
            if (stored.StartsWith(PasswordHashPrefix, StringComparison.Ordinal))
            {
                var parts = stored[PasswordHashPrefix.Length..].Split('$');
                if (parts.Length != 3) return false;
                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out iterations)) return false;
                if (iterations < 1 || iterations > MaxStoredIterations) return false;
                salt = Convert.FromBase64String(parts[1]);
                hash = Convert.FromBase64String(parts[2]);
                return salt.Length >= SaltSize && hash.Length >= HashSize;
            }

            // Legacy: Base64(16-byte salt + 32-byte hash), iteration count supplied by the caller.
            if (stored.StartsWith('$')) return false;
            var decoded = Convert.FromBase64String(stored);
            if (decoded.Length != SaltSize + HashSize) return false;
            if (legacyIterations < 1 || legacyIterations > MaxStoredIterations) return false;
            salt = decoded[..SaltSize];
            hash = decoded[SaltSize..];
            iterations = legacyIterations;
            legacy = true;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a cryptographically random salt encoded as Base64.
    /// </summary>
    /// <param name="size">The number of random bytes to generate. Default: 16.</param>
    /// <returns>A Base64-encoded string of <paramref name="size"/> random bytes.</returns>
    public static string GenerateSalt(int size = 16)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(size));
    }

    /// <summary>
    /// Computes an HMAC-SHA256 of the given input using the specified key.
    /// </summary>
    /// <param name="input">The message to authenticate. Must not be null.</param>
    /// <param name="key">The secret key. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string of the HMAC-SHA256 result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="key"/> is null.</exception>
    public static string HmacSha256(string input, string key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Computes an HMAC-SHA512 of the given input using the specified key.
    /// </summary>
    /// <param name="input">The message to authenticate. Must not be null.</param>
    /// <param name="key">The secret key. Must not be null.</param>
    /// <returns>A lowercase hexadecimal string of the HMAC-SHA512 result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="input"/> or <paramref name="key"/> is null.</exception>
    public static string HmacSha512(string input, string key)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(key);
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(key));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
