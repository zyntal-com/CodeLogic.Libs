using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Security;

/// <summary>
/// Provides AES-256-GCM authenticated encryption and decryption utilities with PBKDF2 key derivation.
/// AES-GCM is an AEAD (Authenticated Encryption with Associated Data) cipher that provides both
/// confidentiality and integrity. Unlike AES-CBC, it detects tampering and rejects modified ciphertext.
/// </summary>
/// <remarks>
/// <para><b>Wire format (version 1):</b> <c>version (1 byte, 0x01) + salt (16 bytes) + nonce (12 bytes) +
/// tag (16 bytes) + ciphertext</c>, with the key derived by PBKDF2-SHA256 over <see cref="Iterations"/>
/// (600 000) iterations.</para>
/// <para><b>Legacy format:</b> blobs written before 5.0 have no version byte
/// (<c>salt + nonce + tag + ciphertext</c>) and used <see cref="LegacyIterations"/> (100 000) iterations.
/// Decryption tries the versioned layout first and falls back to the legacy one, so stored data keeps
/// decrypting; re-encrypt it to move it to the new format. Encryption always writes version 1.</para>
/// <para><b>Breaking change from 4.x and earlier (pre-GCM):</b> this class previously used AES-CBC which
/// provides no authentication and is vulnerable to padding-oracle attacks. Data encrypted with that
/// implementation cannot be decrypted with this version.</para>
/// </remarks>
public static class Encryption
{
    private const int KeySize   = 32;  // AES-256
    private const int NonceSize = 12;  // GCM standard nonce
    private const int TagSize   = 16;  // GCM authentication tag
    private const int SaltSize  = 16;
    private const byte FormatVersion = 1;
    private const int HeaderSize = SaltSize + NonceSize + TagSize;

    /// <summary>PBKDF2-SHA256 iterations for new blobs (OWASP guidance; was 100 000 before 5.0).</summary>
    public const int Iterations = 600_000;

    /// <summary>PBKDF2-SHA256 iterations of the unversioned blobs written before 5.0.</summary>
    public const int LegacyIterations = 100_000;

    /// <summary>
    /// Encrypts a plaintext string using AES-256-GCM with a PBKDF2-derived key.
    /// The output is a Base64-encoded string containing the version byte, salt, nonce, authentication tag, and ciphertext.
    /// </summary>
    /// <param name="plainText">The plaintext to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>
    /// A Base64-encoded string containing version (1 byte) + salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="plainText"/> or <paramref name="password"/> is null.</exception>
    public static string EncryptAes(string plainText, string password)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToBase64String(EncryptBytes(Encoding.UTF8.GetBytes(plainText), password));
    }

    /// <summary>
    /// Decrypts a Base64-encoded ciphertext string produced by <see cref="EncryptAes"/> (either format).
    /// </summary>
    /// <param name="cipherText">The Base64-encoded ciphertext. Must not be null.</param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The original plaintext string.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="cipherText"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">
    /// Thrown when the input is not valid Base64, is too short, or fails authentication (wrong password,
    /// corrupted data, or tampered ciphertext).
    /// </exception>
    public static string DecryptAes(string cipherText, string password)
    {
        ArgumentNullException.ThrowIfNull(cipherText);
        ArgumentNullException.ThrowIfNull(password);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cipherText);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("The ciphertext is not valid Base64.", ex);
        }
        return Encoding.UTF8.GetString(DecryptBytes(bytes, password));
    }

    /// <summary>
    /// Encrypts a byte array using AES-256-GCM with a PBKDF2-derived key.
    /// </summary>
    /// <param name="data">The data to encrypt. Must not be null.</param>
    /// <param name="password">The password used to derive the encryption key. Must not be null.</param>
    /// <returns>
    /// A byte array containing version (1 byte) + salt (16 bytes) + nonce (12 bytes) + tag (16 bytes) + ciphertext.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> or <paramref name="password"/> is null.</exception>
    public static byte[] EncryptBytes(byte[] data, string password)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(password);

        var salt  = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key   = DeriveKey(password, salt, Iterations);

        var ciphertext = new byte[data.Length];
        var tag        = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, data, ciphertext, tag);

        var result = new byte[1 + HeaderSize + ciphertext.Length];
        result[0] = FormatVersion;
        Buffer.BlockCopy(salt,       0, result, 1,                                  SaltSize);
        Buffer.BlockCopy(nonce,      0, result, 1 + SaltSize,                       NonceSize);
        Buffer.BlockCopy(tag,        0, result, 1 + SaltSize + NonceSize,           TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, 1 + HeaderSize,                     ciphertext.Length);
        return result;
    }

    /// <summary>
    /// Decrypts a byte array produced by <see cref="EncryptBytes"/>, in the versioned or the legacy format.
    /// AES-GCM verifies the authentication tag before returning plaintext —
    /// any modification to the encrypted data causes a <see cref="CryptographicException"/>.
    /// </summary>
    /// <param name="encryptedData">The encrypted data. Must not be null.</param>
    /// <param name="password">The password used during encryption. Must not be null.</param>
    /// <returns>The decrypted plaintext bytes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="encryptedData"/> or <paramref name="password"/> is null.</exception>
    /// <exception cref="CryptographicException">
    /// Thrown when the data is too short or authentication fails (wrong password, corrupted data, or tampered ciphertext).
    /// </exception>
    public static byte[] DecryptBytes(byte[] encryptedData, string password)
    {
        ArgumentNullException.ThrowIfNull(encryptedData);
        ArgumentNullException.ThrowIfNull(password);

        if (encryptedData.Length < HeaderSize)
            throw new CryptographicException(
                $"Encrypted data is too short. Expected at least {HeaderSize} bytes.");

        // A legacy blob starts with a random salt byte, which can equal the version byte (1 in 256):
        // a versioned parse that fails authentication falls back to the legacy layout.
        if (encryptedData[0] == FormatVersion && encryptedData.Length > HeaderSize)
        {
            try
            {
                return Decrypt(encryptedData.AsSpan(1), password, Iterations);
            }
            catch (CryptographicException) when (encryptedData.Length - 1 >= HeaderSize)
            {
                // fall through to the legacy layout
            }
        }

        return Decrypt(encryptedData, password, LegacyIterations);
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> blob, string password, int iterations)
    {
        var salt       = blob[..SaltSize];
        var nonce      = blob[SaltSize..(SaltSize + NonceSize)];
        var tag        = blob[(SaltSize + NonceSize)..HeaderSize];
        var ciphertext = blob[HeaderSize..];
        var key        = DeriveKey(password, salt, iterations);

        var plaintext = new byte[ciphertext.Length];
        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Generates a cryptographically random Base64-encoded key of the specified byte length.
    /// </summary>
    /// <param name="length">The number of random bytes to generate. Default: 32 (256-bit key).</param>
    /// <returns>A Base64-encoded string representing the random key.</returns>
    public static string GenerateKey(int length = 32)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);
}
