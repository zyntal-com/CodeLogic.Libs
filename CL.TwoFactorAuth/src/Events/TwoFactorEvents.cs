using CodeLogic.Core.Events;

namespace CL.TwoFactorAuth.Events;

/// <summary>
/// Published on the <see cref="IEventBus"/> after a TOTP code has been validated.
/// </summary>
/// <param name="UserId">Identifier of the user whose code was validated.</param>
/// <param name="IsValid">Whether the TOTP code was valid.</param>
/// <param name="MatchedWindow">The absolute time step that matched (Unix time / time step), or <c>null</c> if validation failed.</param>
/// <param name="ValidatedAt">UTC timestamp of the validation attempt.</param>
public record TotpValidatedEvent(
    string UserId,
    bool IsValid,
    long? MatchedWindow,
    DateTime ValidatedAt
) : IEvent;

/// <summary>
/// Published on the <see cref="IEventBus"/> after a new TOTP secret key is generated.
/// </summary>
/// <param name="IssuerName">The issuer name associated with the generated key.</param>
/// <param name="UserName">The user account name associated with the generated key.</param>
/// <param name="GeneratedAt">UTC timestamp of key generation.</param>
public record SecretKeyGeneratedEvent(
    string IssuerName,
    string UserName,
    DateTime GeneratedAt
) : IEvent;
