using System.Globalization;
using System.Text.Json;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// Validates Steam authentication tickets via the Steam Web API
/// (<c>ISteamUserAuth/AuthenticateUserTicket</c>).
/// Use this to verify that a client's Steam session is genuine before granting access.
/// </summary>
public sealed class SteamAuthenticationService : IDisposable
{
    private readonly Models.SteamConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of <see cref="SteamAuthenticationService"/>.
    /// </summary>
    /// <param name="config">Steam configuration (API key, App ID, timeouts).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="SteamAuthenticatedEvent"/>.</param>
    public SteamAuthenticationService(Models.SteamConfig config, ILogger? logger = null, IEventBus? events = null)
        : this(config, new HttpClientHandler(), logger, events)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SteamAuthenticationService"/> that sends requests
    /// through <paramref name="handler"/> (tests, outbound proxies).
    /// </summary>
    /// <param name="config">Steam configuration (API key, App ID, timeouts).</param>
    /// <param name="handler">HTTP handler for the Steam Web API request; disposed with the service.</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="SteamAuthenticatedEvent"/>.</param>
    /// <exception cref="ArgumentException"><c>config.ApiBaseUrl</c> is not an absolute https URL without user info.</exception>
    public SteamAuthenticationService(Models.SteamConfig config, HttpMessageHandler handler, ILogger? logger = null, IEventBus? events = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handler);
        if (!config.TryGetApiBaseUri(out var baseAddress, out var urlError))
            throw new ArgumentException(urlError, nameof(config));

        _config = config;
        _logger = logger;
        _events = events;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 15),
            BaseAddress = baseAddress
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a Steam session ticket against the Steam Web API.
    /// The ticket should be obtained from the Steam client via <c>ISteamUser::GetAuthSessionTicket</c>.
    /// </summary>
    /// <param name="ticket">The hex-encoded session ticket string from the Steam client.</param>
    /// <param name="appId">
    /// The App ID to validate the ticket against. Falls back to <c>config.AppId</c> when not specified.
    /// </param>
    /// <param name="cancellationToken">Cancels the request (<see cref="SocialConnectErrorCodes.SteamCancelled"/>).</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the <see cref="SteamTicketIdentity"/> when Steam accepted the ticket.
    /// Failures:
    /// <list type="bullet">
    ///   <item><see cref="ValidationError"/> <see cref="SocialConnectErrorCodes.SteamInvalidTicket"/> (empty or not hex) /
    ///   <see cref="SocialConnectErrorCodes.SteamNoAppId"/> (no App ID) — nothing was sent to Steam.</item>
    ///   <item><see cref="UnauthorizedError"/> <see cref="SocialConnectErrorCodes.SteamAuthFailed"/> — Steam rejected the ticket
    ///   (invalid, expired, forged or for another app); <see cref="Error.Details"/> carries Steam's error code when given.</item>
    ///   <item><see cref="TimeoutError"/> <see cref="SocialConnectErrorCodes.SteamTimeout"/> — no answer within <c>TimeoutSeconds</c>.</item>
    ///   <item><see cref="UnavailableError"/> <see cref="SocialConnectErrorCodes.SteamNetworkError"/> — Steam could not be reached or answered 5xx;
    ///   <see cref="SocialConnectErrorCodes.SteamCancelled"/> — <paramref name="cancellationToken"/> was cancelled.</item>
    ///   <item><see cref="InternalError"/> <see cref="SocialConnectErrorCodes.SteamRequestRejected"/> (Steam answered 4xx, e.g. a bad API key),
    ///   <see cref="SocialConnectErrorCodes.SteamParseError"/> (unreadable answer, or an answer whose Steam IDs are not SteamID64s) or
    ///   <see cref="SocialConnectErrorCodes.SteamError"/> (anything else).</item>
    /// </list>
    /// </returns>
    public async Task<Result<SteamTicketIdentity>> AuthenticateAsync(string ticket, string? appId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ticket) || !IsHex(ticket))
        {
            const string msg = "Ticket must be a non-empty hex string";
            return Result<SteamTicketIdentity>.Failure(
                Error.Validation(SocialConnectErrorCodes.SteamInvalidTicket, msg)
            );
        }

        var resolvedAppId = !string.IsNullOrWhiteSpace(appId) ? appId : _config.AppId;
        if (string.IsNullOrWhiteSpace(resolvedAppId))
        {
            const string msg = "App ID is required for Steam authentication";
            return Result<SteamTicketIdentity>.Failure(
                Error.Validation(SocialConnectErrorCodes.SteamNoAppId, msg)
            );
        }

        var authenticatedAt = DateTime.UtcNow;

        try
        {
            var url = $"ISteamUserAuth/AuthenticateUserTicket/v1/?key={Uri.EscapeDataString(_config.ApiKey)}&appid={Uri.EscapeDataString(resolvedAppId)}&ticket={ticket}";
            _logger?.Debug($"Validating Steam ticket for App ID {resolvedAppId}");

            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<SteamAuthApiResponse>(json, JsonOptions);

            // Check for API-level error
            if (response?.Response?.Error is { } error)
            {
                var errMsg = $"Steam auth error {error.ErrorCode}: {error.ErrorDesc}";
                _logger?.Warning(errMsg);

                _events?.Publish(new SteamAuthenticatedEvent(
                    string.Empty, false, authenticatedAt, resolvedAppId, errMsg
                ));

                return Result<SteamTicketIdentity>.Failure(
                    Error.Unauthorized(SocialConnectErrorCodes.SteamAuthFailed, errMsg, error.ErrorCode.ToString(CultureInfo.InvariantCulture))
                );
            }

            var @params = response?.Response?.Params;
            if (@params is null || @params.Result != "OK")
            {
                var resultStr = @params?.Result ?? "null";
                var errMsg = $"Steam auth result was not OK: {resultStr}";
                _logger?.Warning(errMsg);

                _events?.Publish(new SteamAuthenticatedEvent(
                    string.Empty, false, authenticatedAt, resolvedAppId, errMsg
                ));

                return Result<SteamTicketIdentity>.Failure(
                    Error.Unauthorized(SocialConnectErrorCodes.SteamAuthFailed, errMsg)
                );
            }

            // Steam's answer is data from the network: only SteamID64s reach the log, the event and the caller.
            if (!SteamIds.IsSteamId64(@params.SteamId) || !SteamIds.IsSteamId64(@params.OwnerSteamId))
            {
                const string errMsg = "Steam auth answer did not carry valid Steam IDs";
                _logger?.Error(errMsg);

                _events?.Publish(new SteamAuthenticatedEvent(
                    string.Empty, false, authenticatedAt, resolvedAppId, errMsg
                ));

                return Result<SteamTicketIdentity>.Failure(Error.Internal(SocialConnectErrorCodes.SteamParseError, errMsg));
            }

            var identity = new SteamTicketIdentity(
                @params.SteamId,
                @params.OwnerSteamId,
                resolvedAppId,
                @params.VacBanned,
                @params.PublisherBanned);

            _logger?.Info($"Steam authentication succeeded for {identity.SteamId}");

            _events?.Publish(new SteamAuthenticatedEvent(
                identity.SteamId, true, authenticatedAt, resolvedAppId
            ));

            return identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string errMsg = "Steam auth request was cancelled";
            _logger?.Warning(errMsg);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(Error.Unavailable(SocialConnectErrorCodes.SteamCancelled, errMsg));
        }
        catch (OperationCanceledException ex)
        {
            var errMsg = $"Steam auth request timed out: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(Error.Timeout(SocialConnectErrorCodes.SteamTimeout, errMsg));
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode is >= 400 and < 500)
        {
            var errMsg = $"Steam rejected the auth request ({(int)ex.StatusCode!}); check the API key and App ID";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(
                Error.Internal(SocialConnectErrorCodes.SteamRequestRejected, errMsg, ((int)ex.StatusCode!).ToString(CultureInfo.InvariantCulture)));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"Network error during Steam auth: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(Error.Unavailable(SocialConnectErrorCodes.SteamNetworkError, errMsg));
        }
        catch (JsonException ex)
        {
            var errMsg = $"Failed to parse Steam auth response: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(Error.Internal(SocialConnectErrorCodes.SteamParseError, errMsg));
        }
        catch (Exception ex)
        {
            var errMsg = $"Unexpected error during Steam auth: {ex.Message}";
            _logger?.Error(errMsg, ex);

            _events?.Publish(new SteamAuthenticatedEvent(
                string.Empty, false, authenticatedAt, resolvedAppId, errMsg
            ));

            return Result<SteamTicketIdentity>.Failure(Error.Internal(SocialConnectErrorCodes.SteamError, errMsg, ex.GetType().Name));
        }
    }
    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    private static bool IsHex(string value)
    {
        foreach (var c in value)
            if (!char.IsAsciiHexDigit(c))
                return false;
        return true;
    }
}
