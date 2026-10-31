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
    /// The assertion's <c>openid.response_nonce</c>, unique per assertion Steam issues. Steam does not document that
    /// it refuses to confirm the same assertion twice, so to stop replays record the nonce (with
    /// <see cref="NonceIssuedAtUtc"/>) and refuse one you have already seen; entries older than <c>SteamConfig.OpenIdNonceMaxAgeSeconds</c> can be dropped,
    /// because <c>VerifyAsync</c> refuses those assertions anyway.
    /// </summary>
    public required string ResponseNonce { get; init; }

    /// <summary>When Steam issued the assertion (UTC), from the timestamp at the start of <see cref="ResponseNonce"/>.</summary>
    public required DateTime NonceIssuedAtUtc { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"Steam sign-in: {SteamId}";
}
