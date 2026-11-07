namespace CL.TwoFactorAuth.Services;

/// <summary>
/// Remembers the last TOTP time step accepted for each user, so <see cref="TwoFactorAuthenticator.ValidateTotp(string, string, string?)"/>
/// can refuse a code that was already used (replay). The default is <see cref="InMemoryTotpUsedStepStore"/>; an
/// application that runs on several servers must share one store between them (a database row per user with a
/// conditional update, a cache with an atomic compare-and-set).
/// </summary>
public interface ITotpUsedStepStore
{
    /// <summary>
    /// Records <paramref name="step"/> as the last accepted step for <paramref name="userId"/> when it is greater
    /// than the step recorded before. The comparison and the update must be one atomic operation, so two
    /// concurrent requests cannot both accept the same code.
    /// </summary>
    /// <param name="userId">The user the code belongs to.</param>
    /// <param name="step">The absolute TOTP time step that matched (<c>TotpValidationResult.MatchedWindow</c>).</param>
    /// <returns><c>true</c> when the step is newer than every step accepted before and is now recorded; <c>false</c> when it was already used, or an older step was accepted since.</returns>
    bool TryUse(string userId, long step);
}
