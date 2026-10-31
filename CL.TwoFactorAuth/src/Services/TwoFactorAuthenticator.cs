using CL.TwoFactorAuth.Events;
using CL.TwoFactorAuth.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using OtpNet;

namespace CL.TwoFactorAuth.Services;

/// <summary>
/// Provides TOTP (Time-based One-Time Password) generation and validation using OtpNet.
/// </summary>
public sealed class TwoFactorAuthenticator
{
    private readonly TwoFactorAuthConfig _config;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    /// <summary>
    /// Initializes a new instance of <see cref="TwoFactorAuthenticator"/>.
    /// </summary>
    /// <param name="config">Two-factor authentication configuration.</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="events">Optional event bus for publishing TOTP events.</param>
    public TwoFactorAuthenticator(TwoFactorAuthConfig config, ILogger? logger = null, IEventBus? events = null)
    {
        _config = config;
        _logger = logger;
        _events = events;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a new random Base32-encoded TOTP secret key.
    /// </summary>
    /// <returns>A Base32-encoded secret key string suitable for use with authenticator apps.</returns>
    public string GenerateSecretKey()
    {
        var keyBytes = KeyGeneration.GenerateRandomKey();
        var key = Base32Encoding.ToString(keyBytes);
        _logger?.Debug("Generated new TOTP secret key");
        return key;
    }

    /// <summary>
    /// Generates a new <see cref="TwoFactorKey"/> with a fresh secret key for the given issuer and user.
    /// Publishes a <see cref="SecretKeyGeneratedEvent"/> on the event bus.
    /// </summary>
    /// <param name="issuerName">The service or application name (e.g., "MyApp").</param>
    /// <param name="userName">The user account name (typically an email address).</param>
    /// <returns>A new <see cref="TwoFactorKey"/> with a generated secret.</returns>
    public TwoFactorKey GenerateNewKey(string issuerName, string userName)
    {
        var secretKey = GenerateSecretKey();
        var key = new TwoFactorKey(secretKey, issuerName, userName, _config.TimeStepSeconds);

        _logger?.Info($"Generated new 2FA key for {issuerName}:{userName}");
        _events?.Publish(new SecretKeyGeneratedEvent(issuerName, userName, DateTime.UtcNow));

        return key;
    }

    /// <summary>
    /// Generates the current TOTP code for the given secret key.
    /// </summary>
    /// <param name="secretKey">The Base32-encoded TOTP secret key.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the 6-digit TOTP code string,
    /// or a failure if the key is invalid.
    /// </returns>
    public Result<string> GenerateTotpCode(string secretKey)
    {
        try
        {
            var secretBytes = Base32Encoding.ToBytes(secretKey);
            var totp = new Totp(secretBytes, step: _config.TimeStepSeconds);
            var code = totp.ComputeTotp();
            _logger?.Debug("Generated TOTP code");
            return Result<string>.Success(code);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to generate TOTP code: {ex.Message}";
            _logger?.Error(msg, ex);
            return Result<string>.Failure(Error.Internal("2fa.generate_failed", msg, ex.GetType().Name));
        }
    }

    /// <summary>
    /// Validates a TOTP code against the given secret key.
    /// Publishes a <see cref="TotpValidatedEvent"/> on the event bus when a <paramref name="userId"/> is provided.
    /// </summary>
    /// <param name="code">The 6-digit TOTP code to validate.</param>
    /// <param name="secretKey">The Base32-encoded TOTP secret key.</param>
    /// <param name="userId">Optional user identifier for event publishing.</param>
    /// <returns>
    /// A <see cref="TotpValidationResult"/> indicating success or failure. On success
    /// <see cref="TotpValidationResult.MatchedWindow"/> is the absolute time step for replay tracking and
    /// <see cref="TotpValidationResult.WindowOffset"/> the drift in steps.
    /// </returns>
    public TotpValidationResult ValidateTotp(string code, string secretKey, string? userId = null) =>
        ValidateTotp(code, secretKey, DateTime.UtcNow, userId);

    /// <summary>
    /// Validates a TOTP code as of <paramref name="timestampUtc"/> instead of the current time.
    /// Otherwise identical to <see cref="ValidateTotp(string, string, string?)"/>.
    /// </summary>
    /// <param name="code">The TOTP code to validate.</param>
    /// <param name="secretKey">The Base32-encoded TOTP secret key.</param>
    /// <param name="timestampUtc">The moment to validate at (UTC).</param>
    /// <param name="userId">Optional user identifier for event publishing.</param>
    /// <returns>A <see cref="TotpValidationResult"/> indicating success or failure.</returns>
    public TotpValidationResult ValidateTotp(string code, string secretKey, DateTime timestampUtc, string? userId = null)
    {
        try
        {
            var secretBytes = Base32Encoding.ToBytes(secretKey);
            var totp = new Totp(secretBytes, step: _config.TimeStepSeconds);

            var window = new VerificationWindow(previous: _config.WindowSize, future: _config.WindowSize);
            var isValid = totp.VerifyTotp(timestampUtc, code, out long matchedWindow, window);

            TotpValidationResult result;
            if (isValid)
            {
                var currentStep = new DateTimeOffset(DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    / _config.TimeStepSeconds;
                result = TotpValidationResult.Valid(matchedWindow, checked((int)(matchedWindow - currentStep)));
            }
            else
            {
                result = TotpValidationResult.Invalid();
            }

            if (userId is not null)
                _events?.Publish(new TotpValidatedEvent(userId, isValid, result.MatchedWindow, DateTime.UtcNow));

            _logger?.Debug(isValid
                ? $"TOTP validation succeeded (window: {matchedWindow})"
                : "TOTP validation failed — code did not match");

            return result;
        }
        catch (Exception ex)
        {
            var msg = $"TOTP validation error: {ex.Message}";
            _logger?.Error(msg, ex);
            return TotpValidationResult.Invalid(msg);
        }
    }

    /// <summary>
    /// Returns the number of seconds remaining until the current TOTP code expires.
    /// </summary>
    public int GetSecondsUntilExpiry()
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var secondsIntoStep = (int)(epoch % _config.TimeStepSeconds);
        return _config.TimeStepSeconds - secondsIntoStep;
    }

    /// <summary>
    /// Returns the current TOTP time window (Unix epoch divided by the step size).
    /// </summary>
    public long GetCurrentTimeWindow()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _config.TimeStepSeconds;
    }
}
