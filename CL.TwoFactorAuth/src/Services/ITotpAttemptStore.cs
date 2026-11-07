namespace CL.TwoFactorAuth.Services;

/// <summary>Atomic attempt admission for TOTP login checks. Multi-node applications provide a shared implementation.</summary>
public interface ITotpAttemptStore
{
    /// <summary>Reserves one attempt for the user; false when locked out or the store is full.</summary>
    bool TryBegin(string userId);

    /// <summary>Clears attempt state after successful verification of the user's credential.</summary>
    void Reset(string userId);
}
