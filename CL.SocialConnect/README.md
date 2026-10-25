# CodeLogic.SocialConnect

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect)](https://www.nuget.org/packages/CodeLogic.SocialConnect)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Discord webhooks and Steam Web API integration for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — send rich Discord notifications and read Steam player profiles, bans, and game libraries.

Three services behind one library: a **Discord webhook** sender (text, rich embeds, full payloads), a **Steam profile** reader (players, bans, owned games — cached), and **Steam authentication** for validating game-session tickets. No external NuGet dependencies — built on `System.Net.Http.Json`.

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
```

Accessing a disabled service throws `InvalidOperationException`. Guard with `HasDiscord` / `HasSteam` / `HasSteamAuth` when a service may be turned off — Steam is disabled by default.

## Features

- **Discord webhooks** — `SendMessageAsync` (plain text, max 2000 chars), `SendEmbedAsync` (up to 10 rich embeds), and `SendAsync` (full `DiscordWebhookMessage`); all return `Result`.
- **Rich embeds** — title, description, colour, fields, author, footer, image, thumbnail, and timestamp via `DiscordEmbed` / `DiscordEmbedField`.
- **Mention control** — `DiscordAllowedMentions` (with `.None` / `.All`) whitelists which roles/users/`@everyone` may ping; opt-in TTS via `DiscordWebhookMessage.Tts`.
- **Steam profiles** — `GetPlayerAsync`, `GetPlayerBansAsync`, `GetOwnedGamesAsync` return `Result<T>` with computed helpers (`IsPublic`, `IsInGame`, `HasAnyBan`, `TotalPlaytime`, `LastPlayed`).
- **Built-in cache** — Steam reads are cached for `CacheTtlSeconds` (default 300); clear on demand with `Steam.ClearCache()`.
- **Steam authentication** — `Auth.AuthenticateAsync` validates a session ticket, with a per-call `appId` override.
- **Events** — `WebhookSentEvent`, `SteamProfileFetchedEvent`, and `SteamAuthenticatedEvent` on the CodeLogic event bus.

## Configuration

Auto-generated on first run as `config.socialconnect.json`:

```json
{
  "Enabled": true,
  "Discord": {
    "Enabled": true,
    "DefaultWebhookUrl": "https://discord.com/api/webhooks/xxx/yyy",
    "TimeoutSeconds": 10,
    "DefaultUsername": "MyApp",
    "DefaultAvatarUrl": "https://example.com/bot.png"
  },
  "Steam": {
    "Enabled": false,
    "ApiKey": "your-steam-web-api-key",
    "AuthEnabled": false,
    "AppId": "",
    "CacheTtlSeconds": 300,
    "TimeoutSeconds": 15,
    "ApiBaseUrl": "https://api.steampowered.com"
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
| `Steam.CacheTtlSeconds` | `300` | Profile cache lifetime in seconds (must be > 0). |
| `Steam.TimeoutSeconds` | `15` | HTTP timeout for Steam calls (1–120). |
| `Steam.ApiBaseUrl` | `https://api.steampowered.com` | Steam Web API base URL. |

### Changing config while running

With CodeLogic 4.9+, `config.socialconnect.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the service whose settings changed is rebuilt:

- **Discord settings**: `social.Discord` is replaced.
- **Steam settings**: `social.Steam` and `social.Auth` are replaced; the profile cache starts empty.
- **Enabled flags**: each service, and the whole library, can be switched on or off.

A replaced service is disposed only after its request timeout, so requests already running on it finish. Read `social.Discord` / `social.Steam` / `social.Auth` when you use them rather than keeping an old instance.

## Documentation

Full guide: **[CL.SocialConnect documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/socialconnect.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- No external NuGet dependencies

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
