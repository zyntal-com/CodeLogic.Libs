using System.Globalization;
using System.Text.RegularExpressions;
using CL.SocialConnect.Events;
using CL.SocialConnect.Models;
using CL.SocialConnect.Models.Steam;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// "Sign in through Steam" for websites: Steam OpenID 2.0 against <c>steamcommunity.com</c>.
/// <para>
/// <see cref="BuildLoginUrl"/> gives the URL to send the browser to. Steam sends it back to your
/// <c>return_to</c> URL with a signed assertion, and <see cref="VerifyAsync(IEnumerable{KeyValuePair{string, string}}, string, CancellationToken)"/>
/// checks it locally and then asks Steam to confirm it (<c>check_authentication</c>). The only thing
/// Steam returns is the player's 64-bit Steam ID. Use <see cref="SteamProfileService"/> afterwards
/// for the name and avatar.
/// </para>
/// <para>
/// Bind every login attempt to the browser that started it: put a random per-attempt value in
/// <c>return_to</c> (for example <c>?state=…</c>), keep it in a cookie, and pass the exact
/// <c>return_to</c> to <c>VerifyAsync</c>. Steam signs <c>return_to</c>, so a callback taken from
/// someone else's login cannot be replayed into another browser.
/// </para>
/// </summary>
public sealed partial class SteamOpenIdService : IDisposable
{
    /// <summary>Steam's OpenID 2.0 provider endpoint.</summary>
    public const string Endpoint = "https://steamcommunity.com/openid/login";

    private const string OpenIdNs = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    // Fields Steam always signs; an assertion that leaves any of them unsigned is refused.
    private static readonly string[] RequiredSigned = ["op_endpoint", "claimed_id", "identity", "return_to", "response_nonce"];

    // An assertion stamped further ahead than this (clock skew between Steam and this host) is refused.
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(1);

    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly HttpClient _http;
    private readonly TimeSpan? _maxNonceAge;

    /// <summary>
    /// Initializes a new instance of <see cref="SteamOpenIdService"/>.
    /// </summary>
    /// <param name="config">Steam configuration (<c>TimeoutSeconds</c> and <c>OpenIdNonceMaxAgeSeconds</c>; OpenID needs no API key).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="SteamOpenIdVerifiedEvent"/>.</param>
    public SteamOpenIdService(SteamConfig config, ILogger? logger = null, IEventBus? events = null)
        : this(config, new HttpClientHandler(), logger, events)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SteamOpenIdService"/> that sends verification
    /// requests through <paramref name="handler"/> (tests, outbound proxies).
    /// </summary>
    /// <param name="config">Steam configuration (<c>TimeoutSeconds</c> and <c>OpenIdNonceMaxAgeSeconds</c>).</param>
    /// <param name="handler">HTTP handler for the <c>check_authentication</c> request; disposed with the service.</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing <see cref="SteamOpenIdVerifiedEvent"/>.</param>
    public SteamOpenIdService(SteamConfig config, HttpMessageHandler handler, ILogger? logger = null, IEventBus? events = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(handler);
        _logger = logger;
        _events = events;
        _maxNonceAge = config.OpenIdNonceMaxAgeSeconds > 0 ? TimeSpan.FromSeconds(config.OpenIdNonceMaxAgeSeconds) : null;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 15)
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the Steam sign-in URL to redirect the browser to.
    /// </summary>
    /// <param name="returnTo">
    /// Absolute <c>http</c>/<c>https</c> URL Steam sends the browser back to, including your
    /// per-attempt state. Pass the same value to <c>VerifyAsync</c>.
    /// </param>
    /// <param name="realm">
    /// The site Steam shows the user ("Sign in to …"). Defaults to the scheme and host of
    /// <paramref name="returnTo"/>; when given, <paramref name="returnTo"/> must start with it.
    /// </param>
    /// <returns>The <c>https://steamcommunity.com/openid/login?…</c> URL.</returns>
    /// <exception cref="ArgumentException">The URLs are not absolute http(s) URLs, or <paramref name="returnTo"/> is outside <paramref name="realm"/>.</exception>
    public string BuildLoginUrl(string returnTo, string? realm = null)
    {
        var target = AbsoluteHttpUrl(returnTo, nameof(returnTo));
        if (!string.IsNullOrEmpty(target.Fragment))
            throw new ArgumentException("The return URL must not contain a fragment.", nameof(returnTo));

        var resolvedRealm = realm is null
            ? target.GetLeftPart(UriPartial.Authority) + "/"
            : AbsoluteHttpUrl(realm, nameof(realm)).AbsoluteUri;
        if (!target.AbsoluteUri.StartsWith(resolvedRealm, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The return URL must be inside the realm.", nameof(returnTo));

        var query = string.Join('&', new[]
        {
            ("openid.ns", OpenIdNs),
            ("openid.mode", "checkid_setup"),
            ("openid.return_to", returnTo),
            ("openid.realm", resolvedRealm),
            ("openid.identity", IdentifierSelect),
            ("openid.claimed_id", IdentifierSelect)
        }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));

        return Endpoint + "?" + query;
    }

    /// <summary>
    /// Verifies Steam's callback and returns the player's Steam ID.
    /// </summary>
    /// <param name="queryString">The raw query string of the callback request (with or without the leading <c>?</c>).</param>
    /// <param name="expectedReturnTo">The exact <c>return_to</c> this browser was sent to Steam with.</param>
    /// <param name="cancellationToken">Cancels the verification request.</param>
    /// <returns>A <see cref="Result{T}"/> with the verified <see cref="SteamOpenIdResult"/>, or why it was refused.</returns>
    public Task<Result<SteamOpenIdResult>> VerifyAsync(string queryString, string expectedReturnTo, CancellationToken cancellationToken = default) =>
        VerifyAsync(ParseQuery(queryString ?? string.Empty), expectedReturnTo, cancellationToken);

    /// <summary>
    /// Verifies Steam's callback and returns the player's Steam ID.
    /// <para>
    /// Local checks first (mode, namespace, provider endpoint, <c>return_to</c>, claimed id = identity,
    /// a well-formed SteamID64, the required fields signed, no repeated <c>openid.*</c> parameters, a well-formed
    /// <c>response_nonce</c> no older than <c>OpenIdNonceMaxAgeSeconds</c> (<c>social.steam.openid_expired</c>)
    /// and not dated more than a minute ahead),
    /// then Steam must answer <c>is_valid:true</c> to <c>check_authentication</c>.
    /// </para>
    /// </summary>
    /// <param name="parameters">The callback's query parameters (decoded). Non-<c>openid.*</c> parameters are ignored.</param>
    /// <param name="expectedReturnTo">The exact <c>return_to</c> this browser was sent to Steam with.</param>
    /// <param name="cancellationToken">Cancels the verification request.</param>
    /// <returns>A <see cref="Result{T}"/> with the verified <see cref="SteamOpenIdResult"/>, or why it was refused.</returns>
    public async Task<Result<SteamOpenIdResult>> VerifyAsync(IEnumerable<KeyValuePair<string, string>> parameters, string expectedReturnTo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var verifiedAt = DateTime.UtcNow;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (!key.StartsWith("openid.", StringComparison.Ordinal))
                continue;
            if (!fields.TryAdd(key, value ?? string.Empty))
                return Refuse("social.steam.openid_invalid", $"Parameter '{key}' appears more than once", verifiedAt);
        }

        string Get(string name) => fields.TryGetValue("openid." + name, out var v) ? v : string.Empty;

        var mode = Get("mode");
        if (mode == "cancel")
            return Refuse("social.steam.openid_cancelled", "The Steam sign-in was cancelled", verifiedAt);
        if (mode != "id_res")
            return Refuse("social.steam.openid_invalid", "Not a positive Steam assertion", verifiedAt);
        if (Get("ns") != OpenIdNs || Get("op_endpoint") != Endpoint)
            return Refuse("social.steam.openid_invalid", "The response did not come from Steam's OpenID endpoint", verifiedAt);
        if (string.IsNullOrEmpty(expectedReturnTo) || !string.Equals(Get("return_to"), expectedReturnTo, StringComparison.Ordinal))
            return Refuse("social.steam.openid_return_to", "The response was not meant for this sign-in (return_to mismatch)", verifiedAt);

        var claimedId = Get("claimed_id");
        var match = ClaimedIdPattern().Match(claimedId);
        if (!match.Success || !string.Equals(Get("identity"), claimedId, StringComparison.Ordinal))
            return Refuse("social.steam.openid_identity", "Steam returned an unexpected identity", verifiedAt);

        var signed = Get("signed").Split(',', StringSplitOptions.TrimEntries);
        if (string.IsNullOrEmpty(Get("sig")) || RequiredSigned.Any(f => !signed.Contains(f, StringComparer.Ordinal)))
            return Refuse("social.steam.openid_invalid", "The assertion does not sign the required fields", verifiedAt);

        // OpenID 2.0 response_nonce: "yyyy-MM-ddTHH:mm:ssZ" followed by up to 235 unique characters.
        var nonce = Get("response_nonce");
        if (!TryParseNonceTime(nonce, out var issuedAt))
            return Refuse("social.steam.openid_invalid", "The assertion's response_nonce is malformed", verifiedAt);
        if (issuedAt > verifiedAt + MaxFutureSkew)
            return Refuse("social.steam.openid_invalid", "The assertion's response_nonce is dated in the future", verifiedAt);
        if (_maxNonceAge is { } maxAge && verifiedAt - issuedAt > maxAge)
            return Refuse("social.steam.openid_expired", $"The assertion is older than {(int)maxAge.TotalSeconds} seconds", verifiedAt);

        var steamId = match.Groups[1].Value;

        // Ask Steam whether it really issued this assertion.
        var verify = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Key == "openid.mode" ? "check_authentication" : f.Value)).ToList();
        try
        {
            using var content = new FormUrlEncodedContent(verify);
            using var response = await _http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Steam answers in key:value lines; only an exact "is_valid:true" line counts.
            var valid = response.IsSuccessStatusCode && body
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line, "is_valid:true", StringComparison.Ordinal));
            if (!valid)
                return Refuse("social.steam.openid_rejected", "Steam did not confirm the sign-in", verifiedAt, steamId);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var errMsg = $"[{SocialError.Timeout}] Steam OpenID verification timed out: {ex.Message}";
            _logger?.Warning(errMsg);
            _events?.Publish(new SteamOpenIdVerifiedEvent(string.Empty, false, verifiedAt, errMsg));
            return Result<SteamOpenIdResult>.Failure(Error.Timeout("social.steam.openid_timeout", errMsg));
        }
        catch (HttpRequestException ex)
        {
            var errMsg = $"[{SocialError.NetworkError}] Steam could not be reached to verify the sign-in: {ex.Message}";
            _logger?.Warning(errMsg);
            _events?.Publish(new SteamOpenIdVerifiedEvent(string.Empty, false, verifiedAt, errMsg));
            return Result<SteamOpenIdResult>.Failure(Error.Unavailable("social.steam.openid_unavailable", errMsg));
        }

        _logger?.Info($"Steam OpenID sign-in verified for {steamId}");
        _events?.Publish(new SteamOpenIdVerifiedEvent(steamId, true, verifiedAt));
        return Result<SteamOpenIdResult>.Success(new SteamOpenIdResult
        {
            SteamId = steamId,
            ClaimedId = claimedId,
            ResponseNonce = nonce,
            NonceIssuedAtUtc = issuedAt
        });
    }

    private static bool TryParseNonceTime(string nonce, out DateTime issuedAtUtc)
    {
        issuedAtUtc = default;
        return nonce.Length is >= 20 and <= 255
            && DateTime.TryParseExact(nonce.AsSpan(0, 20), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out issuedAtUtc);
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Result<SteamOpenIdResult> Refuse(string code, string message, DateTime verifiedAt, string? steamId = null)
    {
        var errMsg = $"[{SocialError.AuthenticationFailed}] {message}";
        _logger?.Warning($"Steam OpenID sign-in refused: {message}");
        _events?.Publish(new SteamOpenIdVerifiedEvent(steamId ?? string.Empty, false, verifiedAt, errMsg));
        return Result<SteamOpenIdResult>.Failure(Error.Unauthorized(code, errMsg));
    }

    private static Uri AbsoluteHttpUrl(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("An absolute http or https URL is required.", paramName);
        return uri;
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            result.Add(new(Decode(key), Decode(value)));
        }
        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    [GeneratedRegex(@"^https://steamcommunity\.com/openid/id/(7656119[0-9]{10})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClaimedIdPattern();
}
