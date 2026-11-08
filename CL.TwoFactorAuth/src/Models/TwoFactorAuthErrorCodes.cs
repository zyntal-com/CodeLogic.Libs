using CodeLogic.Core.Results;

namespace CL.TwoFactorAuth.Models;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.TwoFactorAuth reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant. Every message is safe to
/// show a user: exception text is logged, never returned.
/// </summary>
public static class TwoFactorAuthErrorCodes
{
    /// <summary>
    /// The TOTP code does not match the secret in the accepted window. Kind: <see cref="UnauthorizedError"/>;
    /// message <c>"Invalid code"</c>. <c>TotpLoginGuard.Validate</c> returns this error for every refused attempt
    /// (wrong code, replayed code, too many attempts, a secret that could not be read), so a caller cannot tell them apart.
    /// </summary>
    public const string InvalidCode = "2fa.invalid_code";

    /// <summary>
    /// The TOTP code matched, but its time step was already accepted for the user (replay), or a later step was
    /// accepted since: the <c>ITotpUsedStepStore</c> refused it. Kind: <see cref="UnauthorizedError"/>; message
    /// <c>"This code has already been used."</c>.
    /// </summary>
    public const string CodeAlreadyUsed = "2fa.code_already_used";

    /// <summary>
    /// Validation could not run, typically because the stored secret key is not valid Base32. Kind:
    /// <see cref="InternalError"/>; message <c>"The code could not be validated."</c>, <see cref="Error.Details"/> is
    /// the exception type.
    /// </summary>
    public const string ValidationFailed = "2fa.validation_failed";

    /// <summary>A TOTP code could not be generated because the secret key is invalid. Kind: <see cref="InternalError"/>.</summary>
    public const string GenerateFailed = "2fa.generate_failed";

    /// <summary>A PNG QR code could not be generated. Kind: <see cref="InternalError"/>; <see cref="Error.Details"/> is the exception type.</summary>
    public const string QrPngFailed = "2fa.qr_png_failed";

    /// <summary>A BMP QR code could not be generated. Kind: <see cref="InternalError"/>; <see cref="Error.Details"/> is the exception type.</summary>
    public const string QrBmpFailed = "2fa.qr_bmp_failed";

    /// <summary>A QR code could not be written to its file. Kind: <see cref="InternalError"/>; <see cref="Error.Details"/> is the exception type.</summary>
    public const string QrSaveFailed = "2fa.qr_save_failed";

    /// <summary>
    /// A QR code file path is not fully qualified, contains NUL, or does not end in <c>.png</c>; nothing is written.
    /// Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string QrInvalidPath = "2fa.qr_invalid_path";
}

/// <summary>Builds CL.TwoFactorAuth errors so every code is always paired with the same kind and a user-safe message.</summary>
internal static class TwoFactorAuthErrors
{
    public static Error InvalidCode() => Error.Unauthorized(TwoFactorAuthErrorCodes.InvalidCode, "Invalid code");

    public static Error CodeAlreadyUsed() => Error.Unauthorized(TwoFactorAuthErrorCodes.CodeAlreadyUsed, "This code has already been used.");

    public static Error ValidationFailed(Exception ex) =>
        Error.Internal(TwoFactorAuthErrorCodes.ValidationFailed, "The code could not be validated.", ex.GetType().Name);

    public static Error GenerateFailed() =>
        Error.Internal(TwoFactorAuthErrorCodes.GenerateFailed, "Failed to generate TOTP code: the secret key is invalid.");

    public static Error QrPngFailed(Exception ex) =>
        Error.Internal(TwoFactorAuthErrorCodes.QrPngFailed, "The QR code could not be generated.", ex.GetType().Name);

    public static Error QrBmpFailed(Exception ex) =>
        Error.Internal(TwoFactorAuthErrorCodes.QrBmpFailed, "The QR code could not be generated.", ex.GetType().Name);

    public static Error QrSaveFailed(Exception ex) =>
        Error.Internal(TwoFactorAuthErrorCodes.QrSaveFailed, "The QR code could not be saved.", ex.GetType().Name);

    public static Error QrInvalidPath(string message) => Error.Validation(TwoFactorAuthErrorCodes.QrInvalidPath, message);
}
