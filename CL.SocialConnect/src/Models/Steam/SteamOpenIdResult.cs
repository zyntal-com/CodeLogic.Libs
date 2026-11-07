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

    /// <summary>
    /// The assertion's <c>openid.response_nonce</c>, unique per assertion Steam issues. <c>VerifyAsync</c> records it
    /// in its <see cref="Services.Steam.IOpenIdNonceStore"/> and refuses a nonce it has verified before
    /// (<c>social.steam.openid_replayed</c>), so an application needs its own tracking only when it runs several
    /// servers without a shared store.
    /// </summary>
    public required string ResponseNonce { get; init; }

    /// <summary>When Steam issued the assertion (UTC), from the timestamp at the start of <see cref="ResponseNonce"/>.</summary>
    public required DateTime NonceIssuedAtUtc { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"Steam sign-in: {SteamId}";
}
