using System.Security.Cryptography;
using System.Text;

namespace CL.Common.Generators;

/// <summary>
/// Describes the estimated strength of a password based on entropy analysis.
/// </summary>
public enum PasswordStrength
{
    /// <summary>Very weak password — easily guessable (e.g., fewer than 6 characters, no complexity).</summary>
    VeryWeak,

    /// <summary>Weak password — minimal complexity.</summary>
    Weak,

    /// <summary>Medium strength — acceptable for non-critical use.</summary>
    Medium,

    /// <summary>Strong password — suitable for most use cases.</summary>
    Strong,

    /// <summary>Very strong password — high entropy, suitable for sensitive accounts.</summary>
    VeryStrong
}

/// <summary>
/// Generates secure random passwords, passphrases, and PINs.
/// All generation methods use <see cref="RandomNumberGenerator"/> for cryptographic randomness.
/// </summary>
public static class PasswordGenerator
{
    private const string Lowercase  = "abcdefghijklmnopqrstuvwxyz";
    private const string Uppercase  = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits     = "0123456789";
    private const string Special    = "!@#$%^&*()-_=+[]{}|;:,.<>?";

    private static readonly string[] Wordlist =
    [
        "apple", "bridge", "candle", "dragon", "engine", "forest", "garden", "harbor",
        "island", "jungle", "kettle", "lemon", "marble", "needle", "orange", "palace",
        "quartz", "rocket", "silver", "timber", "umbrella", "violet", "walnut", "xenon",
        "yellow", "zephyr", "anchor", "butter", "castle", "dagger", "emerald", "falcon",
        "glacier", "hammer", "ivory", "jasper", "kingdom", "lantern", "mirror", "nectar",
        "opaque", "parrot", "quiver", "raven", "saddle", "thunder", "upward", "valley",
        "winter", "xylem", "yearling", "zigzag", "amber", "barrel", "cobalt", "delight",
        "eclipse", "fable", "goblin", "hollow", "illusion", "jewel", "knapsack", "legend"
    ];

    /// <summary>
    /// Generates a random password of the specified length using a configurable character set.
    /// </summary>
    /// <param name="length">The desired password length. Default: 16.</param>
    /// <param name="includeLowercase">Whether to include lowercase letters. Default: true.</param>
    /// <param name="includeUppercase">Whether to include uppercase letters. Default: true.</param>
    /// <param name="includeDigits">Whether to include digits. Default: true.</param>
    /// <param name="includeSpecial">Whether to include special characters. Default: true.</param>
    /// <returns>A cryptographically random password string.</returns>
    /// <exception cref="ArgumentException">Thrown when no character sets are selected or length is less than 1.</exception>
    public static string Generate(
        int length = 16,
        bool includeLowercase = true,
        bool includeUppercase = true,
        bool includeDigits = true,
        bool includeSpecial = true)
    {
        if (length < 1) throw new ArgumentException("Length must be at least 1.", nameof(length));

        var pool = new StringBuilder();
        if (includeLowercase) pool.Append(Lowercase);
        if (includeUppercase) pool.Append(Uppercase);
        if (includeDigits)    pool.Append(Digits);
        if (includeSpecial)   pool.Append(Special);

        if (pool.Length == 0)
            throw new ArgumentException("At least one character set must be selected.");

        return GenerateFromPool(pool.ToString(), length);
    }

    /// <summary>
    /// Generates a strong password that guarantees at least one character from each specified category.
    /// Minimum length is 12.
    /// </summary>
    /// <param name="length">The desired password length. Must be at least 12. Default: 16.</param>
    /// <returns>A strong password with guaranteed character category coverage.</returns>
    public static string GenerateStrong(int length = 16)
    {
        if (length < 12) length = 12;

        // Ensure at least one of each category
        var required = new List<char>
        {
            PickRandom(Lowercase),
            PickRandom(Uppercase),
            PickRandom(Digits),
            PickRandom(Special)
        };

        var pool = Lowercase + Uppercase + Digits + Special;
        var remaining = GenerateFromPool(pool, length - required.Count).ToCharArray().ToList();
        required.AddRange(remaining);

        // Shuffle
        var array = required.ToArray();
        Shuffle(array);
        return new string(array);
    }

    /// <summary>
    /// Generates a memorable passphrase by combining random words with a separator.
    /// </summary>
    /// <param name="wordCount">The number of words in the passphrase. Default: 4.</param>
    /// <param name="separator">The character or string separating words. Default: "-".</param>
    /// <param name="capitalize">Whether to capitalize the first letter of each word. Default: true.</param>
    /// <returns>A passphrase made up of random dictionary words.</returns>
    public static string GeneratePassphrase(int wordCount = 4, string separator = "-", bool capitalize = true)
    {
        if (wordCount < 2) wordCount = 2;
        var words = new string[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            var word = Wordlist[RandomNumberGenerator.GetInt32(Wordlist.Length)];
            words[i] = capitalize
                ? char.ToUpperInvariant(word[0]) + word[1..]
                : word;
        }
        return string.Join(separator, words);
    }

    /// <summary>
    /// Generates a numeric PIN of the specified length.
    /// </summary>
    /// <param name="length">The number of digits in the PIN. Default: 6.</param>
    /// <returns>A string of random digits.</returns>
    public static string GeneratePin(int length = 6)
    {
        if (length < 1) throw new ArgumentException("Length must be at least 1.", nameof(length));
        return GenerateFromPool(Digits, length);
    }

    /// <summary>
    /// Estimates the strength of a password based on its length and character variety.
    /// </summary>
    /// <param name="password">The password to evaluate. Must not be null.</param>
    /// <returns>A <see cref="PasswordStrength"/> enum value representing the estimated strength.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="password"/> is null.</exception>
    public static PasswordStrength CalculateStrength(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length < 6) return PasswordStrength.VeryWeak;

        int score = 0;
        if (password.Length >= 8)  score++;
        if (password.Length >= 12) score++;
        if (password.Length >= 16) score++;
        if (password.Any(char.IsLower))  score++;
        if (password.Any(char.IsUpper))  score++;
        if (password.Any(char.IsDigit))  score++;
        if (password.Any(c => Special.Contains(c))) score++;

        return score switch
        {
            <= 2 => PasswordStrength.Weak,
            <= 3 => PasswordStrength.Medium,
            <= 5 => PasswordStrength.Strong,
            _    => PasswordStrength.VeryStrong
        };
    }

    // RandomNumberGenerator.GetString / GetInt32 / Shuffle sample uniformly (no modulo bias).
    private static string GenerateFromPool(string pool, int length) =>
        length <= 0 ? string.Empty : RandomNumberGenerator.GetString(pool, length);

    private static char PickRandom(string pool) =>
        pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static void Shuffle(char[] array) =>
        RandomNumberGenerator.Shuffle(array.AsSpan());
}
