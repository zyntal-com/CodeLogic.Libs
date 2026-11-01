using CodeLogic.Core.Events;

namespace CL.SocialConnect.Events;

/// <summary>
/// Published on the <see cref="IEventBus"/> after a Discord webhook send attempt completes
/// (whether successful or not).
/// </summary>
/// <param name="WebhookUrl">
/// The webhook URL that was called, with its secret token redacted (<c>https://discord.com/api/webhooks/{id}/***</c>).
/// The token is never published.
/// </param>
/// <param name="Success">Whether the delivery succeeded (HTTP 2xx).</param>
/// <param name="SentAt">UTC timestamp of the send attempt.</param>
/// <param name="StatusCode">The HTTP status code returned, or <c>null</c> if a network error occurred.</param>
/// <param name="ErrorMessage">Error description when <see cref="Success"/> is <c>false</c>.</param>
public record WebhookSentEvent(
    string WebhookUrl,
    bool Success,
    DateTime SentAt,
    int? StatusCode = null,
    string? ErrorMessage = null
) : IEvent
{
    /// <summary>The webhook's id (the numeric segment of its URL), or <c>null</c> when unknown.</summary>
    public string? WebhookId { get; init; }
}

/// <summary>
/// Published on the <see cref="IEventBus"/> after a Steam authentication attempt completes.
/// </summary>
/// <param name="SteamId">The 64-bit Steam ID of the player who attempted authentication.</param>
/// <param name="Success">Whether authentication succeeded.</param>
/// <param name="AuthenticatedAt">UTC timestamp of the authentication attempt.</param>
/// <param name="AppId">The App ID the ticket was validated against.</param>
/// <param name="ErrorMessage">Error description when <see cref="Success"/> is <c>false</c>.</param>
public record SteamAuthenticatedEvent(
    string SteamId,
    bool Success,
    DateTime AuthenticatedAt,
    string? AppId = null,
    string? ErrorMessage = null
) : IEvent;

/// <summary>
/// Published on the <see cref="IEventBus"/> after a Steam OpenID ("Sign in through Steam") callback
/// is verified or refused.
/// </summary>
/// <param name="SteamId">The 64-bit Steam ID, or empty when the assertion was refused before it was known.</param>
/// <param name="Success">Whether Steam confirmed the sign-in.</param>
/// <param name="VerifiedAt">UTC timestamp of the verification.</param>
/// <param name="ErrorMessage">Why the sign-in was refused when <see cref="Success"/> is <c>false</c>.</param>
public record SteamOpenIdVerifiedEvent(
    string SteamId,
    bool Success,
    DateTime VerifiedAt,
    string? ErrorMessage = null
) : IEvent;

/// <summary>
/// Published on the <see cref="IEventBus"/> when a Steam player profile is fetched.
/// </summary>
/// <param name="SteamId">The 64-bit Steam ID of the fetched player.</param>
/// <param name="FetchedAt">UTC timestamp of the profile fetch.</param>
/// <param name="FromCache">Whether the result was served from the local cache.</param>
public record SteamProfileFetchedEvent(
    string SteamId,
    DateTime FetchedAt,
    bool FromCache = false
) : IEvent;
