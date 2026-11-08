using CodeLogic.Core.Results;

namespace CL.SocialConnect.Models;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.SocialConnect reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant.
/// </summary>
public static class SocialConnectErrorCodes
{
    // ── Discord webhooks ─────────────────────────────────────────────────────

    /// <summary>No webhook URL was passed and no default is configured. Kind: <see cref="ValidationError"/>.</summary>
    public const string DiscordNoUrl = "social.discord.no_url";

    /// <summary>
    /// The webhook URL is not a Discord webhook (<c>https://discord.com/api/webhooks/{id}/{token}</c> on a Discord host).
    /// Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string DiscordInvalidUrl = "social.discord.invalid_url";

    /// <summary>The message content is longer than Discord's 2000 characters. Kind: <see cref="ValidationError"/>.</summary>
    public const string DiscordContentTooLong = "social.discord.content_too_long";

    /// <summary>The message has more than Discord's 10 embeds. Kind: <see cref="ValidationError"/>.</summary>
    public const string DiscordTooManyEmbeds = "social.discord.too_many_embeds";

    /// <summary>
    /// Discord answered with a non-2xx status. Kind: <see cref="InternalError"/>; the message names the HTTP status and
    /// <see cref="Error.Details"/> carries the response body.
    /// </summary>
    public const string DiscordDeliveryFailed = "social.discord.delivery_failed";

    /// <summary>The webhook request was cancelled by the caller. Kind: <see cref="UnavailableError"/>.</summary>
    public const string DiscordCancelled = "social.discord.cancelled";

    /// <summary>The webhook request took longer than the configured timeout. Kind: <see cref="TimeoutError"/>.</summary>
    public const string DiscordTimeout = "social.discord.timeout";

    /// <summary>Discord could not be reached (DNS, refused, TLS). Kind: <see cref="UnavailableError"/>.</summary>
    public const string DiscordNetworkError = "social.discord.network_error";

    /// <summary>An unexpected error while sending a webhook. Kind: <see cref="InternalError"/>; <see cref="Error.Details"/> is the exception type.</summary>
    public const string DiscordError = "social.discord.error";

    // ── Steam Web API (profiles, bans, games, ticket authentication) ───────────

    /// <summary>The SteamId is empty or not a 17-digit SteamID64. Kind: <see cref="ValidationError"/>.</summary>
    public const string SteamInvalidSteamId = "social.steam.invalid_steamid";

    /// <summary>Steam has no player for the SteamId. Kind: <see cref="NotFoundError"/>; <see cref="Error.Details"/> is the SteamId.</summary>
    public const string SteamPlayerNotFound = "social.steam.player_not_found";

    /// <summary>Steam has no ban record for the SteamId. Kind: <see cref="NotFoundError"/>; <see cref="Error.Details"/> is the SteamId.</summary>
    public const string SteamBansNotFound = "social.steam.bans_not_found";

    /// <summary>The session ticket is empty or not a hex string. Kind: <see cref="ValidationError"/>.</summary>
    public const string SteamInvalidTicket = "social.steam.invalid_ticket";

    /// <summary>No App ID was passed and none is configured. Kind: <see cref="ValidationError"/>.</summary>
    public const string SteamNoAppId = "social.steam.no_appid";

    /// <summary>
    /// Steam refused the session ticket. Kind: <see cref="UnauthorizedError"/>; <see cref="Error.Details"/> carries
    /// Steam's error code when it sent one.
    /// </summary>
    public const string SteamAuthFailed = "social.steam.auth_failed";

    /// <summary>
    /// Steam rejected the request itself (HTTP 400/401/403), usually a wrong API key or App ID. Kind:
    /// <see cref="InternalError"/>; <see cref="Error.Details"/> is the HTTP status.
    /// </summary>
    public const string SteamRequestRejected = "social.steam.request_rejected";

    /// <summary>A Steam answer could not be parsed or lacked required fields. Kind: <see cref="InternalError"/>.</summary>
    public const string SteamParseError = "social.steam.parse_error";

    /// <summary>A Steam request took longer than the configured timeout. Kind: <see cref="TimeoutError"/>.</summary>
    public const string SteamTimeout = "social.steam.timeout";

    /// <summary>Steam could not be reached or answered with a server error. Kind: <see cref="UnavailableError"/>.</summary>
    public const string SteamNetworkError = "social.steam.network_error";

    /// <summary>A Steam request (Web API or OpenID) was cancelled by the caller. Kind: <see cref="UnavailableError"/>.</summary>
    public const string SteamCancelled = "social.steam.cancelled";

    /// <summary>An unexpected error during a Steam request. Kind: <see cref="InternalError"/>; <see cref="Error.Details"/> is the exception type.</summary>
    public const string SteamError = "social.steam.error";

    // ── Steam OpenID sign-in ─────────────────────────────────────────────────

    /// <summary>
    /// The OpenID response is malformed or not a valid Steam assertion (duplicate parameter, wrong endpoint, missing
    /// signature, bad nonce). Kind: <see cref="UnauthorizedError"/>.
    /// </summary>
    public const string SteamOpenIdInvalid = "social.steam.openid_invalid";

    /// <summary>The user cancelled the sign-in on Steam (<c>openid.mode=cancel</c>). Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdCancelled = "social.steam.openid_cancelled";

    /// <summary>The response was not meant for this sign-in (<c>return_to</c> mismatch). Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdReturnTo = "social.steam.openid_return_to";

    /// <summary>Steam returned an unexpected identity (not a Steam community id, or identity and claimed_id differ). Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdIdentity = "social.steam.openid_identity";

    /// <summary>The assertion is older than the configured maximum age. Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdExpired = "social.steam.openid_expired";

    /// <summary>Steam did not confirm the assertion (<c>is_valid:false</c>). Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdRejected = "social.steam.openid_rejected";

    /// <summary>The assertion was already used, or replay protection is at capacity. Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SteamOpenIdReplayed = "social.steam.openid_replayed";

    /// <summary>Too many sign-ins are being verified at once; try again later. Kind: <see cref="UnavailableError"/>.</summary>
    public const string SteamOpenIdBusy = "social.steam.openid_busy";

    /// <summary>Verifying the assertion with Steam took longer than the configured timeout. Kind: <see cref="TimeoutError"/>.</summary>
    public const string SteamOpenIdTimeout = "social.steam.openid_timeout";

    /// <summary>Steam could not be reached to verify the assertion. Kind: <see cref="UnavailableError"/>.</summary>
    public const string SteamOpenIdUnavailable = "social.steam.openid_unavailable";
}
