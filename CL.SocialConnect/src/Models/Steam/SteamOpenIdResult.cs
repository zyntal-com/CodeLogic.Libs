namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// A Steam sign-in verified by <see cref="Services.Steam.SteamOpenIdService"/>.
/// </summary>
public sealed class SteamOpenIdResult
{
    /// <summary>The player's 64-bit Steam ID (e.g. <c>76561197960435530</c>).</summary>
    public required string SteamId { get; init; }

    /// <summary>The OpenID claimed identifier, <c>https://steamcommunity.com/openid/id/{SteamId}</c>.</summary>
    public required string ClaimedId { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"Steam sign-in: {SteamId}";
}
