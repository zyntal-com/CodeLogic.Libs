# CL.GameNetQuery — Changelog

All notable changes to **CodeLogic.GameNetQuery** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...).
- **`QueryResult` removed.** The class (`Success`, `Info?`, `Players`, `Error` string, `DurationMs`,
  `Ok`/`Fail`) is replaced by CodeLogic `Result<ServerQuery>`, where
  `ServerQuery(ServerInfo Info, IReadOnlyList<PlayerInfo> Players, long DurationMs)` is the success value
  and failures are `Error` kinds with `gamenetquery.*` codes.
- **Counter-Strike commands that take caller text return `Task<Result<string>>`** (was `Task<string>`), so an
  unsafe argument can be refused: `ChangeMapAsync`, `KickPlayerAsync`, `BanPlayerAsync`, `UnbanPlayerAsync`,
  `SetHostnameAsync`, `ExecConfigAsync`, `SetCvarAsync`, `GetCvarAsync`, `SayAsync` (both `CounterStrike2` and
  `CounterStrikeSource`) and `CounterStrike2.SlayPlayerAsync`. Read the response from `.Value` after checking
  `IsFailure`. Commands without a text argument (`RestartAsync`, `EnableCheatsAsync`, `SetFriendlyFireAsync(bool)`, ...) still return `Task<string>`.
- `BanPlayerAsync(string)` / `UnbanPlayerAsync(string)` take a user id or SteamID (`123`, `STEAM_1:0:123`,
  `[U:1:123]`, `7656119...`); `banid` / `removeid` never accepted names, and a name is now a validation error.
  The parameter is renamed `playerId`.
- `SetCvarAsync` quotes the value (`mp_roundtime "1.92"`), so values with spaces are sent as one argument.

### Security

- **RCON command injection in the Counter-Strike wrappers.** Caller values were spliced into the console line
  unchecked, so kicking a player named `x; rcon_password pwned` (names come straight from `GetStatusAsync`) ran a
  second command with full RCON rights. Every argument is now checked before anything is sent: free text
  (names, messages, hostnames, cvar values, kick reasons) may not contain `;`, `"`, CR, LF, NUL or any other
  control character; maps and configs must be relative paths of letters, digits and `_ - . /` without `..`;
  cvar names and ids must match their shape. A rejected value is a `ValidationError`
  `gamenetquery.invalid_rcon_argument`. `ValveRconClient.SendCommandAsync` / `MinecraftRconClient.SendCommandAsync`
  stay raw: never pass untrusted text to them.
- **A2S replies are bound to the queried server.** `ValveUdpQuery` connects its UDP socket to the server and
  discards datagrams from any other endpoint, so a third party can no longer answer a query in its place.
  `MinecraftQueryClient` also checks the reply's source and session id.

### Added

- `KickPlayerAsync(int userId, string? reason = null)` (`kickid`) and `BanPlayerAsync(int userId, int minutes = 0)`
  (`banid`) on both Counter-Strike wrappers: act on the user id, which hostile names cannot affect.
- `PlayerInfo.UserId` (`int?`): the user id from RCON `status` (`ValveStatusParser`, `ValveStatusParserCS2`,
  `GetStatusAsync`); `null` from A2S.
- `GameNetQueryErrorCodes`: every `gamenetquery.*` code in one place.

- `ValveUdpQuery.QueryAsync(ip, port, includePlayers = true, timeoutMs = 3000, cancellationToken)` →
  `Task<Result<ServerQuery>>`: A2S_INFO plus (optionally) A2S_PLAYER with the elapsed time. Failures:
  `ValidationError` `gamenetquery.invalid_address` / `gamenetquery.invalid_timeout`, `TimeoutError`
  `gamenetquery.timeout`, `UnavailableError` `gamenetquery.unreachable`, `InternalError`
  `gamenetquery.malformed_response` / `gamenetquery.query_failed`.

### Fixed

- A2S_INFO answers the server's `S2C_CHALLENGE` (sent by Source servers since 2020) instead of parsing
  the challenge packet as server info, and replies are checked for the expected A2S header. Applies to
  `GetServerInfoAsync` / `GetPlayerListAsync` too (a wrong packet now gives `null` / empty, not garbage).
  The UDP socket now matches the address family, so IPv6 addresses work.
- **`ValveRconClient.ConnectAsync` reported success for a wrong password.** It accepted the empty
  `SERVERDATA_RESPONSE_VALUE` that Source servers send before the real answer. It now waits for the
  `SERVERDATA_AUTH_RESPONSE`, requires the request id, and returns `false` for id `-1` (rejected password)
  or when no auth response arrives within `ResponseTimeout`.
- **RCON responses split across TCP reads were dropped.** Both `ValveRconClient` and `MinecraftRconClient`
  now frame packets exactly from a persistent buffer, so a packet split over several segments (or several
  packets in one segment) is reassembled, and a malformed size field closes the connection instead of
  desynchronising it.
- **Multi-packet responses are complete.** `ValveRconClient.SendCommandAsync` returned only the first
  response packet (about 4 KiB, which truncated `status`/`cvarlist` on busy servers). Both clients now send
  an end-of-response marker after each command (Valve: an empty `SERVERDATA_RESPONSE_VALUE`; Minecraft: a
  packet of unknown type 200, answered with "Unknown request") and return every body up to its reply,
  joined in order. Servers that don't answer the marker fall back to `MultiPacketIdleTimeout`.
- Each request uses a fresh id, so a late reply to a timed-out command is skipped instead of being
  returned as the answer to the next one. Calls on one client are serialised. Request bodies and responses
  are UTF-8 (Minecraft used ASCII).
- **`MinecraftQueryClient.QueryServer` never returned the documented keys.** It sent a basic-stat request
  (without the four padding bytes) but parsed the reply as a full stat, so `hostname`, `map`, `numplayers` and
  the rest never appeared. It now sends a full-stat request and parses the reply by its layout; the response
  also has a `players` line (names joined by `", "`), empty values are empty (were `(empty)`), text is decoded
  as UTF-8, and `QueryServer(string, ...)` returns an empty string for a non-IP address instead of throwing.
- `ServerInfo.Version` and `ServerInfo.Tags` are filled from A2S_INFO (version string and `sv_tags` keywords),
  and `CounterStrike2.GetStatusAsync` fills `Version`. `RawResponse` is the raw `status` text for RCON-parsed
  info and empty for A2S.
- A2S strings without a terminator, or starting past the end of the packet, are a
  `gamenetquery.malformed_response` instead of reading out of bounds; a short A2S_PLAYER challenge is too.

### Added

- `ValveRconClient` / `MinecraftRconClient`: `ResponseTimeout` (default 5 s, was a fixed poll of about
  5 s) and `MultiPacketIdleTimeout` (default 500 ms).

## 2026-10-24

### Changed

- Requires **CodeLogic 4.9+**. The official libraries move to the 4.9 line together with the framework,
  whose live config changes the stateful libraries now use. No API changes in this library.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Documentation

- Full rewrite of the README and the `docs/libs/gamenetquery.md` deep-dive to the current
  CodeLogic house style.
- Clarified that the library is zero-config and that the query clients (`ValveUdpQuery`,
  `ValveRconClient`, `CounterStrike2`/`CounterStrikeSource`, the status parsers, and the
  Minecraft clients) are used directly by namespace — the `GameNetQueryLibrary` instance is
  only needed for lifecycle/health integration.
- Documented that the query clients return model types directly (`ServerInfo?`,
  `List<PlayerInfo>`, response strings) rather than the framework `Result`, including the
  no-throw failure contracts.
- Added field tables for `ServerInfo` and `PlayerInfo`, method tables for both Counter-Strike
  wrappers (shared set vs. CS2-only extras), and the `ValveStatusParser` /
  `ValveStatusParserCS2` parser surfaces.
- No functional code changes.

## [4.5.2] — 2026-06-20

### Documentation

- Documented the `CounterStrike2` / `CounterStrikeSource` admin wrappers, including the
  CS2-only helpers (`UnbanPlayerAsync`, `SetHostnameAsync`, `ExecConfigAsync`, `GetCvarAsync`,
  `EnableCheatsAsync`/`DisableCheatsAsync`, `SetFriendlyFireAsync`, `SetTeamBalanceAsync`,
  `SlayPlayerAsync`, `GetStatusRawAsync`) and the shared command subset.
- Documented the `status` parsers — `ValveStatusParser` (`GetServerAddress`, `GetVersion`,
  `GetTags`, `GetPlayerList`, `ParseStatus`/`ParseStatusWithPlayers`) and `ValveStatusParserCS2`
  (`GetPlayerList`, `GetSpawngroups`).
- Documented the `ValveRconClient(IPAddress, …)` overload, the `ushort` port and `timeoutMs`
  parameters, and the no-throw failure contracts of the UDP/RCON clients.
- Documented the full `ServerInfo`, `PlayerInfo`, and `QueryResult` shapes (with `Ok`/`Fail`).
- Corrected the docs to match the API: `ServerInfo.Hostname` (not `Name`); `QueryResult.DurationMs`
  (not `LatencyMs`) with non-null `Players`; and `MinecraftQueryClient.QueryServer` is synchronous
  and returns key/value text read via `GetStatusValue` (not an awaited `QueryResult`).
- No functional code changes.

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

- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Valve A2S + Minecraft Server List Ping queries with
typed request/response models and full XML documentation.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.GameNetQuery).
