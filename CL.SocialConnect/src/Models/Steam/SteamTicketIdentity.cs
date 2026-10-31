using System.Text.Json.Serialization;

namespace CL.SocialConnect.Models.Steam;

/// <summary>
/// A Steam session ticket that <c>ISteamUserAuth/AuthenticateUserTicket</c> accepted: who the
/// player is. Returned by <see cref="Services.Steam.SteamAuthenticationService.AuthenticateAsync"/>
/// as the success value of a <c>Result&lt;SteamTicketIdentity&gt;</c>; a rejected ticket is a
/// failure (<c>UnauthorizedError</c>), never a value of this type.
/// </summary>
/// <param name="SteamId">The 64-bit Steam ID of the authenticated user.</param>
/// <param name="OwnerSteamId">
/// The Steam ID that owns the app license. Differs from <paramref name="SteamId"/> under Steam Family Sharing.
/// </param>
/// <param name="AppId">The App ID the ticket was validated against.</param>
/// <param name="VacBanned">Whether the user has a VAC ban on this app.</param>
/// <param name="PublisherBanned">Whether the publisher has banned the user from this app.</param>
public sealed record SteamTicketIdentity(
    string SteamId,
    string OwnerSteamId,
    string AppId,
    bool VacBanned,
    bool PublisherBanned)
{
    /// <summary>Whether the user plays on a license borrowed through Steam Family Sharing.</summary>
    public bool IsFamilyShared => !string.IsNullOrEmpty(OwnerSteamId) && OwnerSteamId != SteamId;
}

/// <summary>
/// Internal response wrapper for the ISteamUserAuth/AuthenticateUserTicket endpoint.
/// </summary>
internal class SteamAuthApiResponse
{
    [JsonPropertyName("response")]
    public SteamAuthApiParams? Response { get; set; }
}

internal class SteamAuthApiParams
{
    [JsonPropertyName("params")]
    public SteamAuthApiParamsData? Params { get; set; }

    [JsonPropertyName("error")]
    public SteamAuthApiError? Error { get; set; }
}

internal class SteamAuthApiParamsData
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("steamid")]
    public string SteamId { get; set; } = string.Empty;

    [JsonPropertyName("ownersteamid")]
    public string OwnerSteamId { get; set; } = string.Empty;

    [JsonPropertyName("vacbanned")]
    public bool VacBanned { get; set; }

    [JsonPropertyName("publisherbanned")]
    public bool PublisherBanned { get; set; }
}

internal class SteamAuthApiError
{
    [JsonPropertyName("errorcode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("errordesc")]
    public string ErrorDesc { get; set; } = string.Empty;
}
