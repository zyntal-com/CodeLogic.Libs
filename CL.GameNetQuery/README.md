# CodeLogic.GameNetQuery

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GameNetQuery)](https://www.nuget.org/packages/CodeLogic.GameNetQuery)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Query and administer game servers from [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — Valve A2S over UDP, Source RCON, and Minecraft GameSpy4 / RCON.

Speaks the wire protocols used by Source-engine and Minecraft servers: A2S over UDP for live server info and player lists, the Source RCON TCP protocol for remote console commands (with high-level Counter-Strike 2 / Counter-Strike: Source admin wrappers), and Minecraft's GameSpy4 UDP query plus RCON. No external NuGet dependencies.

## Install

```bash
dotnet add package CodeLogic.GameNetQuery
```

## Quick start

The query clients are used **directly by namespace** — they don't need a library instance. Load the library only when you want its lifecycle and health-check integration.

```csharp
using CL.GameNetQuery.Valve;
using CL.GameNetQuery.Models;
using CodeLogic.Core.Results;

// A2S UDP — info + players + timing as a CodeLogic Result (no library load required)
Result<ServerQuery> query = await ValveUdpQuery.QueryAsync("203.0.113.10", 27015);
if (query.IsSuccess)
    Console.WriteLine($"{query.Value.Info.Hostname}: {query.Value.Players.Count} players in {query.Value.DurationMs} ms");
else
    Console.WriteLine($"{query.Error.Code}: {query.Error.Message}"); // e.g. gamenetquery.timeout

// Or the simple helpers: null / empty list on failure
ServerInfo? info = await ValveUdpQuery.GetServerInfoAsync("203.0.113.10", 27015);
if (info is { IsOnline: true })
    Console.WriteLine($"{info.Hostname} — {info.Map} ({info.PlayerCount}/{info.MaxPlayers})");

List<PlayerInfo> players = await ValveUdpQuery.GetPlayerListAsync("203.0.113.10", 27015);
foreach (var p in players)
    Console.WriteLine($"{p.Name}  score {p.Score}  {p.Ping}ms");

// Source RCON — remote console command
using var rcon = new ValveRconClient("203.0.113.10", 27015, "rcon-password");
if (await rcon.ConnectAsync())
    Console.WriteLine(await rcon.SendCommandAsync("status"));
```

To wire the library into CodeLogic's lifecycle (so it participates in health checks):

```csharp
using CL.GameNetQuery;

await Libraries.LoadAsync<GameNetQueryLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

## Features

- **Valve A2S UDP query** — `ValveUdpQuery.QueryAsync` returns `Result<ServerQuery>` (info, players, duration) with typed failures (`TimeoutError`, `UnavailableError`, ...); `GetServerInfoAsync` / `GetPlayerListAsync` return `ServerInfo?` and `List<PlayerInfo>`, with `null` / an empty list on failure.
- **Source RCON** — `ValveRconClient` connects over TCP and runs console commands verbatim, returning the raw server response string. Never build a raw command from untrusted text.
- **Counter-Strike admin wrappers** — `CounterStrike2` and `CounterStrikeSource` expose named helpers (change map, kick/ban by user id or name, say, cvars, restart…) over RCON; CS2 adds cheats, friendly-fire, team-balance, and slay. Arguments are validated before sending, so a player name like `x; rcon_password pwned` is refused with a `ValidationError` (`gamenetquery.invalid_rcon_argument`) instead of running a second command.
- **`status` parsers** — `ValveStatusParser` and `ValveStatusParserCS2` turn raw `status` output into `ServerInfo` / `PlayerInfo`.
- **Minecraft** — `MinecraftQueryClient` (GameSpy4 full-stat UDP query: `hostname`, `map`, `numplayers`, `players`, ... via `GetStatusValue`) and `MinecraftRconClient` (RCON).

## Configuration

None. `CL.GameNetQuery` is a zero-config library — it writes no config file. Every connection parameter (IP, port, RCON password, timeout) is passed directly to the query clients at call time.

## Documentation

Full guide: **[CL.GameNetQuery documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/gamenetquery.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- No external NuGet dependencies.

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
