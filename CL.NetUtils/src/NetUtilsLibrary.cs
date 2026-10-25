using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using CodeLogic.Framework.Libraries;

namespace CL.NetUtils;

/// <summary>
/// <b>CL.NetUtils</b> — CodeLogic library providing DNSBL blacklist checking
/// and IP geolocation via the MaxMind GeoIP2 database.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="NetUtilsConfig"/> and <see cref="NetUtilsStrings"/>.</description></item>
///   <item><description><b>Initialize</b> — loads config, creates <see cref="DnsblChecker"/> and optionally <see cref="GeoIpService"/>.</description></item>
///   <item><description><b>Start</b> — logs that the library is ready.</description></item>
///   <item><description><b>Stop</b> — disposes the <see cref="GeoIpService"/> database reader.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class NetUtilsLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.NetUtils",
        Name = "Network Utilities Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "DNSBL blacklist checking and IP geolocation services",
        Author = "Zyntal.com",
        Tags = ["network", "dnsbl", "geoip", "ip"]
    };

    private LibraryContext? _context;
    private DnsblChecker? _dnsblChecker;
    private GeoIpService? _geoIpService;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<NetUtilsConfig>("netutils");
        context.Localization.Register<NetUtilsStrings>();

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<NetUtilsConfig>();

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
            return;
        }

        // DNSBL checker — always created when enabled
        if (config.Dnsbl.Enabled)
        {
            _dnsblChecker = new DnsblChecker(config.Dnsbl, context.Logger);
            context.Logger.Info("DNSBL checker initialized");
        }

        // GeoIP service — optional; failure is a warning, not a fatal error
        if (config.GeoIp.Enabled)
        {
            _geoIpService = new GeoIpService(config.GeoIp, context.Logger, context.DataDirectory);

            try
            {
                await _geoIpService.InitializeAsync();
            }
            catch (Exception ex)
            {
                context.Logger.Warning($"GeoIP service initialization failed (service will be unavailable): {ex.Message}");
                _geoIpService.Dispose();
                _geoIpService = null;
            }
        }

        context.Logger.Info($"{Manifest.Name} initialized");
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

        _geoIpService?.Dispose();
        _geoIpService = null;
        _dnsblChecker = null;

        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<NetUtilsConfig>();

        if (config is { Enabled: false })
            return Task.FromResult(HealthStatus.Healthy("NetUtils library is disabled"));

        if (_dnsblChecker is null && _geoIpService is null)
            return Task.FromResult(HealthStatus.Unhealthy("Not initialized"));

        if (_dnsblChecker is not null && _geoIpService is not null)
            return Task.FromResult(HealthStatus.Healthy("All network services operational"));

        if (_dnsblChecker is not null)
            return Task.FromResult(HealthStatus.Degraded("DNSBL available, GeoIP unavailable"));

        return Task.FromResult(HealthStatus.Degraded("GeoIP available, DNSBL unavailable"));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The DNSBL checker service.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the library has not been initialized or is disabled.
    /// </exception>
    public DnsblChecker Dnsbl =>
        _dnsblChecker ?? throw new InvalidOperationException("NetUtils not initialized or disabled");

    /// <summary>
    /// The GeoIP lookup service.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the GeoIP service is not available (database absent, not configured, or disabled).
    /// </exception>
    public GeoIpService GeoIp =>
        _geoIpService ?? throw new InvalidOperationException("GeoIP service not available");

    /// <summary>
    /// <see langword="true"/> when the GeoIP service has been successfully initialized.
    /// </summary>
    public bool HasGeoIp => _geoIpService is not null;

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _geoIpService?.Dispose();
        _geoIpService = null;
    }
}
