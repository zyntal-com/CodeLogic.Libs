using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Discord;

/// <summary>
/// Represents a Discord user object returned by the Discord API (<c>GET /users/@me</c>).
/// <para>
/// A data model only: CL.SocialConnect does not implement Discord OAuth2 or call the user endpoints. Use it to
/// deserialize the response when your application runs the OAuth2 flow itself (for example with ASP.NET Core's
/// authentication handlers) and calls <c>https://discord.com/api/users/@me</c> with the token.
/// </para>
/// </summary>
public class DiscordUser
{
    /// <summary>The user's unique Discord ID (snowflake).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The user's username (not unique across the platform).</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>The user's Discord discriminator tag (e.g. "0001"). May be "0" for new usernames.</summary>
    [JsonPropertyName("discriminator")]
    public string Discriminator { get; set; } = string.Empty;

    /// <summary>The user's avatar hash. Use with Discord CDN to build the avatar URL.</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>Whether the user is a Discord bot account.</summary>
    [JsonPropertyName("bot")]
    public bool Bot { get; set; } = false;

    /// <summary>Whether the user has two-factor authentication enabled.</summary>
    [JsonPropertyName("mfa_enabled")]
    public bool MfaEnabled { get; set; } = false;

    /// <summary>The user's chosen language option.</summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    /// <summary>Whether the email on this account has been verified.</summary>
    [JsonPropertyName("verified")]
    public bool Verified { get; set; } = false;

    /// <summary>The user's email address (requires the <c>email</c> OAuth2 scope).</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Builds the avatar URL for this user using the Discord CDN.
    /// Returns a default avatar URL when <see cref="Avatar"/> is null.
    /// </summary>
    /// <param name="size">Image size in pixels (must be a power of 2 between 16 and 4096).</param>
    public string GetAvatarUrl(int size = 256)
    {
        if (string.IsNullOrEmpty(Avatar))
        {
            // Default avatar index is discriminator % 5 (or 0 for new usernames)
            var index = int.TryParse(Discriminator, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var discriminator) ? discriminator % 5 : 0;
            return $"https://cdn.discordapp.com/embed/avatars/{index}.png";
        }

        var extension = Avatar.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
        return $"https://cdn.discordapp.com/avatars/{Uri.EscapeDataString(Id)}/{Uri.EscapeDataString(Avatar)}.{extension}?size={size}";
    }

    /// <summary>Returns the user's tag in <c>Username#Discriminator</c> format.</summary>
    public override string ToString() =>
        Discriminator == "0" ? Username : $"{Username}#{Discriminator}";
}
