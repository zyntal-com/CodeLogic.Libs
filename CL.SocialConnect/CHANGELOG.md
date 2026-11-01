# CL.SocialConnect — Changelog

All notable changes to **CodeLogic.SocialConnect** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). Results can now be matched
  exhaustively with `switch`.
- `Auth.AuthenticateAsync` returns `Result<SteamTicketIdentity>` instead of `Result<SteamAuthResult>`.
  The mutable `SteamAuthResult` class (with `IsAuthenticated`, `ErrorDescription` and `0`/`1` ints for
  bans) is gone: the immutable `SteamTicketIdentity(SteamId, OwnerSteamId, AppId, VacBanned, PublisherBanned)`
  record (plus `IsFamilyShared`) is only ever a success, and every failure is an `Error` kind —
  `ValidationError` `social.steam.invalid_ticket` / `social.steam.no_appid`, `UnauthorizedError`
  `social.steam.auth_failed`, `TimeoutError` `social.steam.timeout`, `UnavailableError`
  `social.steam.network_error`, `InternalError` `social.steam.request_rejected` / `parse_error` / `error`.
- A ticket that is not hex is now refused locally with `social.steam.invalid_ticket` (it used to be
  pasted unescaped into the request URL). A 4xx from Steam (e.g. a bad API key) is now
  `InternalError` `social.steam.request_rejected` instead of `UnavailableError` `social.steam.network_error`.
- **Steam OpenID checks `openid.response_nonce`.** `VerifyAsync` never looked at the nonce, so an intercepted
  assertion stayed usable indefinitely. It now refuses, before asking Steam, a nonce without a valid
  `yyyy-MM-ddTHH:mm:ssZ` prefix or dated more than a minute ahead (`social.steam.openid_invalid`) and an
  assertion older than the new `SteamConfig.OpenIdNonceMaxAgeSeconds` (default 300; `0` turns the age check
  off) with `UnauthorizedError` `social.steam.openid_expired`. `SteamOpenIdResult` gains the required
  `ResponseNonce` and `NonceIssuedAtUtc`: record accepted nonces and refuse a repeat to stop replays inside
  the window. Code that constructs `SteamOpenIdResult` itself must set both.
- **Discord webhook URLs are restricted to Discord (SSRF).** `SendMessageAsync` / `SendEmbedAsync` / `SendAsync`
  posted to any URL they were given. Only `https` URLs on `discord.com` / `discordapp.com` (and their `ptb.` /
  `canary.` subdomains) with a `/api[/v{n}]/webhooks/{id}/{token}` path are accepted now; anything else fails
  with `ValidationError` `social.discord.invalid_url` before a request, and redirects are not followed. A
  `Discord.DefaultWebhookUrl` that is not such a URL fails configuration validation.
- **`WebhookSentEvent.WebhookUrl` no longer carries the webhook token.** It holds the redacted URL
  (`https://discord.com/api/webhooks/{id}/***`); the new `WebhookId` property holds the id.
- **Steam profile reads validate the Steam ID.** `GetPlayerAsync`, `GetPlayerBansAsync` and `GetOwnedGamesAsync`
  spliced `steamId` and the API key unescaped into the query string, so `"7656…&key=…"` could add or override
  parameters. A Steam ID that is not a SteamID64 (17 digits, `7656119…`) now fails with `ValidationError`
  `social.steam.invalid_steamid` before a request, and both values are URL-escaped.

### Added

- `DiscordWebhookService.IsValidWebhookUrl(url)` and `DiscordWebhookService.RedactWebhookUrl(url)`.
- `SteamIds.IsSteamId64(value)` (`CL.SocialConnect.Models.Steam`).
- `WebhookSentEvent.WebhookId`.

- `SteamAuthenticationService(SteamConfig, HttpMessageHandler, ILogger?, IEventBus?)` constructor for
  tests and outbound proxies, matching `SteamOpenIdService`.

### Fixed

- **Steam sign-in works without a Steam Web API key**, as documented. Validation demanded `ApiKey` whenever
  Steam was enabled, so OpenID could not be used on its own. With `Steam.Enabled` and `OpenIdEnabled` on and no
  key, the config is valid and only `OpenId` is created (`HasSteam` is `false`). Ticket authentication
  (`AuthEnabled`) now requires the key explicitly.
- **Live config changes are validated by the library too.** An invalid configuration that reaches
  `OnConfigChangedAsync` is logged and ignored, and the running services keep the last valid one; later changes
  are compared with the configuration actually running.
- `DiscordUser.GetAvatarUrl` no longer throws on a non-numeric discriminator, and escapes the id and avatar hash.

### Documentation

- The model reference matches the code: the Discord and Steam models are (non-sealed) classes with `List<>`
  collections, `DiscordEmbed.Timestamp` is a `DateTime?`, and the embed sub-models use `required` members.
  The examples use collection expressions and compile against the library.
- `DiscordUser` and `DiscordOAuthToken` are documented as data models for applications that run Discord OAuth2
  themselves; the library does not implement Discord OAuth2.
- Configuration examples in the README and the docs use the camelCase keys the CodeLogic loader reads.
  The PascalCase keys shown before are matched case-sensitively and were ignored, leaving defaults active.

## 2026-10-30

### Fixed

- **Health with every service switched off is Healthy** (`No services enabled`). It reported Unhealthy
  "Not initialized — no services active", although services are only ever absent because the config
  turns them off — an app that uses only some of the library (or none of it yet) showed as broken.

## 2026-10-26

### Added

- **Steam sign-in (OpenID 2.0)** for websites: `SteamOpenIdService`, exposed as `social.OpenId` with
  `HasSteamOpenId`. `BuildLoginUrl(returnTo, realm?)` builds the "Sign in through Steam" redirect;
  `VerifyAsync(query, expectedReturnTo)` checks the signed assertion locally (Steam's endpoint and
  namespace, exact `return_to`, `claimed_id` = `identity` as a SteamID64, required signed fields, no
  repeated parameters), then asks Steam to confirm it (`check_authentication`), and returns the
  player's 64-bit Steam ID as `Result<SteamOpenIdResult>`. Publishes `SteamOpenIdVerifiedEvent`.
- `Steam.OpenIdEnabled` (default `true`): sign-in is available whenever Steam is enabled (which
  requires `Steam.ApiKey`). It follows live config changes like the other Steam services: a Steam
  settings change replaces `social.OpenId`, and it can be switched on or off without a restart.

## 2026-10-24

### Added

- **Live config changes** (requires CodeLogic 4.9+). `SocialConnectLibrary` implements
  `IConfigReloadable`: a change to `config.socialconnect.json` made through `Libraries.UpdateConfigAsync`,
  `Libraries.ReloadConfigAsync` or the config file watcher is applied without a restart, rebuilding only
  the service whose settings changed (`Discord`, or `Steam` + `Auth`). Services and the library can be
  switched on or off. A replaced service is disposed after its request timeout, so running requests finish.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- Discord sends now validate the 2000-character content limit and 10-embed limit
  up front, returning a clear validation error instead of a failed HTTP
  round-trip.
- Corrected the `CommunityVisibilityState` documentation comment (the Steam Web
  API returns 1 = not visible / 3 = public to third parties; the previous
  comment listed nonexistent levels).

### Documentation

- Full rewrite of the README and the `docs/libs/socialconnect.md` guide to the
  current house style.
- README restructured to the standard layout (badges, tagline, intro, Install,
  Quick start, Features, Configuration with table + JSON, Documentation,
  Requirements, License) and now links the published docs page.
- Corrected the `Result` contract throughout: Discord methods return `Result`
  (no value); Steam profile and auth methods return `Result<T>`. Examples use
  `IsSuccess` / `IsFailure`, `.Value`, and `Error?.Message`.
- Documented the canonical load sequence (`LoadAsync` → `ConfigureAsync` →
  `StartAsync` → `Libraries.Get`) and the `HasDiscord` / `HasSteam` /
  `HasSteamAuth` guards (Steam disabled by default).
- Added a deep guide covering the three services, Discord webhook methods and
  full model reference (with a rich-embed example), the Steam profile/bans/games
  models and computed properties, Steam ticket authentication, caching
  (`CacheTtlSeconds`, `ClearCache`), configuration, events, and the health check.

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README Quick Start: the text-message helper is
  `Discord.SendMessageAsync(...)`, not `SendAsync(string, username:)`.
- Fixed the README configuration example to use PascalCase JSON keys and added the
  previously omitted `DefaultAvatarUrl`, `AuthEnabled`, `AppId`, and `ApiBaseUrl` fields.
- Documented previously undocumented user-facing API surface:
  - Discord `SendAsync(DiscordWebhookMessage)`, `DiscordAllowedMentions`
    (`.None` / `.All`), and the `Tts` flag.
  - Steam `SteamProfileService.ClearCache()` and the `GetOwnedGamesAsync`
    `includeAppInfo` parameter.
  - Per-call `appId` override on `Auth.AuthenticateAsync`.
  - `SteamPlayer` (`IsPublic`, `IsInGame`, `AccountCreated`), `SteamPlayerBans`
    (`HasAnyBan`), and `SteamGame` (`TotalPlaytime`, `RecentPlaytime`, `LastPlayed`,
    `GetIconUrl`) computed helpers; the `DiscordUser` model.
  - `HasDiscord` / `HasSteam` / `HasSteamAuth` availability properties and the
    `InvalidOperationException` thrown when a disabled service is accessed.
  - Event-bus events `WebhookSentEvent`, `SteamProfileFetchedEvent`, and
    `SteamAuthenticatedEvent`, and the `SocialError` failure-code enum.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated SocialConnect configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

### Fixed

- Closed resource leaks in the OAuth client + reworked the health check so
  it no longer mutates internal state as a side-effect.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. OAuth/OIDC providers (Google, Discord, Steam,
generic OIDC) with a uniform abstraction.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.SocialConnect).
