namespace CL.GameNetQuery.Models;

/// <summary>
/// Represents a player currently on a game server.
/// </summary>
public sealed class PlayerInfo
{
    /// <summary>Player name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The server's user id for the player (the <c>userid</c> / <c>id</c> column of RCON <c>status</c>), for
    /// <c>kickid</c> / <c>banid</c>. <see langword="null"/> when the source does not report one (A2S_PLAYER).
    /// </summary>
    public int? UserId { get; init; }

    /// <summary>Unique identifier (SteamID for Valve, UUID for Minecraft).</summary>
    public string UniqueId { get; init; } = string.Empty;

    /// <summary>Player score/kills.</summary>
    public int Score { get; init; }

    /// <summary>Connection duration in seconds.</summary>
    public float Duration { get; init; }

    /// <summary>Ping/latency in milliseconds.</summary>
    public int Ping { get; init; }

    /// <summary>Packet loss percentage.</summary>
    public int Loss { get; init; }

    /// <summary>Player connection state.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>Whether this player is a bot.</summary>
    public bool IsBot { get; init; }
}
