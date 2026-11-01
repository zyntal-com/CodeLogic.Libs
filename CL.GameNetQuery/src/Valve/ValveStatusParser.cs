using System.Text.RegularExpressions;
using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// Parses Valve Source Engine RCON "status" command output.
/// Works for CSS (Source 1) servers.
/// </summary>
public static class ValveStatusParser
{
    /// <summary>Extracts the server hostname from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The server hostname, or empty string if not found.</returns>
    public static string GetHostname(string status)
    {
        var match = Regex.Match(status, @"hostname:\s*(.+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Extracts the current map name from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The map name, or empty string if not found.</returns>
    public static string GetMapName(string status)
    {
        var match = Regex.Match(status, @"map\s*:\s*(\S+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Extracts the human and bot player counts from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A tuple containing the human player count and bot count.</returns>
    public static (int HumanCount, int BotCount) GetPlayerCount(string status)
    {
        var match = Regex.Match(status, @"players\s*:\s*(\d+)\s*humans,\s*(\d+)\s*bots");
        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : (0, 0);
    }

    /// <summary>Extracts the server IP address and port from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A tuple containing the IP address and port number.</returns>
    public static (string Ip, int Port) GetServerAddress(string status)
    {
        var match = Regex.Match(status, @"udp/ip\s*:\s*(\S+):(\d+)");
        return match.Success
            ? (match.Groups[1].Value, int.Parse(match.Groups[2].Value))
            : (string.Empty, 0);
    }

    /// <summary>Extracts the server version string from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The version string, or empty string if not found.</returns>
    public static string GetVersion(string status)
    {
        var match = Regex.Match(status, @"version\s*:\s*(\S+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Extracts the server tags from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A list of server tags.</returns>
    public static List<string> GetTags(string status)
    {
        var match = Regex.Match(status, @"tags\s*:\s*(.+)");
        return match.Success
            ? match.Groups[1].Value.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    /// <summary>Parses the player list from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A list of player information entries.</returns>
    public static List<PlayerInfo> GetPlayerList(string status)
    {
        var players = new List<PlayerInfo>();
        var regex = new Regex(
            @"#\s+(?<userid>\d+)\s+""(?<name>.+?)""\s+(?<uniqueid>\S+)\s*(?<connected>\S*)\s*(?<ping>\d*)\s*(?<loss>\d*)\s*(?<state>\S*)\s*(?<adr>\S*)?",
            RegexOptions.Compiled);

        foreach (var line in status.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith('#') || line.Contains("userid name")) continue;

            var match = regex.Match(line);
            if (!match.Success) continue;

            var uniqueId = match.Groups["uniqueid"].Value;
            players.Add(new PlayerInfo
            {
                Name = match.Groups["name"].Value,
                UserId = int.TryParse(match.Groups["userid"].Value, out var id) ? id : null,
                UniqueId = uniqueId,
                Ping = int.TryParse(match.Groups["ping"].Value, out var p) ? p : 0,
                Loss = int.TryParse(match.Groups["loss"].Value, out var l) ? l : 0,
                State = match.Groups["state"].Value,
                IsBot = uniqueId.Equals("BOT", StringComparison.OrdinalIgnoreCase)
            });
        }

        return players;
    }

    /// <summary>Parses the full status output into a <see cref="ServerInfo"/> object.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The parsed server information.</returns>
    public static ServerInfo ParseStatus(string status) =>
        ParseStatusWithPlayers(status).Info;

    /// <summary>Parses the full status output into server info and a player list.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A tuple containing the server information and player list.</returns>
    public static (ServerInfo Info, List<PlayerInfo> Players) ParseStatusWithPlayers(string status)
    {
        var (humanCount, botCount) = GetPlayerCount(status);
        var (ip, port) = GetServerAddress(status);

        var info = new ServerInfo
        {
            Hostname = GetHostname(status),
            Map = GetMapName(status),
            PlayerCount = humanCount,
            BotCount = botCount,
            Version = GetVersion(status),
            Ip = ip,
            Port = port,
            Tags = GetTags(status),
            IsOnline = !string.IsNullOrEmpty(GetHostname(status)),
            RawResponse = status,
            QueriedUtc = DateTime.UtcNow
        };

        return (info, GetPlayerList(status));
    }
}
