using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Discord;

/// <summary>
/// Represents the payload sent to a Discord webhook endpoint.
/// At least one of <see cref="Content"/>, <see cref="Embeds"/>, or a file must be provided.
/// </summary>
public class DiscordWebhookMessage
{
    /// <summary>Plain text content of the message. Max 2000 characters.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Override the webhook's configured username for this message.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Override the webhook's configured avatar URL for this message.</summary>
    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// When <c>true</c>, the message is sent as a text-to-speech message.
    /// </summary>
    [JsonPropertyName("tts")]
    public bool Tts { get; set; } = false;

    /// <summary>Rich embed objects to attach to the message. Max 10 embeds.</summary>
    [JsonPropertyName("embeds")]
    public List<DiscordEmbed>? Embeds { get; set; }

    /// <summary>Controls which mentions in the message content are allowed to ping.</summary>
    [JsonPropertyName("allowed_mentions")]
    public DiscordAllowedMentions? AllowedMentions { get; set; }
}

/// <summary>
/// Controls which mentions within the message content are allowed to trigger pings.
/// </summary>
public class DiscordAllowedMentions
{
    /// <summary>
    /// An array of allowed mention types: <c>"roles"</c>, <c>"users"</c>, <c>"everyone"</c>.
    /// When empty, no mention types are allowed.
    /// </summary>
    [JsonPropertyName("parse")]
    public List<string>? Parse { get; set; }

    /// <summary>List of role IDs (up to 100) whose mentions are allowed.</summary>
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }

    /// <summary>List of user IDs (up to 100) whose mentions are allowed.</summary>
    [JsonPropertyName("users")]
    public List<string>? Users { get; set; }

    /// <summary>
    /// Creates an instance that suppresses all mentions (safe default for webhook messages).
    /// </summary>
    public static DiscordAllowedMentions None => new() { Parse = [] };

    /// <summary>
    /// Creates an instance that allows all mention types.
    /// </summary>
    public static DiscordAllowedMentions All => new() { Parse = ["roles", "users", "everyone"] };
}

/// <summary>
/// Represents a Discord OAuth2 token response (<c>POST /oauth2/token</c>).
/// <para>
/// A data model only: CL.SocialConnect does not implement Discord OAuth2. Use it to deserialize the token
/// response when your application runs the OAuth2 flow itself; see <see cref="DiscordUser"/>.
/// </para>
/// </summary>
public class DiscordOAuthToken
{
    /// <summary>The access token string.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>The token type (typically <c>"Bearer"</c>).</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    /// <summary>Number of seconds until the token expires.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>The scopes granted by this token.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
}
