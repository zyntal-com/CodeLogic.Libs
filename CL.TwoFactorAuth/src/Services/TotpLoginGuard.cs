using CL.TwoFactorAuth.Models;

namespace CL.TwoFactorAuth.Services;

/// <summary>
/// Combines atomic attempt admission with user-bound replay protection. Wrong, replayed and locked-out codes
/// return the same generic result. Use a shared <see cref="ITotpAttemptStore"/> and <see cref="ITotpUsedStepStore"/>
/// across nodes; also rate-limit by source at the application boundary.
/// </summary>
public sealed class TotpLoginGuard
{
    private readonly TwoFactorAuthenticator _authenticator;
    private readonly ITotpAttemptStore _attempts;

    /// <summary>Creates a guard around the authenticator and an optional application-owned attempt store.</summary>
    /// <param name="authenticator">Authenticator providing the replay store and TOTP settings.</param>
    /// <param name="attempts">Atomic attempt store; defaults to the bounded process-local store.</param>
    public TotpLoginGuard(TwoFactorAuthenticator authenticator, ITotpAttemptStore? attempts = null)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _attempts = attempts ?? new InMemoryTotpAttemptStore();
    }

    /// <summary>Validates a login attempt; only a valid, unused code clears the user's attempt state.</summary>
    /// <param name="code">User-entered code.</param>
    /// <param name="secretKey">Stored Base32 secret.</param>
    /// <param name="userId">Stable account identifier used for replay protection and attempts.</param>
    public TotpValidationResult Validate(string code, string secretKey, string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        if (!_attempts.TryBegin(userId)) return TotpValidationResult.Invalid();
        var result = _authenticator.ValidateTotp(code, secretKey, userId);
        if (!result.IsValid) return TotpValidationResult.Invalid();
        _attempts.Reset(userId);
        return result;
    }
}
