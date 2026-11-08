# CodeLogic.SocialConnect

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect)](https://www.nuget.org/packages/CodeLogic.SocialConnect)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Discord webhooks and Steam integration for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — send rich Discord notifications, read Steam player profiles, bans, and game libraries, and add "Sign in through Steam" to a website.

Four services behind one library: a **Discord webhook** sender (text, rich embeds, full payloads), a **Steam profile** reader (players, bans, owned games — cached), **Steam authentication** for validating game-session tickets, and **Steam sign-in** (OpenID 2.0) for websites. No external NuGet dependencies — built on `System.Net.Http.Json`.

## Install

```bash
dotnet add package CodeLogic.SocialConnect
```

## Quick start

```csharp
using CL.SocialConnect;

await Libraries.LoadAsync<SocialConnectLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var social = Libraries.Get<SocialConnectLibrary>();

// Discord — send a webhook text message (Result, no value)
Result sent = await social.Discord.SendMessageAsync("Server restarted");
if (sent.IsFailure)
    Console.WriteLine(sent.Error?.Message);

// Steam — fetch a player profile (Result<SteamPlayer>)
if (social.HasSteam)
{
    Result<SteamPlayer> player = await social.Steam.GetPlayerAsync("76561198012345678");
    if (player is SteamPlayer p)
        Console.WriteLine($"{p.PersonaName} — {p.ProfileUrl}");
}

// Steam — "Sign in through Steam" on a website (OpenID 2.0; returns the Steam ID)
if (social.HasSteamOpenId)
{
    // 1. Start: a random per-attempt state goes into return_to AND into an HttpOnly cookie, then redirect.
    var state = SteamOpenIdService.CreateState();
    response.Cookies.Append("steam_state", state, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax });
    string loginUrl = social.OpenId.BuildLoginUrl($"https://example.com/auth/steam/callback?state={state}");

    // 2. Callback: read the state back from the cookie, rebuild the exact return_to, verify.
    //    A missing cookie, or one that differs from the ?state= Steam echoes, means this browser never started the login.
    var expectedState = request.Cookies["steam_state"];
    if (expectedState is null || expectedState != request.Query["state"]) return Unauthorized();
    var returnTo = $"https://example.com/auth/steam/callback?state={expectedState}";
    Result<SteamOpenIdResult> signIn = await social.OpenId.VerifyAsync(request.QueryString.Value, returnTo);
    response.Cookies.Delete("steam_state");
    if (signIn is SteamOpenIdResult user)
        Console.WriteLine($"Signed in as {user.SteamId}");   // a replayed assertion fails with social.steam.openid_replayed
}
```

Set `Steam.OpenIdRealm` (for example `https://example.com/`) so a `return_to` built from user input cannot send the browser elsewhere.

Accessing a disabled service throws `InvalidOperationException`. Guard with `HasDiscord` / `HasSteam` / `HasSteamAuth` / `HasSteamOpenId` when a service may be turned off — Steam is disabled by default.

## Features

- **Discord webhooks** — `SendMessageAsync` (plain text, max 2000 chars), `SendEmbedAsync` (up to 10 rich embeds), and `SendAsync` (full `DiscordWebhookMessage`); all return `Result`. Only `https://discord.com/api/webhooks/{id}/{token}` URLs (and `discordapp.com`, `ptb.`/`canary.`) are accepted (`IsValidWebhookUrl`); logs and events carry the URL with the token redacted.
- **Rich embeds** — title, description, colour, fields, author, footer, image, thumbnail, and timestamp via `DiscordEmbed` / `DiscordEmbedField`.
- **Mention control** — `DiscordAllowedMentions` (with `.None` / `.All`) whitelists which roles/users/`@everyone` may ping; opt-in TTS via `DiscordWebhookMessage.Tts`.
- **Steam profiles** — `GetPlayerAsync`, `GetPlayerBansAsync`, `GetOwnedGamesAsync` return `Result<T>` with computed helpers (`IsPublic`, `IsInGame`, `HasAnyBan`, `TotalPlaytime`, `LastPlayed`). Steam IDs must be SteamID64s (`SteamIds.IsSteamId64`), otherwise `social.steam.invalid_steamid`.
- **Built-in cache** — Steam reads are cached for `CacheTtlSeconds` (default 300), at most `MaxCacheEntries` (default 10 000) per cache with the oldest evicted when full; clear on demand with `Steam.ClearCache()`.
- **Steam authentication** — `Auth.AuthenticateAsync` validates a session ticket (per-call `appId` override) and returns `Result<SteamTicketIdentity>`; a rejected ticket is an `UnauthorizedError`.
- **Steam sign-in** — `OpenId.BuildLoginUrl` / `OpenId.VerifyAsync` implement "Sign in through Steam" (OpenID 2.0): local checks of the signed assertion (endpoint, `return_to`, identity, signed fields, `response_nonce` age), a replay check against an `IOpenIdNonceStore` (`social.steam.openid_replayed`), then confirmation by Steam (`check_authentication`). `OpenIdRealm` pins `return_to` to your site; `CreateState()` gives the per-attempt state.
- **Cancellation** — every network call takes a `CancellationToken` (last parameter); a cancelled call is an `UnavailableError` (`social.steam.cancelled` / `social.discord.cancelled`), never an exception.
- **Testable** — every service has a constructor taking an `HttpMessageHandler` (fakes, outbound proxies).
- **Events** — `WebhookSentEvent`, `SteamProfileFetchedEvent`, `SteamAuthenticatedEvent`, and `SteamOpenIdVerifiedEvent` on the CodeLogic event bus.

## Configuration

Auto-generated on first run as `config.socialconnect.json`:

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
    "openIdRealm": "https://example.com/",
    "cacheTtlSeconds": 300,
    "maxCacheEntries": 10000,
    "timeoutSeconds": 15,
    "apiBaseUrl": "https://api.steampowered.com"
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch for the whole library. |
| `Discord.Enabled` | `true` | Enable the Discord webhook service. |
| `Discord.DefaultWebhookUrl` | `""` | Webhook used when a call omits `webhookUrl` (secret); must be a Discord webhook URL. |
| `Discord.TimeoutSeconds` | `10` | HTTP timeout for Discord calls (1–120). |
| `Discord.DefaultUsername` | `""` | Default sender name for `SendMessageAsync` / `SendEmbedAsync`. |
| `Discord.DefaultAvatarUrl` | `""` | Default sender avatar for `SendMessageAsync` / `SendEmbedAsync`. |
| `Steam.Enabled` | `false` | Enable the Steam services. The profile service **requires `ApiKey`**; sign-in (OpenID) alone does not. |
| `Steam.ApiKey` | `""` | Steam Web API key (secret) — get one at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey). |
| `Steam.AuthEnabled` | `false` | Enable ticket authentication; **requires `AppId` and `ApiKey`**. |
| `Steam.AppId` | `""` | Your game's Steam App ID (used by auth). |
| `Steam.OpenIdEnabled` | `true` | Enable "Sign in through Steam" (OpenID) while Steam is enabled. OpenID itself needs no key. |
| `Steam.OpenIdNonceMaxAgeSeconds` | `300` | Oldest sign-in assertion accepted, by its `response_nonce` timestamp (`social.steam.openid_expired`); `0` = no age limit. |
| `Steam.OpenIdRealm` | `""` | Site every sign-in `return_to` must lie inside (absolute http(s) URL). Empty = derived from the return URL; set it unless `return_to` is a constant. |
| `Steam.CacheTtlSeconds` | `300` | Profile cache lifetime in seconds (must be > 0). |
| `Steam.MaxCacheEntries` | `10000` | Upper bound per profile cache (players, bans, games); the oldest entries are evicted when full. |
| `Steam.TimeoutSeconds` | `15` | HTTP timeout for Steam calls (1–120). |
| `Steam.ApiBaseUrl` | `https://api.steampowered.com` | Steam Web API base URL; must be `https` without user info (the API key is sent to it). |

### Changing config while running

`config.socialconnect.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the service whose settings changed is rebuilt:

- **Discord settings**: `social.Discord` is replaced.
- **Steam settings**: `social.Steam`, `social.Auth` and `social.OpenId` are replaced; the profile cache starts empty.
- **Enabled flags**: each service, and the whole library, can be switched on or off.

A replaced service is disposed only after its request timeout, so requests already running on it finish. Read `social.Discord` / `social.Steam` / `social.Auth` / `social.OpenId` when you use them rather than keeping an old instance. An invalid configuration is logged and ignored; the last valid one stays active.

## Documentation

Full guide: **[CL.SocialConnect documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/socialconnect.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- No external NuGet dependencies

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
