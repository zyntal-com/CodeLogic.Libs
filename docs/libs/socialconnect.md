# CL.SocialConnect

> Discord webhooks and Steam integration — send rich Discord notifications, read Steam player profiles, bans, and game libraries, and add "Sign in through Steam" to a website.

`CL.SocialConnect` adds two third-party integrations to a CodeLogic application: outbound **Discord webhooks** and read access to the **Steam Web API**, plus **Steam ticket authentication** for game-server scenarios and **Steam sign-in** (OpenID 2.0) for websites. The library exposes a single `SocialConnectLibrary` surface with four services — `Discord`, `Steam`, `Auth`, and `OpenId` — each independently toggled by configuration. It has no external NuGet dependencies; HTTP is handled with `System.Net.Http.Json`.

| | |
|---|---|
| **Package** | [`CodeLogic.SocialConnect`](https://www.nuget.org/packages/CodeLogic.SocialConnect) |
| **Library class** | `CL.SocialConnect.SocialConnectLibrary` |
| **Config file** | `config.socialconnect.json` (section `socialconnect`) |
| **Dependencies** | None (uses `System.Net.Http.Json`) |

## Install & load

```bash
dotnet add package CodeLogic.SocialConnect
```

```csharp
using CL.SocialConnect;

await Libraries.LoadAsync<SocialConnectLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var social = Libraries.Get<SocialConnectLibrary>();
```

## The four services

The library surfaces four services. Each property throws `InvalidOperationException` if accessed while its service is disabled or uninitialized, so pair every access with the matching `Has*` guard when the service might be off.

| Property | Type | Guard | Default state |
|----------|------|-------|---------------|
| `Discord` | `DiscordWebhookService` | `HasDiscord` | enabled |
| `Steam` | `SteamProfileService` | `HasSteam` | **disabled** |
| `Auth` | `SteamAuthenticationService` | `HasSteamAuth` | **disabled** |
| `OpenId` | `SteamOpenIdService` | `HasSteamOpenId` | on while Steam is enabled |

```csharp
if (social.HasDiscord)
    await social.Discord.SendMessageAsync("hello");

if (social.HasSteam)
{
    Result<SteamPlayer> p = await social.Steam.GetPlayerAsync("76561198012345678");
}
```

Discord webhook methods return a plain `Result` (no value — they either delivered or they didn't). Steam profile and auth methods return `Result<T>`. Always check `IsSuccess` before reading `.Value`, and read `Error?.Message` on failure.

## Discord webhooks

`DiscordWebhookService` posts to Discord webhook URLs. Every method accepts an optional `webhookUrl`; when omitted it falls back to `Discord.DefaultWebhookUrl` from config. The configured `DefaultUsername` and `DefaultAvatarUrl` are applied automatically unless the message overrides them.

Only Discord webhook URLs are accepted, so a URL that reaches the service from user input cannot point the server at another host (SSRF). `DiscordWebhookService.IsValidWebhookUrl(url)` is the check: `https` on the default port, no user info or fragment, host `discord.com` or `discordapp.com` (or their `ptb.` / `canary.` subdomains), and path `/api/webhooks/{id}/{token}` or `/api/v{n}/webhooks/{id}/{token}`; a query such as `?wait=true` or `?thread_id=…` is allowed. Any other URL fails with `ValidationError` `social.discord.invalid_url` before a request is made, and a `Discord.DefaultWebhookUrl` that fails the check is a configuration error. Redirects are not followed. Logs and events only ever carry the redacted URL (`DiscordWebhookService.RedactWebhookUrl`, e.g. `https://discord.com/api/webhooks/123/***`).

### Plain text

`SendMessageAsync(content, webhookUrl?)` sends a plain-text message (max 2000 characters).

```csharp
Result r = await social.Discord.SendMessageAsync("Deployment finished ✅");
if (r.IsFailure)
    log.Warn(r.Error?.Message);

// Override the destination per call:
await social.Discord.SendMessageAsync("Alert!", "https://discord.com/api/webhooks/aaa/bbb");
```

### Embeds

`SendEmbedAsync(embeds, content?, webhookUrl?)` sends up to 10 rich embeds, optionally with leading text.

```csharp
var embed = new DiscordEmbed
{
    Title       = "Build #482",
    Description = "All checks passed.",
    Color       = 0x2ECC71,   // decimal int colour (green)
};

Result r = await social.Discord.SendEmbedAsync([embed]);
```

### Full payload

`SendAsync(message, webhookUrl?)` posts a complete `DiscordWebhookMessage`, giving you control over username, avatar, TTS, mentions, and multiple embeds in one call.

```csharp
var message = new DiscordWebhookMessage
{
    Content          = "Release notes:",
    Username         = "Release Bot",
    AvatarUrl        = "https://example.com/bot.png",
    Tts              = false,
    AllowedMentions  = DiscordAllowedMentions.None,   // suppress all pings
    Embeds           = [embed],
};

Result r = await social.Discord.SendAsync(message);
```

All three methods publish a `WebhookSentEvent` after the request completes.

### Discord model reference

```csharp
public class DiscordWebhookMessage
{
    public string? Content { get; set; }
    public string? Username { get; set; }
    public string? AvatarUrl { get; set; }
    public bool Tts { get; set; } = false;
    public List<DiscordEmbed>? Embeds { get; set; }
    public DiscordAllowedMentions? AllowedMentions { get; set; }
}

public class DiscordAllowedMentions
{
    public List<string>? Parse { get; set; }   // "roles" | "users" | "everyone"
    public List<string>? Roles { get; set; }
    public List<string>? Users { get; set; }

    public static DiscordAllowedMentions None { get; }   // ping nobody
    public static DiscordAllowedMentions All  { get; }   // allow all mentions
}

public class DiscordEmbed
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public int? Color { get; set; }                 // decimal int, e.g. 0xFF0000 (red)
    public DateTime? Timestamp { get; set; }        // use UTC
    public DiscordEmbedFooter? Footer { get; set; }
    public DiscordEmbedImage? Image { get; set; }
    public DiscordEmbedImage? Thumbnail { get; set; }
    public DiscordEmbedAuthor? Author { get; set; }
    public List<DiscordEmbedField>? Fields { get; set; }
}

public class DiscordEmbedField
{
    public required string Name { get; set; }
    public required string Value { get; set; }
    public bool Inline { get; set; } = false;
}

public class DiscordEmbedImage
{
    public required string Url { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public class DiscordEmbedAuthor
{
    public required string Name { get; set; }
    public string? Url { get; set; }
    public string? IconUrl { get; set; }
}

public class DiscordEmbedFooter
{
    public required string Text { get; set; }
    public string? IconUrl { get; set; }
}
```

`DiscordUser` and `DiscordOAuthToken` are data models only, for applications that run the Discord OAuth2 flow themselves and deserialize the `/oauth2/token` and `/users/@me` responses. The library does not implement Discord OAuth2.

> Embed `Color` is a **decimal integer**, not a CSS hex string. The hex literal `0xFF0000` is the cleanest way to write it in C#.

### A rich embed

```csharp
var embed = new DiscordEmbed
{
    Title       = "Server Status",
    Description = "Nightly maintenance complete.",
    Url         = "https://status.example.com",
    Color       = 0x5865F2,                       // Discord blurple
    Timestamp   = DateTime.UtcNow,
    Author      = new DiscordEmbedAuthor
    {
        Name    = "Ops",
        IconUrl = "https://example.com/ops.png",
    },
    Thumbnail   = new DiscordEmbedImage { Url = "https://example.com/icon.png" },
    Fields      =
    [
        new DiscordEmbedField { Name = "Region",  Value = "eu-west", Inline = true },
        new DiscordEmbedField { Name = "Uptime",  Value = "99.98%",  Inline = true },
        new DiscordEmbedField { Name = "Notes",   Value = "No incidents." },
    ],
    Footer      = new DiscordEmbedFooter { Text = "Reported automatically" },
};

await social.Discord.SendEmbedAsync([embed], content: "📣 Status update");
```

## Steam profiles

`SteamProfileService` reads the Steam Web API. It requires `Steam.Enabled = true` and a `Steam.ApiKey`; without the key only Steam sign-in is available and `HasSteam` is `false`. Results are cached for `CacheTtlSeconds` (see [Caching](#caching)).

Every method takes a SteamID64 of an individual account (17 digits starting `7656119`, the form Steam sign-in returns; `SteamIds.IsSteamId64` is the check). Anything else fails with `ValidationError` `social.steam.invalid_steamid` before a request is made, and the API key and Steam ID are URL-escaped in the request.

### Players

`GetPlayerAsync(steamId)` returns a `SteamPlayer` for a 64-bit Steam ID.

```csharp
Result<SteamPlayer> r = await social.Steam.GetPlayerAsync("76561198012345678");
if (r.IsSuccess)
{
    SteamPlayer p = r.Value;
    Console.WriteLine($"{p.PersonaName} ({(p.IsPublic ? "public" : "private")})");
    if (p.IsInGame)
        Console.WriteLine($"Playing: {p.GameExtraInfo}");
}
```

### Bans

`GetPlayerBansAsync(steamId)` returns a `SteamPlayerBans`.

```csharp
Result<SteamPlayerBans> r = await social.Steam.GetPlayerBansAsync("76561198012345678");
if (r.IsSuccess && r.Value.HasAnyBan)
    Console.WriteLine($"VAC bans: {r.Value.NumberOfVacBans}");
```

### Owned games

`GetOwnedGamesAsync(steamId, includeAppInfo = true)` returns a `List<SteamGame>`. With `includeAppInfo` set, each game carries its `Name` and icon; turn it off for a lighter response when you only need playtimes.

```csharp
Result<List<SteamGame>> r = await social.Steam.GetOwnedGamesAsync("76561198012345678");
if (r.IsSuccess)
{
    foreach (var game in r.Value.OrderByDescending(g => g.PlaytimeForever).Take(5))
        Console.WriteLine($"{game.Name}: {game.TotalPlaytime.TotalHours:F0}h");
}
```

Each profile read publishes a `SteamProfileFetchedEvent` carrying a `FromCache` flag.

### Steam model reference

```csharp
public class SteamPlayer
{
    public string SteamId { get; set; }
    public int CommunityVisibilityState { get; set; }   // 3 = public
    public int ProfileState { get; set; }
    public string PersonaName { get; set; }
    public string ProfileUrl { get; set; }
    public string Avatar { get; set; }          // 32px
    public string AvatarMedium { get; set; }    // 64px
    public string AvatarFull { get; set; }      // 184px
    public int PersonaState { get; set; }       // 0=Offline, 1=Online … 6=LookingToPlay
    public string? RealName { get; set; }
    public string? PrimaryClanId { get; set; }
    public long? TimeCreated { get; set; }       // unix seconds
    public string? GameId { get; set; }
    public string? GameExtraInfo { get; set; }
    public string? LocCountryCode { get; set; }
    public string? LocStateCode { get; set; }

    // Computed
    public bool IsPublic { get; }            // CommunityVisibilityState == 3
    public bool IsInGame { get; }            // GameId is set
    public DateTime? AccountCreated { get; } // from TimeCreated
}

public class SteamPlayerBans
{
    public string SteamId { get; set; }
    public bool CommunityBanned { get; set; }
    public bool VacBanned { get; set; }
    public int NumberOfVacBans { get; set; }
    public int DaysSinceLastBan { get; set; }
    public int NumberOfGameBans { get; set; }
    public string EconomyBan { get; set; }       // "none" | "probation" | "banned"

    // Computed
    public bool HasAnyBan { get; }
}

public class SteamGame
{
    public int AppId { get; set; }
    public string? Name { get; set; }            // requires includeAppInfo
    public int PlaytimeForever { get; set; }     // minutes
    public int? Playtime2Weeks { get; set; }     // minutes
    public string? ImgIconUrl { get; set; }
    public long? RtimeLastPlayed { get; set; }   // unix seconds

    // Computed
    public TimeSpan TotalPlaytime { get; }       // from PlaytimeForever
    public TimeSpan? RecentPlaytime { get; }     // from Playtime2Weeks
    public DateTime? LastPlayed { get; }         // from RtimeLastPlayed
    public string? GetIconUrl();                  // full CDN icon URL
}
```

## Steam authentication

`SteamAuthenticationService` validates a Steam session ticket — typically used by a game server to confirm a connecting client's identity. It requires `Steam.AuthEnabled = true`, a configured `Steam.AppId` and a `Steam.ApiKey`.

`AuthenticateAsync(ticket, appId?)` validates the hex-encoded ticket; the optional `appId` overrides `Steam.AppId` for that call. It returns `Result<SteamTicketIdentity>`: success always means Steam accepted the ticket.

```csharp
if (social.HasSteamAuth)
{
    Result<SteamTicketIdentity> r = await social.Auth.AuthenticateAsync(sessionTicket);
    if (r.IsSuccess)
        Console.WriteLine($"Verified {r.Value.SteamId}{(r.Value.VacBanned ? " (VAC banned)" : "")}");
    else if (r.Error is UnauthorizedError)
        Console.WriteLine("Ticket rejected");
}
```

| Failure | Error kind | Code |
|---------|------------|------|
| Ticket empty or not hex | `ValidationError` | `social.steam.invalid_ticket` |
| No App ID (argument or `Steam.AppId`) | `ValidationError` | `social.steam.no_appid` |
| Steam rejected the ticket (invalid, expired, forged, other app) | `UnauthorizedError` | `social.steam.auth_failed` (`Details` = Steam error code when given) |
| No answer within `TimeoutSeconds` | `TimeoutError` | `social.steam.timeout` |
| Steam unreachable or answered 5xx | `UnavailableError` | `social.steam.network_error` |
| Steam answered 4xx (e.g. bad API key) | `InternalError` | `social.steam.request_rejected` (`Details` = HTTP status) |
| Unreadable answer / anything else | `InternalError` | `social.steam.parse_error` / `social.steam.error` |

Each call publishes a `SteamAuthenticatedEvent`.

```csharp
public sealed record SteamTicketIdentity(
    string SteamId,
    string OwnerSteamId,     // license owner; differs from SteamId under Family Sharing
    string AppId,            // the App ID the ticket was validated against
    bool VacBanned,
    bool PublisherBanned)
{
    public bool IsFamilyShared { get; }
}
```

## Steam sign-in (OpenID)

`SteamOpenIdService` (`social.OpenId`) implements "Sign in through Steam" for websites with Steam's OpenID 2.0 provider. It is available whenever Steam is enabled and `Steam.OpenIdEnabled` is `true` (the default). OpenID itself needs no API key. Steam only returns the player's 64-bit Steam ID; use `social.Steam.GetPlayerAsync` afterwards for the name and avatar.

The flow has two steps:

1. **Redirect.** `BuildLoginUrl(returnTo, realm?)` returns the `https://steamcommunity.com/openid/login?…` URL. `returnTo` must be an absolute http(s) URL without a fragment. `realm` (what Steam shows the user) defaults to the scheme and host of `returnTo`, and `returnTo` must start with it.
2. **Callback.** Steam sends the browser back to `returnTo` with a signed assertion. `VerifyAsync(query, expectedReturnTo)` takes the raw query string or the decoded parameters, and returns `Result<SteamOpenIdResult>`.

```csharp
// Start: bind the attempt to this browser with a random state kept in a cookie.
var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var returnTo = $"https://example.com/auth/steam/callback?state={state}";
// …store state in an HttpOnly cookie, then redirect:
string loginUrl = social.OpenId.BuildLoginUrl(returnTo);

// Callback: rebuild the exact return_to from the cookie's state, then verify.
Result<SteamOpenIdResult> result = await social.OpenId.VerifyAsync(request.QueryString, returnTo);
if (result.IsSuccess)
    SignIn(result.Value.SteamId);
```

`VerifyAsync` checks the assertion locally first, then asks Steam to confirm it (`check_authentication`, only an exact `is_valid:true` line counts). The local checks are:

- `openid.mode` is `id_res` (`cancel` gives `social.steam.openid_cancelled`).
- The namespace and `op_endpoint` are Steam's.
- `return_to` equals `expectedReturnTo` exactly.
- `claimed_id` equals `identity` and is `https://steamcommunity.com/openid/id/7656119…` (a SteamID64).
- `op_endpoint`, `claimed_id`, `identity`, `return_to` and `response_nonce` are among the signed fields, and a signature is present.
- No `openid.*` parameter appears twice.
- `response_nonce` starts with a `yyyy-MM-ddTHH:mm:ssZ` timestamp that is no more than a minute ahead and no older than `Steam.OpenIdNonceMaxAgeSeconds` (default 300; `0` turns the age check off).

Because Steam signs `return_to`, putting a per-attempt state in it and checking it against a cookie stops a callback taken from someone else's login from being replayed into another browser. To stop the same callback being replayed into the same browser within the age window, record each accepted `ResponseNonce` until `NonceIssuedAtUtc + OpenIdNonceMaxAgeSeconds` and refuse a repeat.

| Error code | Meaning |
|------------|---------|
| `social.steam.openid_cancelled` | The user cancelled at Steam. |
| `social.steam.openid_return_to` | `return_to` does not match this sign-in. |
| `social.steam.openid_identity` | Missing or malformed Steam identity. |
| `social.steam.openid_invalid` | Not a valid Steam assertion (mode, endpoint, signed fields, repeated parameters, malformed or future nonce). |
| `social.steam.openid_expired` | The assertion is older than `Steam.OpenIdNonceMaxAgeSeconds`. |
| `social.steam.openid_rejected` | Steam did not confirm the assertion. |
| `social.steam.openid_unavailable` / `social.steam.openid_timeout` | Steam could not be reached. |

Each verification publishes a `SteamOpenIdVerifiedEvent`.

```csharp
public sealed class SteamOpenIdResult
{
    public required string SteamId { get; init; }    // "76561197960435530"
    public required string ClaimedId { get; init; }  // "https://steamcommunity.com/openid/id/76561197960435530"
    public required string ResponseNonce { get; init; }       // "2026-10-31T12:00:00Z…", unique per assertion
    public required DateTime NonceIssuedAtUtc { get; init; }  // the nonce's timestamp
}
```

## Caching

`SteamProfileService` caches the results of `GetPlayerAsync`, `GetPlayerBansAsync`, and `GetOwnedGamesAsync` for `CacheTtlSeconds` (default 300). Cached reads publish `SteamProfileFetchedEvent` with `FromCache = true`. Call `Steam.ClearCache()` to drop all cached entries — useful after you know a profile has changed, or in tests.

```csharp
social.Steam.ClearCache();
```

## Configuration

The library writes `config.socialconnect.json` (section `socialconnect`) with defaults on first run.

```json
{
  "enabled": true,
  "discord": {
    "enabled": true,
    "defaultWebhookUrl": "https://discord.com/api/webhooks/xxx/yyy",
    "timeoutSeconds": 10,
    "defaultUsername": "MyApp",
    "defaultAvatarUrl": "https://example.com/bot.png"
  },
  "steam": {
    "enabled": false,
    "apiKey": "your-steam-web-api-key",
    "authEnabled": false,
    "appId": "",
    "openIdEnabled": true,
    "openIdNonceMaxAgeSeconds": 300,
    "cacheTtlSeconds": 300,
    "timeoutSeconds": 15,
    "apiBaseUrl": "https://api.steampowered.com"
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch for the whole library. |
| `Discord.Enabled` | `bool` | `true` | Create the Discord webhook service. |
| `Discord.DefaultWebhookUrl` | `string` | `""` | Webhook used when a call omits `webhookUrl`. Secret. Must be a Discord webhook URL (see above). |
| `Discord.TimeoutSeconds` | `int` | `10` | HTTP timeout for Discord requests (1–120). |
| `Discord.DefaultUsername` | `string` | `""` | Default sender name applied to outgoing messages. |
| `Discord.DefaultAvatarUrl` | `string` | `""` | Default sender avatar applied to outgoing messages. |
| `Steam.Enabled` | `bool` | `false` | Turn the Steam services on. The profile service needs `ApiKey`; sign-in alone does not. |
| `Steam.ApiKey` | `string` | `""` | Steam Web API key (secret). Get one at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey). |
| `Steam.AuthEnabled` | `bool` | `false` | Create the Steam auth service. Requires `AppId` and `ApiKey`. |
| `Steam.AppId` | `string` | `""` | Your game's Steam App ID (used by ticket auth). |
| `Steam.OpenIdEnabled` | `bool` | `true` | Create the Steam sign-in (OpenID) service while Steam is enabled. OpenID itself needs no key. |
| `Steam.OpenIdNonceMaxAgeSeconds` | `int` | `300` | Oldest sign-in assertion accepted, by its `response_nonce` timestamp. `0` = no age limit; negative is a config error. |
| `Steam.CacheTtlSeconds` | `int` | `300` | Profile cache lifetime in seconds. Must be > 0. |
| `Steam.TimeoutSeconds` | `int` | `15` | HTTP timeout for Steam requests (1–120). |
| `Steam.ApiBaseUrl` | `string` | `https://api.steampowered.com` | Steam Web API base URL. |

**Validation** — `Steam.Enabled` requires a non-empty `ApiKey` unless `OpenIdEnabled` is on (sign-in only: no profile service); `Steam.AuthEnabled` requires a non-empty `AppId` and `ApiKey`; `CacheTtlSeconds` must be greater than 0; a non-empty `Discord.DefaultWebhookUrl` must be a Discord webhook URL. Steam (and Steam auth) are disabled by default.

### Changing config while running

Config changes apply live: `config.socialconnect.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the service whose settings changed is rebuilt:

- **Discord settings**: `social.Discord` is replaced.
- **Steam settings**: `social.Steam`, `social.Auth` and `social.OpenId` are replaced; the profile cache starts empty.
- **Enabled flags**: each service, and the whole library, can be switched on or off.

A replaced service is disposed only after its request timeout, so requests already running on it finish. Read `social.Discord` / `social.Steam` / `social.Auth` / `social.OpenId` when you use them rather than keeping an old instance. A configuration that fails validation is logged and ignored; the services keep running on the last valid one.

## Events

All events implement `IEvent` and are published to the CodeLogic event bus.

| Event | Published when | Payload |
|-------|----------------|---------|
| `WebhookSentEvent` | A Discord webhook request completes. | `WebhookUrl` (token redacted: `…/webhooks/{id}/***`), `WebhookId`, `Success`, `SentAt`, `StatusCode?`, `ErrorMessage?` |
| `SteamProfileFetchedEvent` | A Steam profile/bans/games read completes. | `SteamId`, `FetchedAt`, `FromCache` (default `false`) |
| `SteamAuthenticatedEvent` | A ticket authentication completes. | `SteamId`, `Success`, `AuthenticatedAt`, `AppId?`, `ErrorMessage?` |
| `SteamOpenIdVerifiedEvent` | A Steam sign-in callback is verified or refused. | `SteamId`, `Success`, `VerifiedAt`, `ErrorMessage?` |

## Health check

`HealthCheckAsync()` reports a `HealthStatus` listing the active services (`Active services: Discord, Steam, SteamOpenId`). Services are only absent when their config switches them off, so an app with every service off is Healthy (`No services enabled`); only a call before initialization is Unhealthy.

```csharp
var status = await social.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.SocialConnect)
