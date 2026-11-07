using System.Security.Cryptography;
using System.Text;
using CodeLogic.Core.Configuration;

namespace CL.Mqtt.Models;

/// <summary>
/// An account of the embedded broker (<see cref="MqttBrokerConfig.Users"/>) with its topic permissions.
/// Permissions are topic filters, so wildcards work: <c>sensors/#</c>, <c>home/+/state</c>.
/// </summary>
public sealed class MqttBrokerUser
{
    /// <summary>Prefix of a hashed <see cref="Password"/> produced by <see cref="HashPassword"/>.</summary>
    public const string HashPrefix = "$pbkdf2-sha256$";

    /// <summary>PBKDF2 iterations <see cref="HashPassword"/> uses.</summary>
    public const int HashIterations = 600_000;

    /// <summary>The user name the client sends.</summary>
    [ConfigField(Label = "Username", Required = true, Order = 0)]
    public string Username { get; set; } = "";

    /// <summary>
    /// The password the client sends, either in clear or as a PBKDF2-SHA256 hash made with <see cref="HashPassword"/>
    /// (<c>$pbkdf2-sha256$iterations$salt$hash</c>). Both are compared in constant time; a hash keeps the password
    /// out of the configuration file.
    /// </summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, Order = 1,
        Description = "Clear text, or a hash from MqttBrokerUser.HashPassword.")]
    public string Password { get; set; } = "";

    /// <summary>Whether this user may publish at all. With false, <see cref="PublishTopics"/> is ignored.</summary>
    [ConfigField(Label = "Can Publish", Order = 2)]
    public bool CanPublish { get; set; } = true;

    /// <summary>Topics this user may publish to. Empty allows every topic (when <see cref="CanPublish"/> is on).</summary>
    [ConfigField(Label = "Publish Topics", Description = "Topic filters; empty allows all.", Order = 3)]
    public List<string> PublishTopics { get => field; set => field = value ?? []; } = [];

    /// <summary>Whether this user may subscribe at all. With false, <see cref="SubscribeTopics"/> is ignored.</summary>
    [ConfigField(Label = "Can Subscribe", Order = 4)]
    public bool CanSubscribe { get; set; } = true;

    /// <summary>
    /// Topic filters this user may subscribe to. A subscription is allowed when one of these covers it:
    /// <c>home/#</c> covers <c>home/kitchen/+</c>, but <c>home/+</c> does not cover <c>home/#</c>. Empty allows every filter.
    /// </summary>
    [ConfigField(Label = "Subscribe Topics", Description = "Topic filters; empty allows all.", Order = 5)]
    public List<string> SubscribeTopics { get => field; set => field = value ?? []; } = [];

    /// <summary>
    /// Client ids this user may connect with. Empty allows any id. Use it to pin a device account to its device,
    /// so a leaked password cannot be used to take over another client's session.
    /// </summary>
    [ConfigField(Label = "Client IDs", Description = "Allowed client ids; empty allows any.", Order = 6, Collapsed = true)]
    public List<string> ClientIds { get => field; set => field = value ?? []; } = [];

    /// <summary>True when <see cref="Password"/> is a hash made with <see cref="HashPassword"/>.</summary>
    public bool HasHashedPassword => IsHash(Password);

    /// <summary>
    /// Hashes a password for the <see cref="Password"/> field: PBKDF2-SHA256, <see cref="HashIterations"/> iterations,
    /// a random 16-byte salt and a 32-byte hash, as <c>$pbkdf2-sha256$iterations$salt$hash</c> (base64 parts).
    /// </summary>
    public static string HashPassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, HashIterations, HashAlgorithmName.SHA256, 32);
        return $"{HashPrefix}{HashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool IsHash(string? value) => value is not null && value.StartsWith(HashPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses a hash made with <see cref="HashPassword"/>. Returns false for anything that is not one (including a
    /// hash with an unreasonable iteration count).
    /// </summary>
    internal static bool TryParseHash(string value, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (!IsHash(value))
            return false;
        var parts = value[HashPrefix.Length..].Split('$');
        if (parts.Length != 3 || !int.TryParse(parts[0], out iterations) || iterations is < 1_000 or > 10_000_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            hash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length is >= 8 and <= 64 && hash.Length is >= 16 and <= 64;
    }

    /// <summary>Constant-time check of <paramref name="candidate"/> against a clear or hashed <paramref name="expected"/> password.</summary>
    internal static bool PasswordMatches(string expected, string candidate)
    {
        if (TryParseHash(expected, out var iterations, out var salt, out var hash))
        {
            var derived = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, iterations, HashAlgorithmName.SHA256, hash.Length);
            return CryptographicOperations.FixedTimeEquals(derived, hash);
        }
        // Compare SHA-256 digests so neither the length nor the content leaks through timing.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }
}
