using CodeLogic.Core.Configuration;

namespace CL.SocialConnect.Models;

/// <summary>
/// Root configuration model for the CL.SocialConnect library.
/// Serialized as <c>config.socialconnect.json</c> in the library's config directory.
/// </summary>
[ConfigSection("socialconnect")]
public class SocialConnectConfig : ConfigModelBase
{
    /// <summary>Whether the SocialConnect library is enabled.</summary>
    [ConfigField(Label = "Enabled", Description = "Master switch for Discord + Steam integrations.",
        Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Discord integration configuration.</summary>
    public DiscordConfig Discord { get; set; } = new();

    /// <summary>Steam integration configuration.</summary>
    public SteamConfig Steam { get; set; } = new();

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        // The API key is needed by the profile and ticket services; Steam sign-in (OpenID) works without one.
        if (Steam.Enabled && string.IsNullOrWhiteSpace(Steam.ApiKey) && !Steam.OpenIdEnabled)
            errors.Add("Steam API key is required when Steam is enabled (unless only Steam sign-in is used)");

        if (Steam.AuthEnabled && string.IsNullOrWhiteSpace(Steam.ApiKey))
            errors.Add("Steam API key is required when Steam authentication is enabled");

        if (!string.IsNullOrWhiteSpace(Discord.DefaultWebhookUrl)
            && !Services.Discord.DiscordWebhookService.IsValidWebhookUrl(Discord.DefaultWebhookUrl))
            errors.Add("Discord default webhook URL must be an https://discord.com/api/webhooks/{id}/{token} URL");

        if (Steam.AuthEnabled && string.IsNullOrWhiteSpace(Steam.AppId))
            errors.Add("Steam App ID is required when Steam authentication is enabled");

        if (Steam.CacheTtlSeconds <= 0)
            errors.Add("Steam cache TTL must be greater than 0");

        if (Steam.MaxCacheEntries <= 0)
            errors.Add("Steam MaxCacheEntries must be greater than 0");

        if (Steam.OpenIdNonceMaxAgeSeconds < 0)
            errors.Add("Steam OpenID max assertion age cannot be negative");

        if (!Steam.TryGetApiBaseUri(out _, out var apiBaseUrlError))
            errors.Add(apiBaseUrlError);

        if (!Steam.TryGetOpenIdRealm(out _, out var realmError))
            errors.Add(realmError);

        return errors.Any()
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Configuration for Discord webhook integration.
/// </summary>
public class DiscordConfig
{
    /// <summary>Whether Discord integration is enabled.</summary>
    [ConfigField(Label = "Discord Enabled", Group = "Discord", Order = 10)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default webhook URL used when no URL is specified in individual send calls.
    /// Format: <c>https://discord.com/api/webhooks/{id}/{token}</c>; any other URL fails validation
    /// (see <see cref="Services.Discord.DiscordWebhookService.IsValidWebhookUrl"/>).
    /// </summary>
    [ConfigField(Label = "Default Webhook URL", InputType = ConfigInputType.Url, Secret = true,
        Description = "https://discord.com/api/webhooks/{id}/{token}",
        Placeholder = "https://discord.com/api/webhooks/…", Group = "Discord", Order = 11)]
    public string DefaultWebhookUrl { get; set; } = string.Empty;

    /// <summary>HTTP request timeout in seconds for webhook calls.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 120,
        Group = "Discord", Order = 12, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Default username displayed for webhook messages (overrides Discord server setting).</summary>
    [ConfigField(Label = "Default Username", Description = "Shown as the sender on webhook messages.",
        Group = "Discord", Order = 13, Collapsed = true)]
    public string DefaultUsername { get; set; } = string.Empty;

    /// <summary>Default avatar URL for webhook messages.</summary>
    [ConfigField(Label = "Default Avatar URL", InputType = ConfigInputType.Url,
        Group = "Discord", Order = 14, Collapsed = true)]
    public string DefaultAvatarUrl { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for Steam Web API integration.
/// </summary>
public class SteamConfig
{
    /// <summary>Whether Steam integration is enabled.</summary>
    [ConfigField(Label = "Steam Enabled", Group = "Steam", Order = 20)]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Steam Web API key. Needed for the profile service (<c>Steam</c>) and ticket authentication; without it only
    /// Steam sign-in (OpenID) is available.
    /// Obtain from <c>https://steamcommunity.com/dev/apikey</c>.
    /// </summary>
    [ConfigField(Label = "Steam API Key", InputType = ConfigInputType.Password, Secret = true,
        Description = "Get one at https://steamcommunity.com/dev/apikey",
        Group = "Steam", Order = 21)]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Whether Steam authentication (ticket validation) is enabled.</summary>
    [ConfigField(Label = "Steam Auth Enabled", Description = "Validate Steam login tickets.",
        Group = "Steam", Order = 22)]
    public bool AuthEnabled { get; set; } = false;

    /// <summary>
    /// Steam App ID for authentication. Required when <see cref="AuthEnabled"/> is <c>true</c>.
    /// </summary>
    [ConfigField(Label = "Steam App ID", Placeholder = "480",
        Description = "Your game's Steam App ID. Required for auth.",
        Group = "Steam", Order = 23)]
    public string AppId { get; set; } = string.Empty;

    /// <summary>How long (in seconds) to cache Steam profile data. Default: 300 (5 minutes).</summary>
    [ConfigField(Label = "Cache TTL (s)", Min = 1,
        Description = "Profile data cache duration.",
        Group = "Steam", Order = 24, Collapsed = true)]
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>
    /// Most entries each of the profile caches (players, bans, games) keeps. Default: 10 000. When a cache is full,
    /// its oldest entries are evicted to make room; expired entries are dropped when read. Must be greater than 0.
    /// </summary>
    [ConfigField(Label = "Max cache entries", Min = 1,
        Description = "Upper bound per profile cache (players, bans, games); the oldest entries are evicted when full.",
        Group = "Steam", Order = 30, Collapsed = true)]
    public int MaxCacheEntries { get; set; } = 10_000;

    /// <summary>HTTP request timeout in seconds for Steam API calls.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 120,
        Group = "Steam", Order = 25, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Whether "Sign in through Steam" (OpenID 2.0) is available. Active when Steam is enabled;
    /// OpenID itself needs no API key, the key is for reading the player's profile afterwards.
    /// </summary>
    [ConfigField(Label = "Steam Sign-in (OpenID)", Description = "\"Sign in through Steam\" for websites.",
        Group = "Steam", Order = 27)]
    public bool OpenIdEnabled { get; set; } = true;

    /// <summary>
    /// Oldest Steam OpenID assertion accepted, in seconds, judged by the timestamp in its
    /// <c>openid.response_nonce</c>. Default: 300 (5 minutes). <c>0</c> turns the age check off (the nonce must
    /// still be well formed). An assertion stamped more than a minute in the future is always refused.
    /// </summary>
    [ConfigField(Label = "OpenID max assertion age (s)", Min = 0, Max = 3600,
        Description = "Refuse Steam sign-in callbacks older than this. 0 = no age limit.",
        Group = "Steam", Order = 28, Collapsed = true)]
    public int OpenIdNonceMaxAgeSeconds { get; set; } = 300;

    /// <summary>
    /// The site Steam sign-in belongs to, as an absolute http(s) URL (for example <c>https://example.com/</c>).
    /// When set, every <c>return_to</c> passed to <c>SteamOpenIdService.BuildLoginUrl</c> must lie inside it, so a
    /// return URL taken from user input cannot send the browser (and the signed assertion) to another site, and it
    /// is the realm Steam shows the user unless the call gives one. Empty (the default) keeps the realm derived
    /// from the return URL's scheme and host; set it whenever <c>return_to</c> is not a constant in your code.
    /// </summary>
    [ConfigField(Label = "OpenID realm", InputType = ConfigInputType.Url,
        Description = "Site URL every Steam sign-in return URL must lie inside (e.g. https://example.com/). Empty = derived from the return URL.",
        Placeholder = "https://example.com/", Group = "Steam", Order = 29)]
    public string OpenIdRealm { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the Steam Web API. Typically not changed. Must be an absolute <c>https</c> URL without user info:
    /// the API key travels in the query string of every request.
    /// </summary>
    [ConfigField(Label = "API Base URL", InputType = ConfigInputType.Url,
        Description = "Rarely changed. Must be https.",
        Group = "Steam", Order = 26, Collapsed = true)]
    public string ApiBaseUrl { get; set; } = "https://api.steampowered.com";

    /// <summary>
    /// Parses <see cref="ApiBaseUrl"/> into the base address the Steam services send requests to: an absolute
    /// <c>https</c> URL without user info, normalised to end in <c>/</c>. The API key is part of every request, so
    /// <c>http</c> (which would send it in clear) and embedded credentials are refused.
    /// </summary>
    /// <param name="uri">The base address, or <c>null</c> when the value is refused.</param>
    /// <param name="error">Why the value is refused, or empty.</param>
    /// <returns><c>true</c> when <see cref="ApiBaseUrl"/> is usable.</returns>
    public bool TryGetApiBaseUri([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri, out string error)
    {
        uri = null;
        var value = ApiBaseUrl?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Steam.ApiBaseUrl is required";
            return false;
        }
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var parsed))
        {
            error = "Steam.ApiBaseUrl must be an absolute URL";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Steam.ApiBaseUrl must use https (the Steam API key is sent to it)";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Steam.ApiBaseUrl must not contain user info";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Steam.ApiBaseUrl must not contain a query or fragment";
            return false;
        }

        uri = parsed;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Parses <see cref="OpenIdRealm"/>: empty gives <c>null</c> (no configured realm); otherwise it must be an
    /// absolute http(s) URL without user info, query or fragment.
    /// </summary>
    /// <param name="realm">The configured realm, or <c>null</c> when none is set.</param>
    /// <param name="error">Why the value is refused, or empty.</param>
    /// <returns><c>false</c> only when a non-empty value is not a usable realm.</returns>
    public bool TryGetOpenIdRealm(out Uri? realm, out string error)
    {
        realm = null;
        error = string.Empty;
        var value = OpenIdRealm?.Trim() ?? string.Empty;
        if (value.Length == 0) return true;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            error = "Steam.OpenIdRealm must be an absolute http or https URL";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Steam.OpenIdRealm must not contain user info, a query or a fragment";
            return false;
        }

        realm = parsed;
        return true;
    }
}
