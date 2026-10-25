# CL.SocialConnect — Changelog

All notable changes to **CodeLogic.SocialConnect** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

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
