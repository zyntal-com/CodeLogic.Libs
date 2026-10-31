namespace CL.TwoFactorAuth.Models;

/// <summary>
/// Represents a TOTP secret key bound to a specific issuer and user.
/// Provides the provisioning URI used to generate QR codes for authenticator apps.
/// </summary>
/// <param name="SecretKey">The Base32-encoded TOTP secret key.</param>
/// <param name="IssuerName">The name of the service or application issuing the key.</param>
/// <param name="UserName">The user account name (typically an email address).</param>
/// <param name="PeriodSeconds">
/// The TOTP time step in seconds the server validates with. It is written to the provisioning URI so the
/// authenticator app generates codes for the same period. Default: 30.
/// </param>
public record TwoFactorKey(string SecretKey, string IssuerName, string UserName, int PeriodSeconds = 30)
{
    /// <summary>
    /// The OTP provisioning URI in the format:
    /// <c>otpauth://totp/{issuer}:{user}?secret={key}&amp;issuer={issuer}&amp;period={seconds}</c>
    /// <para>
    /// This URI is used as the QR code payload for authenticator app scanning.
    /// </para>
    /// </summary>
    public string ProvisioningUri =>
        $"otpauth://totp/{Uri.EscapeDataString(IssuerName)}:{Uri.EscapeDataString(UserName)}" +
        $"?secret={SecretKey}&issuer={Uri.EscapeDataString(IssuerName)}&period={PeriodSeconds}";

    /// <summary>
    /// Returns a display-friendly string showing the issuer and user, not the secret key.
    /// </summary>
    public override string ToString() => $"{IssuerName}:{UserName}";
}
