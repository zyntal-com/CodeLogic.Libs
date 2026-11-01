namespace CL.SocialConnect.Models.Steam;

/// <summary>Helpers for Steam account identifiers.</summary>
public static class SteamIds
{
    /// <summary>
    /// Whether <paramref name="value"/> is a SteamID64 of an individual account: exactly 17 ASCII digits starting
    /// with <c>7656119</c>, the same form Steam sign-in (<see cref="Services.Steam.SteamOpenIdService"/>) accepts.
    /// </summary>
    /// <param name="value">The candidate Steam ID.</param>
    public static bool IsSteamId64(string? value)
    {
        if (value is not { Length: 17 } || !value.StartsWith("7656119", StringComparison.Ordinal))
            return false;
        foreach (var c in value)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }
}
