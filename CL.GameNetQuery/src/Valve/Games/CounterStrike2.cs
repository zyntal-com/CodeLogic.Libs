using CL.GameNetQuery.Models;
using CodeLogic.Core.Results;

namespace CL.GameNetQuery.Valve.Games;

/// <summary>
/// Counter-Strike 2 server interaction via RCON.
/// Uses CS2-specific status parsing (different player/map format from CSS).
/// </summary>
public sealed class CounterStrike2 : IDisposable
{
    private readonly ValveRconClient _rcon;

    /// <summary>Initializes a new CS2 server client with the specified connection details.</summary>
    /// <param name="ip">The server IP address.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="rconPassword">The RCON password.</param>
    public CounterStrike2(string ip, ushort port, string rconPassword)
    {
        _rcon = new ValveRconClient(ip, port, rconPassword);
    }

    /// <summary>Connects to the CS2 RCON server and authenticates.</summary>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <returns><c>true</c> if connection and authentication succeeded.</returns>
    public Task<bool> ConnectAsync(int timeoutMs = 5000) => _rcon.ConnectAsync(timeoutMs);

    /// <summary>Gets the raw status command output from the server.</summary>
    /// <returns>The raw status response string.</returns>
    public async Task<string> GetStatusRawAsync() => await _rcon.SendCommandAsync("status").ConfigureAwait(false);

    /// <summary>Gets parsed server info and player list from the status command.</summary>
    /// <returns>A tuple containing server information and the player list.</returns>
    public async Task<(ServerInfo Info, List<PlayerInfo> Players)> GetStatusAsync()
    {
        var raw = await GetStatusRawAsync().ConfigureAwait(false);
        var hostname = ValveStatusParserCS2.GetHostname(raw);
        var map = ValveStatusParserCS2.GetMapName(raw);
        var (humanCount, botCount) = ValveStatusParserCS2.GetPlayerCount(raw);
        var players = ValveStatusParserCS2.GetPlayerList(raw);

        var info = new ServerInfo
        {
            Hostname = hostname,
            Map = map,
            PlayerCount = humanCount,
            BotCount = botCount,
            Version = ValveStatusParser.GetVersion(raw),
            IsOnline = !string.IsNullOrEmpty(hostname),
            RawResponse = raw,
            QueriedUtc = DateTime.UtcNow
        };

        return (info, players);
    }

    // Every command that splices in a caller-supplied value is built by SourceRconCommands, which rejects
    // values that could end the command or add another one (';', '"', CR, LF, NUL, ...). A rejected value
    // is a ValidationError (gamenetquery.invalid_rcon_argument) and nothing is sent.

    /// <summary>Changes the current map on the server (<c>changelevel</c>).</summary>
    /// <param name="mapName">The map, e.g. <c>de_dust2</c>: letters, digits and <c>_ - . /</c> only.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an invalid map name.</returns>
    public Task<Result<string>> ChangeMapAsync(string mapName) => SendAsync(SourceRconCommands.ChangeMap(mapName));

    /// <summary>
    /// Kicks a player by name (<c>kick name</c>). Prefer <see cref="KickPlayerAsync(int, string?)"/>: a name
    /// containing <c>;</c>, <c>"</c> or a control character is rejected, and names need not be unique.
    /// </summary>
    /// <param name="playerName">The player name.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an unsafe name.</returns>
    public Task<Result<string>> KickPlayerAsync(string playerName) => SendAsync(SourceRconCommands.Kick(playerName));

    /// <summary>Kicks a player by user id (<c>kickid userid [reason]</c>), as reported in <see cref="PlayerInfo.UserId"/>.</summary>
    /// <param name="userId">The user id from <c>status</c>.</param>
    /// <param name="reason">Optional message shown to the player; no <c>;</c>, <c>"</c> or control characters.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for a negative id or unsafe reason.</returns>
    public Task<Result<string>> KickPlayerAsync(int userId, string? reason = null) => SendAsync(SourceRconCommands.KickId(userId, reason));

    /// <summary>Permanently bans a player (<c>banid 0 id</c>).</summary>
    /// <param name="playerId">A user id or SteamID (<c>123</c>, <c>STEAM_1:0:123</c>, <c>[U:1:123]</c>, <c>7656119...</c>). <c>banid</c> does not take names.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) when <paramref name="playerId"/> is not an id.</returns>
    public Task<Result<string>> BanPlayerAsync(string playerId) => SendAsync(SourceRconCommands.Ban(playerId));

    /// <summary>Bans a player by user id (<c>banid minutes userid</c>).</summary>
    /// <param name="userId">The user id from <c>status</c> (<see cref="PlayerInfo.UserId"/>).</param>
    /// <param name="minutes">Ban length in minutes; <c>0</c> (default) is permanent.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for a negative id or length.</returns>
    public Task<Result<string>> BanPlayerAsync(int userId, int minutes = 0) => SendAsync(SourceRconCommands.BanId(userId, minutes));

    /// <summary>Removes a ban (<c>removeid id</c>).</summary>
    /// <param name="playerId">A user id or SteamID (<c>STEAM_1:0:123</c>, <c>[U:1:123]</c>, ...).</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) when <paramref name="playerId"/> is not an id.</returns>
    public Task<Result<string>> UnbanPlayerAsync(string playerId) => SendAsync(SourceRconCommands.Unban(playerId));

    /// <summary>Sets the server hostname (<c>hostname "name"</c>).</summary>
    /// <param name="name">The new hostname; no <c>;</c>, <c>"</c> or control characters.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an unsafe name.</returns>
    public Task<Result<string>> SetHostnameAsync(string name) => SendAsync(SourceRconCommands.SetHostname(name));

    /// <summary>Executes a server config file (<c>exec name</c>).</summary>
    /// <param name="configName">The config, e.g. <c>gamemode_competitive</c>: letters, digits and <c>_ - . /</c>, no <c>..</c>.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an invalid name.</returns>
    public Task<Result<string>> ExecConfigAsync(string configName) => SendAsync(SourceRconCommands.ExecConfig(configName));

    /// <summary>Sets a console variable (<c>cvar "value"</c>).</summary>
    /// <param name="cvar">The console variable name (letters, digits, <c>_</c> and <c>.</c>).</param>
    /// <param name="value">The value; may be empty; no <c>;</c>, <c>"</c> or control characters.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an invalid name or unsafe value.</returns>
    public Task<Result<string>> SetCvarAsync(string cvar, string value) => SendAsync(SourceRconCommands.SetCvar(cvar, value));

    /// <summary>Gets the current value of a console variable.</summary>
    /// <param name="cvar">The console variable name (letters, digits, <c>_</c> and <c>.</c>).</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an invalid name.</returns>
    public Task<Result<string>> GetCvarAsync(string cvar) => SendAsync(SourceRconCommands.GetCvar(cvar));

    /// <summary>Restarts the current map.</summary>
    /// <returns>The server response.</returns>
    public Task<string> RestartAsync() => _rcon.SendCommandAsync("restart");

    /// <summary>Sends a chat message to all players (<c>say message</c>).</summary>
    /// <param name="message">The message; no <c>;</c>, <c>"</c> or control characters.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an unsafe message.</returns>
    public Task<Result<string>> SayAsync(string message) => SendAsync(SourceRconCommands.Say(message));

    /// <summary>Enables cheats on the server.</summary>
    /// <returns>The server response.</returns>
    public Task<string> EnableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 1");
    /// <summary>Disables cheats on the server.</summary>
    /// <returns>The server response.</returns>
    public Task<string> DisableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 0");
    /// <summary>Enables or disables friendly fire.</summary>
    /// <param name="enable">Whether to enable friendly fire.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetFriendlyFireAsync(bool enable) => _rcon.SendCommandAsync($"mp_friendlyfire {(enable ? 1 : 0)}");
    /// <summary>Enables or disables automatic team balancing.</summary>
    /// <param name="enable">Whether to enable team balancing.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetTeamBalanceAsync(bool enable) => _rcon.SendCommandAsync($"mp_autoteambalance {(enable ? 1 : 0)}");
    /// <summary>Slays (kills) a player on the server (<c>slay name</c>).</summary>
    /// <param name="playerName">The player name; no <c>;</c>, <c>"</c> or control characters.</param>
    /// <returns>The server response, or a <see cref="ValidationError"/> (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) for an unsafe name.</returns>
    public Task<Result<string>> SlayPlayerAsync(string playerName) => SendAsync(SourceRconCommands.Slay(playerName));

    private async Task<Result<string>> SendAsync(Result<string> command) =>
        command.IsFailure ? command.Error! : await _rcon.SendCommandAsync(command.Value!).ConfigureAwait(false);

    /// <summary>Disconnects from the RCON server.</summary>
    public void Disconnect() => _rcon.Disconnect();
    /// <summary>Disposes the client and disconnects from the server.</summary>
    public void Dispose() => _rcon.Dispose();
}
