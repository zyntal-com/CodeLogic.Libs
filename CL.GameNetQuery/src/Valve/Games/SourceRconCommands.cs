using System.Globalization;
using System.Text.RegularExpressions;
using CodeLogic.Core.Results;

namespace CL.GameNetQuery.Valve.Games;

/// <summary>
/// Builds the console lines sent by <see cref="CounterStrike2"/> and <see cref="CounterStrikeSource"/>.
/// The Source console splits a line into commands at <c>;</c> and line breaks (outside quotes) and into
/// arguments at whitespace and quotes, so every caller-supplied value is checked before it is spliced in:
/// free text may not contain <c>;</c>, <c>"</c> or control characters, and names (maps, configs, cvars, ids)
/// must match a strict pattern. A value that fails is a <see cref="ValidationError"/>
/// (<see cref="GameNetQueryErrorCodes.InvalidRconArgument"/>) and nothing is sent.
/// </summary>
internal static partial class SourceRconCommands
{
    public static Result<string> ChangeMap(string mapName) =>
        Path(mapName, nameof(mapName)) is { } e ? e : $"changelevel {mapName}";

    public static Result<string> Kick(string playerName) =>
        Text(playerName, nameof(playerName)) is { } e ? e : $"kick {playerName}";

    public static Result<string> KickId(int userId, string? reason)
    {
        if (UserId(userId, nameof(userId)) is { } e) return e;
        if (string.IsNullOrEmpty(reason)) return $"kickid {userId.ToString(CultureInfo.InvariantCulture)}";
        if (Text(reason, nameof(reason)) is { } r) return r;
        return $"kickid {userId.ToString(CultureInfo.InvariantCulture)} {reason}";
    }

    public static Result<string> Ban(string playerId) =>
        PlayerId(playerId, nameof(playerId)) is { } e ? e : $"banid 0 {playerId}";

    public static Result<string> BanId(int userId, int minutes)
    {
        if (UserId(userId, nameof(userId)) is { } e) return e;
        if (minutes < 0) return Invalid(nameof(minutes), "must be 0 (permanent) or more");
        return string.Create(CultureInfo.InvariantCulture, $"banid {minutes} {userId}");
    }

    public static Result<string> Unban(string playerId) =>
        PlayerId(playerId, nameof(playerId)) is { } e ? e : $"removeid {playerId}";

    public static Result<string> SetHostname(string name) =>
        Text(name, nameof(name)) is { } e ? e : $"hostname \"{name}\"";

    public static Result<string> ExecConfig(string configName) =>
        Path(configName, nameof(configName)) is { } e ? e : $"exec {configName}";

    public static Result<string> SetCvar(string cvar, string value)
    {
        if (Cvar(cvar, nameof(cvar)) is { } e) return e;
        if (Text(value, nameof(value), allowEmpty: true) is { } v) return v;
        return $"{cvar} \"{value}\"";
    }

    public static Result<string> GetCvar(string cvar) =>
        Cvar(cvar, nameof(cvar)) is { } e ? e : cvar;

    public static Result<string> Say(string message) =>
        Text(message, nameof(message)) is { } e ? e : $"say {message}";

    public static Result<string> Slay(string playerName) =>
        Text(playerName, nameof(playerName)) is { } e ? e : $"slay {playerName}";

    /// <summary>
    /// Free text (names, messages, hostnames, cvar values): rejects <c>;</c>, <c>"</c> and every control
    /// character (CR, LF, NUL, TAB, C1, ...), so the value can neither end the command nor close a quote.
    /// </summary>
    internal static Error? Text(string? value, string parameter, bool allowEmpty = false)
    {
        if (value is null) return Invalid(parameter, "is null");
        if (value.Length == 0) return allowEmpty ? null : Invalid(parameter, "is empty");
        foreach (var c in value)
        {
            if (c is ';' or '"')
                return Invalid(parameter, $"contains '{c}', which the server console reads as a command or quote boundary");
            if (char.IsControl(c))
                return Invalid(parameter, $"contains the control character U+{(int)c:X4}");
        }
        return null;
    }

    /// <summary>A map or config path: letters, digits, <c>_ - . /</c>, no <c>..</c> segment, not absolute.</summary>
    private static Error? Path(string? value, string parameter)
    {
        if (string.IsNullOrEmpty(value)) return Invalid(parameter, "is empty");
        if (!PathPattern().IsMatch(value) || value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal))
            return Invalid(parameter, "must contain only letters, digits and _ - . / (no '..', not absolute)");
        return null;
    }

    /// <summary>A console variable name: letters, digits, <c>_</c> and <c>.</c>, starting with a letter or <c>_</c>.</summary>
    private static Error? Cvar(string? value, string parameter) =>
        !string.IsNullOrEmpty(value) && CvarPattern().IsMatch(value)
            ? null
            : Invalid(parameter, "must be a console variable name (letters, digits, _ and .)");

    /// <summary>A user id or SteamID: <c>123</c>, <c>STEAM_1:0:123</c>, <c>[U:1:123]</c> or a 64-bit SteamID.</summary>
    private static Error? PlayerId(string? value, string parameter) =>
        !string.IsNullOrEmpty(value) && PlayerIdPattern().IsMatch(value)
            ? null
            : Invalid(parameter, "must be a user id or SteamID (123, STEAM_1:0:123, [U:1:123] or 7656119...)");

    private static Error? UserId(int value, string parameter) =>
        value < 0 ? Invalid(parameter, "must not be negative") : null;

    private static Error Invalid(string parameter, string reason) =>
        Error.Validation(GameNetQueryErrorCodes.InvalidRconArgument, $"RCON argument '{parameter}' {reason}; nothing was sent");

    [GeneratedRegex(@"^[A-Za-z0-9_\-./]+$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex CvarPattern();

    [GeneratedRegex(@"^(\d{1,20}|STEAM_[0-5]:[01]:\d{1,10}|\[U:1:\d{1,10}\])$")]
    private static partial Regex PlayerIdPattern();
}
