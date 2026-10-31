# CL.GameNetQuery

> Query and administer game servers — Valve A2S over UDP, Source RCON, high-level Counter-Strike admin wrappers, and Minecraft GameSpy4 / RCON.

`CL.GameNetQuery` speaks the wire protocols used by Source-engine and Minecraft servers. A2S_INFO and A2S_PLAYER run over UDP with no credentials; authenticated control runs over the Source RCON protocol — exposed raw (`ValveRconClient`, `MinecraftRconClient`) and through typed admin wrappers for Counter-Strike 2 and Counter-Strike: Source. Minecraft status is read via the GameSpy4 UDP query protocol. The library has **no external NuGet dependencies** and **no configuration file**.

| | |
|---|---|
| **Package** | [`CodeLogic.GameNetQuery`](https://www.nuget.org/packages/CodeLogic.GameNetQuery) |
| **Library class** | `CL.GameNetQuery.GameNetQueryLibrary` |
| **Config file** | None (zero-config) |
| **Dependencies** | None |

## Install & load

```bash
dotnet add package CodeLogic.GameNetQuery
```

The query clients are **used directly by namespace** — they are static helpers or standalone classes that take their connection parameters at call time, so they need no library instance. Load the library only when you want it to participate in CodeLogic's lifecycle and health checks:

```csharp
using CL.GameNetQuery;

await Libraries.LoadAsync<GameNetQueryLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

## Zero-config — clients are used directly

There is no `Libraries.Get<…>()` step for querying. Once the package is referenced, import the relevant namespace and call the client:

```csharp
using CL.GameNetQuery.Valve;          // ValveUdpQuery, ValveRconClient, status parsers
using CL.GameNetQuery.Valve.Games;    // CounterStrike2, CounterStrikeSource
using CL.GameNetQuery.Minecraft;      // MinecraftQueryClient, MinecraftRconClient
using CL.GameNetQuery.Models;         // ServerInfo, PlayerInfo
```

`ValveUdpQuery.QueryAsync` returns a CodeLogic `Result<ServerQuery>`, so you learn *why* a query failed. The older helpers return their **model types directly** — `ServerInfo?`, `List<PlayerInfo>`, and raw response strings — and surface failures as `null` / empty collections (UDP queries) or `false` from `ConnectAsync` (RCON).

## Valve A2S (UDP queries)

`ValveUdpQuery` is a static helper. The port is a `ushort` and every method takes an optional `timeoutMs` (default `3000`, per request).

`QueryAsync(ip, port, includePlayers = true, timeoutMs = 3000, cancellationToken)` sends A2S_INFO (answering the server's challenge if it asks for one) and, unless `includePlayers` is `false`, A2S_PLAYER. It returns `Result<ServerQuery>`:

```csharp
using CL.GameNetQuery.Valve;
using CL.GameNetQuery.Models;
using CodeLogic.Core.Results;

Result<ServerQuery> r = await ValveUdpQuery.QueryAsync("192.168.1.100", 27015);
if (r.IsSuccess)
{
    var (info, players, ms) = r.Value;
    Console.WriteLine($"{info.Hostname} on {info.Map} ({info.PlayerCount}/{info.MaxPlayers}) in {ms} ms");
}
else if (r.Error is TimeoutError)
    Console.WriteLine("Server did not answer");
```

```csharp
public sealed record ServerQuery(ServerInfo Info, IReadOnlyList<PlayerInfo> Players, long DurationMs);
```

| Failure | Error kind | Code |
|---------|------------|------|
| `ip` is not an IP address | `ValidationError` | `gamenetquery.invalid_address` |
| `timeoutMs` ≤ 0 | `ValidationError` | `gamenetquery.invalid_timeout` |
| No answer within `timeoutMs` | `TimeoutError` | `gamenetquery.timeout` |
| Socket error (e.g. ICMP port unreachable) | `UnavailableError` | `gamenetquery.unreachable` (`Details` = `SocketError`) |
| Answer is not a valid A2S response | `InternalError` | `gamenetquery.malformed_response` |
| Anything else | `InternalError` | `gamenetquery.query_failed` |

Cancelling `cancellationToken` throws `OperationCanceledException` rather than returning a failure.

`GetServerInfoAsync` and `GetPlayerListAsync` do one request each and return `null` / an empty list on failure — neither throws.

```csharp
using CL.GameNetQuery.Valve;
using CL.GameNetQuery.Models;

ServerInfo? info = await ValveUdpQuery.GetServerInfoAsync("192.168.1.100", 27015, timeoutMs: 3000);
if (info is not null)
    Console.WriteLine($"{info.Hostname} - {info.Map} ({info.PlayerCount}/{info.MaxPlayers})");

List<PlayerInfo> players = await ValveUdpQuery.GetPlayerListAsync("192.168.1.100", 27015);
foreach (var p in players)
    Console.WriteLine($"  {p.Name} - score {p.Score} - {p.Duration:F0}s connected");
```

| Method | Returns | On failure |
|--------|---------|-----------|
| `QueryAsync(string ip, ushort port, bool includePlayers = true, int timeoutMs = 3000, CancellationToken ct = default)` | `Task<Result<ServerQuery>>` | an `Error` kind (table above) |
| `GetServerInfoAsync(string ip, ushort port, int timeoutMs = 3000)` | `Task<ServerInfo?>` | `null` |
| `GetPlayerListAsync(string ip, ushort port, int timeoutMs = 3000)` | `Task<List<PlayerInfo>>` | empty list |

### ServerInfo

`ServerInfo` (record, `CL.GameNetQuery.Models`):

| Field | Type | Notes |
|-------|------|-------|
| `Hostname` | `string` | Server name |
| `Map` | `string` | Current map |
| `PlayerCount` | `int` | Connected players (humans) |
| `MaxPlayers` | `int` | Slot limit |
| `BotCount` | `int` | Bots present |
| `Version` | `string` | Server/game version |
| `Ip` | `string` | Queried IP |
| `Port` | `ushort` | Queried port |
| `IsOnline` | `bool` | `true` when the query succeeded |
| `Tags` | `IReadOnlyList<string>` | `sv_tags` entries |
| `RawResponse` | `string` | Unparsed A2S payload |
| `QueriedUtc` | `DateTime` | When the query ran |

### PlayerInfo

`PlayerInfo` (record, `CL.GameNetQuery.Models`):

| Field | Type | Notes |
|-------|------|-------|
| `Name` | `string` | Display name |
| `UniqueId` | `string` | SteamID / UUID |
| `Score` | `int` | In-game score |
| `Duration` | `float` | Seconds connected |
| `Ping` | `int` | Latency, ms |
| `Loss` | `int` | Packet loss, % |
| `State` | `string` | Connection state |
| `IsBot` | `bool` | Bot flag |

> The 4.x `QueryResult` class (`Success`, `Info?`, `Players`, `Error` string, `DurationMs`, `Ok`/`Fail`) was removed in 5.0. Use `Result<ServerQuery>` from `QueryAsync`.

## Valve RCON

`ValveRconClient` (sealed, `IDisposable`) connects to any Source-engine server over the RCON TCP protocol. The host is a `string` or an `IPAddress`, the port is a `ushort`, and `ConnectAsync` returns `false` instead of throwing when the connection or authentication fails.

```csharp
using System.Net;
using CL.GameNetQuery.Valve;

using var rcon = new ValveRconClient("192.168.1.100", 27015, "rcon_password");
// or: new ValveRconClient(IPAddress.Parse("192.168.1.100"), 27015, "rcon_password");

if (await rcon.ConnectAsync(timeoutMs: 5000))
{
    string response = await rcon.SendCommandAsync("status");
    Console.WriteLine(response);
}

rcon.Disconnect();
```

| Member | Returns | Notes |
|--------|---------|-------|
| `ConnectAsync(int timeoutMs = 5000)` | `Task<bool>` | `false` on connect/auth failure |
| `SendCommandAsync(string command)` | `Task<string>` | Raw server response |
| `Disconnect()` | `void` | Close the socket |
| `Dispose()` | `void` | Release resources |

> `SendCommandAsync` throws `InvalidOperationException` if called before a successful `ConnectAsync`.

## Counter-Strike admin wrappers

`CounterStrike2` and `CounterStrikeSource` (both sealed, `IDisposable`, namespace `CL.GameNetQuery.Valve.Games`) layer named, typed commands over RCON. Construct with `(string ip, ushort port, string rconPassword)`, then `ConnectAsync` before issuing commands. Every command helper returns `Task<string>` (the raw RCON response).

```csharp
using CL.GameNetQuery.Valve.Games;

using var cs2 = new CounterStrike2("192.168.1.100", 27015, "rcon_password");
await cs2.ConnectAsync();

var (info, players) = await cs2.GetStatusAsync();   // parsed with the CS2 status parser
Console.WriteLine($"{info.Hostname} — {info.Map} — {info.PlayerCount} (+{info.BotCount} bots)");

await cs2.ChangeMapAsync("de_dust2");
await cs2.KickPlayerAsync("PlayerName");
await cs2.SetCvarAsync("mp_roundtime", "1.92");
string val = await cs2.GetCvarAsync("mp_roundtime");
await cs2.EnableCheatsAsync();
await cs2.SetFriendlyFireAsync(false);
await cs2.SlayPlayerAsync("PlayerName");
await cs2.SayAsync("Hello from RCON!");

cs2.Disconnect();
```

### Shared commands (CS2 and CSS)

| Method | Purpose |
|--------|---------|
| `ConnectAsync(int timeoutMs = 5000)` | Connect + authenticate (`Task<bool>`) |
| `GetStatusRawAsync()` | Raw `status` output |
| `GetStatusAsync()` | Parsed `(ServerInfo Info, List<PlayerInfo> Players)` |
| `ChangeMapAsync(map)` | Change the active map |
| `KickPlayerAsync(name)` | Kick a player |
| `BanPlayerAsync(name)` | Ban a player |
| `UnbanPlayerAsync(name)` | Remove a ban |
| `SetHostnameAsync(name)` | Set the server name |
| `ExecConfigAsync(cfg)` | Execute a server config |
| `SetCvarAsync(cvar, value)` | Set a console variable |
| `GetCvarAsync(cvar)` | Read a console variable |
| `RestartAsync()` | Restart the round/match |
| `SayAsync(msg)` | Broadcast a chat message |
| `Disconnect()` / `Dispose()` | Close + release |

### CS2-only extras

`CounterStrike2` adds the following beyond the shared set; `CounterStrikeSource` does **not** expose these:

| Method | Purpose |
|--------|---------|
| `EnableCheatsAsync()` | `sv_cheats 1` |
| `DisableCheatsAsync()` | `sv_cheats 0` |
| `SetFriendlyFireAsync(bool)` | Toggle friendly fire |
| `SetTeamBalanceAsync(bool)` | Toggle auto team balance |
| `SlayPlayerAsync(name)` | Slay (kill) a player |

## Status parsers

When you already hold raw `status` text, parse it directly with the static parsers in `CL.GameNetQuery.Valve`.

```csharp
using CL.GameNetQuery.Valve;
using CL.GameNetQuery.Models;

string raw = await rcon.SendCommandAsync("status");

// Source 1 / CSS
ServerInfo s1 = ValveStatusParser.ParseStatus(raw);
var (info, list) = ValveStatusParser.ParseStatusWithPlayers(raw);

// CS2 (different player/map format)
string cs2Map = ValveStatusParserCS2.GetMapName(raw);
List<PlayerInfo> cs2Players = ValveStatusParserCS2.GetPlayerList(raw);
List<string> spawngroups = ValveStatusParserCS2.GetSpawngroups(raw);
```

### ValveStatusParser (Source 1 / CSS)

| Method | Returns |
|--------|---------|
| `GetHostname(status)` / `GetMapName(status)` / `GetVersion(status)` | `string` |
| `GetPlayerCount(status)` | `(int HumanCount, int BotCount)` |
| `GetServerAddress(status)` | `(string Ip, int Port)` |
| `GetTags(status)` | `List<string>` |
| `GetPlayerList(status)` | `List<PlayerInfo>` |
| `ParseStatus(status)` | `ServerInfo` |
| `ParseStatusWithPlayers(status)` | `(ServerInfo Info, List<PlayerInfo> Players)` |

### ValveStatusParserCS2

The CS2 `status` layout differs, so it has its own parser:

| Method | Returns |
|--------|---------|
| `GetHostname(status)` / `GetMapName(status)` | `string` |
| `GetPlayerCount(status)` | `(int, int)` |
| `GetPlayerList(status)` | `List<PlayerInfo>` |
| `GetSpawngroups(status)` | `List<string>` |

## Minecraft (UDP query / RCON)

`MinecraftQueryClient` (static, `CL.GameNetQuery.Minecraft`) implements the GameSpy4 query protocol. `QueryServer` is **synchronous** (a fixed 3000 ms timeout) and returns the parsed response as text; read individual fields with `GetStatusValue`, which is case-insensitive and strips Minecraft `§` color codes. Failures return an empty string.

```csharp
using CL.GameNetQuery.Minecraft;

string response = MinecraftQueryClient.QueryServer("mc.example.com", 25565);
if (!string.IsNullOrEmpty(response))
{
    string motd       = MinecraftQueryClient.GetStatusValue(response, "hostname");
    string map        = MinecraftQueryClient.GetStatusValue(response, "map");
    string numPlayers = MinecraftQueryClient.GetStatusValue(response, "numplayers");
    string maxPlayers = MinecraftQueryClient.GetStatusValue(response, "maxplayers");
    Console.WriteLine($"{motd} - {numPlayers}/{maxPlayers} on {map}");
}
```

| Method | Returns | Notes |
|--------|---------|-------|
| `QueryServer(string ip, int queryPort)` | `string` | Synchronous; empty on failure |
| `QueryServer(IPAddress ip, int queryPort)` | `string` | Same, `IPAddress` overload |
| `GetStatusValue(string response, string key)` | `string` | Case-insensitive; color codes stripped |

> The query port (default 25565) needs `enable-query=true` in `server.properties`, and it is separate from the RCON port.

`MinecraftRconClient` (sealed, `IDisposable`) runs the same Source RCON wire protocol with Minecraft-specific auth handling:

```csharp
using CL.GameNetQuery.Minecraft;

using var rcon = new MinecraftRconClient("mc.example.com", 25575, "rcon_password");
if (await rcon.ConnectAsync())
    Console.WriteLine(await rcon.SendCommandAsync("list"));

rcon.Disconnect();
```

| Member | Returns |
|--------|---------|
| `ConnectAsync(int timeoutMs = 5000)` | `Task<bool>` |
| `SendCommandAsync(string command)` | `Task<string>` |
| `Disconnect()` / `Dispose()` | `void` |

## Health check

`HealthCheckAsync()` always reports *healthy* once the library is loaded — the query clients open their own short-lived connections per call, so there is no persistent resource to probe.

```csharp
var status = await Libraries.Get<GameNetQueryLibrary>().HealthCheckAsync();
// status.Status : Healthy
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.GameNetQuery)
