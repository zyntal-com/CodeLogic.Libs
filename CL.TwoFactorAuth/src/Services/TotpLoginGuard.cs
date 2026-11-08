using CL.TwoFactorAuth.Models;
using CodeLogic.Core.Results;

namespace CL.TwoFactorAuth.Services;

/// <summary>
/// Combines atomic attempt admission with user-bound replay protection. Wrong, replayed and locked-out codes
/// return the same generic error (<see cref="TwoFactorAuthErrorCodes.InvalidCode"/>), so a caller cannot tell them apart. Use a shared <see cref="ITotpAttemptStore"/> and <see cref="ITotpUsedStepStore"/>
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
    /// <returns>
    /// The accepted <see cref="TotpMatch"/>, or an <see cref="UnauthorizedError"/> coded
    /// <see cref="TwoFactorAuthErrorCodes.InvalidCode"/> for every refused attempt: a wrong code, a replayed code, too
    /// many attempts, or a secret that could not be read.
    /// </returns>
    public Result<TotpMatch> Validate(string code, string secretKey, string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        if (!_attempts.TryBegin(userId)) return TwoFactorAuthErrors.InvalidCode();
        var result = _authenticator.ValidateTotp(code, secretKey, userId);
        if (result.IsFailure) return TwoFactorAuthErrors.InvalidCode();
        _attempts.Reset(userId);
        return result;
    }
}
