using CL.TwoFactorAuth.Models;
using CL.TwoFactorAuth.Services;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.TwoFactorAuth;

/// <summary>
/// <b>CL.TwoFactorAuth</b> — CodeLogic library providing TOTP two-factor authentication
/// and QR code generation for authenticator app provisioning.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="TwoFactorAuthConfig"/> (<c>config.twofactorauth.json</c>).</description></item>
///   <item><description><b>Initialize</b> — validates config, creates authenticator and QR generator services.</description></item>
///   <item><description><b>Start</b> — logs that the library is ready.</description></item>
///   <item><description><b>Stop</b> — clears service references.</description></item>
/// </list>
/// </para>
/// <para>
/// Access services via the public properties:
/// <list type="bullet">
///   <item><see cref="Authenticator"/> — TOTP generation and validation.</item>
///   <item><see cref="QrCode"/> — QR code generation in PNG/BMP/Base64/DataUri formats.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TwoFactorAuthLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.TwoFactorAuth",
        Name = "Two-Factor Auth Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "TOTP two-factor authentication with QR code generation for CodeLogic 4 applications",
        Author = "Zyntal.com",
        Tags = ["security", "2fa", "totp", "qrcode", "authentication"]
    };

    private LibraryContext? _context;
    private TwoFactorAuthenticator? _authenticator;
    private QrCodeGenerator? _qrCode;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<TwoFactorAuthConfig>("twofactorauth");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<TwoFactorAuthConfig>();

        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join(", ", validation.Errors);
            context.Logger.Error($"{Manifest.Name} configuration is invalid: {errors}");
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        _authenticator = new TwoFactorAuthenticator(config, context.Logger, context.Events);
        _qrCode = new QrCodeGenerator(config, context.Logger);

        context.Logger.Info($"{Manifest.Name} initialized (timeStep={config.TimeStepSeconds}s, window={config.WindowSize})");
        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} started");
        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _authenticator = null;
        _qrCode = null;

        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<TwoFactorAuthConfig>();

        if (config is { Enabled: false })
            return Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is disabled"));

        if (_authenticator is null)
            return Task.FromResult(HealthStatus.Unhealthy("Not initialized"));

        try
        {
            _ = _authenticator.GenerateSecretKey();
            return Task.FromResult(HealthStatus.Healthy("TOTP key generation operational"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthStatus.Unhealthy($"TOTP key generation failed: {ex.Message}"));
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The TOTP authenticator service for generating and validating one-time passwords.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public TwoFactorAuthenticator Authenticator =>
        _authenticator ?? throw new InvalidOperationException("TwoFactorAuth service not available — check configuration or initialization");

    /// <summary>
    /// The QR code generator service for producing authenticator app provisioning QR codes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public QrCodeGenerator QrCode =>
        _qrCode ?? throw new InvalidOperationException("QrCodeGenerator service not available — check configuration or initialization");

    // ── Convenience pass-throughs ─────────────────────────────────────────────

    /// <summary>Generates a new random Base32-encoded TOTP secret key.</summary>
    public string GenerateSecretKey() => Authenticator.GenerateSecretKey();

    /// <summary>Generates a new <see cref="TwoFactorKey"/> for the given issuer and user.</summary>
    /// <param name="issuer">The service or application name.</param>
    /// <param name="user">The user account name (typically an email address).</param>
    public TwoFactorKey GenerateNewKey(string issuer, string user) => Authenticator.GenerateNewKey(issuer, user);

    /// <summary>Validates a TOTP code against the given secret key.</summary>
    /// <param name="code">The 6-digit TOTP code to validate.</param>
    /// <param name="secretKey">The Base32-encoded TOTP secret key.</param>
    public TotpValidationResult ValidateTotp(string code, string secretKey) => Authenticator.ValidateTotp(code, secretKey);

    /// <summary>
    /// Generates a QR code data URI for embedding in HTML.
    /// Format: <c>data:image/png;base64,{base64data}</c>
    /// </summary>
    /// <param name="key">The two-factor key to encode.</param>
    public Result<string> GenerateQrCodeDataUri(TwoFactorKey key) => QrCode.GenerateQrCodeDataUri(key);

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _authenticator = null;
        _qrCode = null;
    }
}
