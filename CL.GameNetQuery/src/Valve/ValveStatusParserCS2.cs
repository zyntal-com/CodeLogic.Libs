using System.Text.RegularExpressions;
using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// Parses Counter-Strike 2 RCON "status" command output.
/// CS2 uses a different format from CSS for player lists and map names.
/// </summary>
public static class ValveStatusParserCS2
{
    /// <summary>Extracts the server hostname from the CS2 status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The server hostname, or empty string if not found.</returns>
    public static string GetHostname(string status) =>
        ValveStatusParser.GetHostname(status);

    /// <summary>Extracts the current map name from the CS2 status output, falling back to Source 1 format.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>The map name, or empty string if not found.</returns>
    public static string GetMapName(string status)
    {
        var match = Regex.Match(status, @"loaded spawngroup\(.+\):\s*SV:\s*\[\d+:\s*(\S+)\s*\|\s*main lump");
        return match.Success ? match.Groups[1].Value.Trim() : ValveStatusParser.GetMapName(status);
    }

    /// <summary>Extracts the human and bot player counts from the CS2 status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A tuple containing the human player count and bot count.</returns>
    public static (int HumanCount, int BotCount) GetPlayerCount(string status) =>
        ValveStatusParser.GetPlayerCount(status);

    /// <summary>Parses the CS2-format player list from the status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A list of player information entries.</returns>
    public static List<PlayerInfo> GetPlayerList(string status)
    {
        var players = new List<PlayerInfo>();
        var regex = new Regex(@"\s+(\d+)\s+(BOT|\d+)\s+(\d+)\s+(\d+)\s+(\w+)\s+(\d+)\s+'(.+?)'", RegexOptions.Multiline);

        foreach (Match match in regex.Matches(status))
        {
            var uniqueId = match.Groups[2].Value.Trim();
            players.Add(new PlayerInfo
            {
                Name = match.Groups[7].Value.Trim(),
                UserId = int.TryParse(match.Groups[1].Value, out var id) ? id : null,
                UniqueId = uniqueId,
                Ping = int.TryParse(match.Groups[3].Value, out var p) ? p : 0,
                Loss = int.TryParse(match.Groups[4].Value, out var l) ? l : 0,
                State = match.Groups[5].Value.Trim(),
                IsBot = uniqueId.Equals("BOT", StringComparison.OrdinalIgnoreCase)
            });
        }

        return players;
    }

    /// <summary>Extracts loaded spawngroup names from the CS2 status output.</summary>
    /// <param name="status">The raw status command output.</param>
    /// <returns>A list of spawngroup names.</returns>
    public static List<string> GetSpawngroups(string status)
    {
        var groups = new List<string>();
        foreach (Match match in Regex.Matches(status, @"loaded spawngroup\(\s*\d+\)\s*:\s*SV:\s*\[\d+:\s*(.+?)\s*\|"))
        {
            if (match.Success)
                groups.Add(match.Groups[1].Value.Trim());
        }
        return groups;
    }
}
