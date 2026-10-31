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
    if (player.IsSuccess)
        Console.WriteLine($"{player.Value.PersonaName} — {player.Value.ProfileUrl}");
}

// Steam — "Sign in through Steam" on a website (OpenID 2.0; returns the Steam ID)
if (social.HasSteamOpenId)
{
    // 1. Redirect the browser. Keep the per-attempt state in a cookie.
    var returnTo = $"https://example.com/auth/steam/callback?state={state}";
    string loginUrl = social.OpenId.BuildLoginUrl(returnTo);

    // 2. On the callback, verify against the exact return_to you sent.
    Result<SteamOpenIdResult> signIn = await social.OpenId.VerifyAsync(callbackQueryString, returnTo);
    // 3. Refuse a nonce you have already accepted (replay); keep each one for OpenIdNonceMaxAgeSeconds.
    if (signIn.IsSuccess && seenNonces.TryAdd(signIn.Value.ResponseNonce, signIn.Value.NonceIssuedAtUtc))
        Console.WriteLine($"Signed in as {signIn.Value.SteamId}");
}
```

Accessing a disabled service throws `InvalidOperationException`. Guard with `HasDiscord` / `HasSteam` / `HasSteamAuth` / `HasSteamOpenId` when a service may be turned off — Steam is disabled by default.

## Features

- **Discord webhooks** — `SendMessageAsync` (plain text, max 2000 chars), `SendEmbedAsync` (up to 10 rich embeds), and `SendAsync` (full `DiscordWebhookMessage`); all return `Result`.
- **Rich embeds** — title, description, colour, fields, author, footer, image, thumbnail, and timestamp via `DiscordEmbed` / `DiscordEmbedField`.
- **Mention control** — `DiscordAllowedMentions` (with `.None` / `.All`) whitelists which roles/users/`@everyone` may ping; opt-in TTS via `DiscordWebhookMessage.Tts`.
- **Steam profiles** — `GetPlayerAsync`, `GetPlayerBansAsync`, `GetOwnedGamesAsync` return `Result<T>` with computed helpers (`IsPublic`, `IsInGame`, `HasAnyBan`, `TotalPlaytime`, `LastPlayed`).
- **Built-in cache** — Steam reads are cached for `CacheTtlSeconds` (default 300); clear on demand with `Steam.ClearCache()`.
- **Steam authentication** — `Auth.AuthenticateAsync` validates a session ticket (per-call `appId` override) and returns `Result<SteamTicketIdentity>`; a rejected ticket is an `UnauthorizedError`.
- **Steam sign-in** — `OpenId.BuildLoginUrl` / `OpenId.VerifyAsync` implement "Sign in through Steam" (OpenID 2.0): local checks of the signed assertion (endpoint, `return_to`, identity, signed fields, `response_nonce` age), then confirmation by Steam (`check_authentication`). Returns the 64-bit Steam ID and the assertion's `ResponseNonce` / `NonceIssuedAtUtc` for replay tracking.
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
    "cacheTtlSeconds": 300,
    "timeoutSeconds": 15,
    "apiBaseUrl": "https://api.steampowered.com"
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch for the whole library. |
| `Discord.Enabled` | `true` | Enable the Discord webhook service. |
| `Discord.DefaultWebhookUrl` | `""` | Webhook used when a call omits `webhookUrl` (secret). |
| `Discord.TimeoutSeconds` | `10` | HTTP timeout for Discord calls (1–120). |
| `Discord.DefaultUsername` | `""` | Default sender name applied to messages. |
| `Discord.DefaultAvatarUrl` | `""` | Default sender avatar applied to messages. |
| `Steam.Enabled` | `false` | Enable the Steam profile service; **requires `ApiKey`**. |
| `Steam.ApiKey` | `""` | Steam Web API key (secret) — get one at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey). |
| `Steam.AuthEnabled` | `false` | Enable ticket authentication; **requires `AppId`**. |
| `Steam.AppId` | `""` | Your game's Steam App ID (used by auth). |
| `Steam.OpenIdEnabled` | `true` | Enable "Sign in through Steam" (OpenID) while Steam is enabled. OpenID itself needs no key. |
| `Steam.OpenIdNonceMaxAgeSeconds` | `300` | Oldest sign-in assertion accepted, by its `response_nonce` timestamp (`social.steam.openid_expired`); `0` = no age limit. |
| `Steam.CacheTtlSeconds` | `300` | Profile cache lifetime in seconds (must be > 0). |
| `Steam.TimeoutSeconds` | `15` | HTTP timeout for Steam calls (1–120). |
| `Steam.ApiBaseUrl` | `https://api.steampowered.com` | Steam Web API base URL. |

### Changing config while running

With CodeLogic 4.9+, `config.socialconnect.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the service whose settings changed is rebuilt:

- **Discord settings**: `social.Discord` is replaced.
- **Steam settings**: `social.Steam`, `social.Auth` and `social.OpenId` are replaced; the profile cache starts empty.
- **Enabled flags**: each service, and the whole library, can be switched on or off.

A replaced service is disposed only after its request timeout, so requests already running on it finish. Read `social.Discord` / `social.Steam` / `social.Auth` / `social.OpenId` when you use them rather than keeping an old instance.

## Documentation

Full guide: **[CL.SocialConnect documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/socialconnect.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- No external NuGet dependencies

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
