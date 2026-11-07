using CL.TwoFactorAuth.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using QRCoder;

namespace CL.TwoFactorAuth.Services;

/// <summary>
/// Generates QR codes for TOTP provisioning URIs in various output formats. Failures carry a generic message;
/// the exception (which can describe the key or a file path) is logged only.
/// </summary>
public sealed class QrCodeGenerator
{
    private readonly TwoFactorAuthConfig _config;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="QrCodeGenerator"/>.
    /// </summary>
    /// <param name="config">Two-factor authentication configuration.</param>
    /// <param name="logger">Optional scoped logger.</param>
    public QrCodeGenerator(TwoFactorAuthConfig config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a QR code as a PNG byte array for the given <see cref="TwoFactorKey"/>.
    /// </summary>
    /// <param name="key">The two-factor key whose provisioning URI will be encoded.</param>
    /// <returns>A <see cref="Result{T}"/> containing the PNG bytes, or <c>2fa.qr_png_failed</c>.</returns>
    public Result<byte[]> GenerateQrCodePng(TwoFactorKey key)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(key);
            var data = CreateQrData(key.ProvisioningUri);
            var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(_config.QrCodeModuleSize);
            _logger?.Debug($"Generated PNG QR code for {key}");
            return Result<byte[]>.Success(bytes);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to generate PNG QR code: {ex.Message}", ex);
            return Result<byte[]>.Failure(Error.Internal("2fa.qr_png_failed", "The QR code could not be generated.", ex.GetType().Name));
        }
    }

    /// <summary>
    /// Generates a QR code as a Base64-encoded PNG string for the given <see cref="TwoFactorKey"/>.
    /// </summary>
    /// <param name="key">The two-factor key whose provisioning URI will be encoded.</param>
    /// <returns>A <see cref="Result{T}"/> containing the Base64 string, or a failure result.</returns>
    public Result<string> GenerateQrCodeBase64(TwoFactorKey key)
    {
        var pngResult = GenerateQrCodePng(key);
        if (pngResult.IsFailure)
            return Result<string>.Failure(pngResult.Error!);

        var base64 = Convert.ToBase64String(pngResult.Value!);
        return Result<string>.Success(base64);
    }

    /// <summary>
    /// Generates a QR code as a data URI string suitable for embedding in HTML.
    /// Format: <c>data:image/png;base64,{base64data}</c>
    /// </summary>
    /// <param name="key">The two-factor key whose provisioning URI will be encoded.</param>
    /// <returns>A <see cref="Result{T}"/> containing the data URI string, or a failure result.</returns>
    public Result<string> GenerateQrCodeDataUri(TwoFactorKey key)
    {
        var base64Result = GenerateQrCodeBase64(key);
        if (base64Result.IsFailure)
            return Result<string>.Failure(base64Result.Error!);

        var dataUri = $"data:image/png;base64,{base64Result.Value}";
        return Result<string>.Success(dataUri);
    }

    /// <summary>
    /// Generates a QR code as a BMP byte array for the given <see cref="TwoFactorKey"/>.
    /// </summary>
    /// <param name="key">The two-factor key whose provisioning URI will be encoded.</param>
    /// <returns>A <see cref="Result{T}"/> containing the BMP bytes, or <c>2fa.qr_bmp_failed</c>.</returns>
    public Result<byte[]> GenerateQrCodeBmp(TwoFactorKey key)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(key);
            var data = CreateQrData(key.ProvisioningUri);
            var qr = new BitmapByteQRCode(data);
            var bytes = qr.GetGraphic(_config.QrCodeModuleSize);
            _logger?.Debug($"Generated BMP QR code for {key}");
            return Result<byte[]>.Success(bytes);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to generate BMP QR code: {ex.Message}", ex);
            return Result<byte[]>.Failure(Error.Internal("2fa.qr_bmp_failed", "The QR code could not be generated.", ex.GetType().Name));
        }
    }

    /// <summary>
    /// Generates a QR code as a PNG and saves it to the specified file path.
    /// </summary>
    /// <param name="key">The two-factor key whose provisioning URI will be encoded.</param>
    /// <param name="filePath">
    /// Fully qualified path of the file to write, ending in <c>.png</c>; anything else is a <c>2fa.qr_invalid_path</c>
    /// validation error and nothing is written. The directory must exist.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating success, <c>2fa.qr_invalid_path</c>, or <c>2fa.qr_save_failed</c>.</returns>
    public Result SaveQrCodeToFile(TwoFactorKey key, string filePath)
    {
        if (ValidateSavePath(filePath) is { } invalid)
            return Result.Failure(invalid);

        var pngResult = GenerateQrCodePng(key);
        if (pngResult.IsFailure)
            return Result.Failure(pngResult.Error!);

        try
        {
            File.WriteAllBytes(filePath, pngResult.Value!);
            _logger?.Info($"Saved QR code to {filePath}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to save QR code to file '{filePath}': {ex.Message}", ex);
            return Result.Failure(Error.Internal("2fa.qr_save_failed", "The QR code could not be saved.", ex.GetType().Name));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A save path must be fully qualified, free of NUL, and end in <c>.png</c> (any case).</summary>
    private static Error? ValidateSavePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains('\0') || !Path.IsPathFullyQualified(filePath))
            return Error.Validation("2fa.qr_invalid_path", "The QR code file path must be a fully qualified path.");
        if (!string.Equals(Path.GetExtension(filePath), ".png", StringComparison.OrdinalIgnoreCase))
            return Error.Validation("2fa.qr_invalid_path", "The QR code file path must end in .png.");
        return null;
    }

    private QRCodeData CreateQrData(string uri)
    {
        var generator = new QRCodeGenerator();
        return generator.CreateQrCode(uri, MapEccLevel(_config.ErrorCorrectionLevel));
    }

    private static QRCodeGenerator.ECCLevel MapEccLevel(QrErrorCorrectionLevel level) => level switch
    {
        QrErrorCorrectionLevel.L => QRCodeGenerator.ECCLevel.L,
        QrErrorCorrectionLevel.M => QRCodeGenerator.ECCLevel.M,
        QrErrorCorrectionLevel.Q => QRCodeGenerator.ECCLevel.Q,
        QrErrorCorrectionLevel.H => QRCodeGenerator.ECCLevel.H,
        _ => QRCodeGenerator.ECCLevel.Q
    };
}
