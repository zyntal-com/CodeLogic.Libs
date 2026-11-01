using CodeLogic.Core.Results;

namespace CL.GameNetQuery;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.GameNetQuery reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant.
/// </summary>
public static class GameNetQueryErrorCodes
{
    /// <summary>The address is not an IP address. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidAddress = "gamenetquery.invalid_address";

    /// <summary>The timeout is zero or negative. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidTimeout = "gamenetquery.invalid_timeout";

    /// <summary>
    /// An argument of a typed RCON command (player name, map, cvar, message, ...) could be read by the server as
    /// more than one argument or more than one command: it contains <c>;</c>, <c>"</c>, a line break, NUL or another
    /// control character, or it does not have the shape the command needs (a map or config path, a cvar name,
    /// a user id or SteamID). Nothing is sent. Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidRconArgument = "gamenetquery.invalid_rcon_argument";

    /// <summary>No answer within the timeout. Kind: <see cref="TimeoutError"/>.</summary>
    public const string Timeout = "gamenetquery.timeout";

    /// <summary>Socket error, e.g. ICMP port unreachable; <c>Details</c> is the <c>SocketError</c>. Kind: <see cref="UnavailableError"/>.</summary>
    public const string Unreachable = "gamenetquery.unreachable";

    /// <summary>The answer could not be parsed. Kind: <see cref="InternalError"/>.</summary>
    public const string MalformedResponse = "gamenetquery.malformed_response";

    /// <summary>Any other failure. Kind: <see cref="InternalError"/>.</summary>
    public const string QueryFailed = "gamenetquery.query_failed";
}
