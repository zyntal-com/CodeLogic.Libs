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
    private bool _initialized;
    private bool _initializeRan;
    private string _geoIpState = "off";   // "on", "off", "not configured" or "failed: …"
    private DnsblChecker? _disabledDnsbl;

    /// <summary>Localized messages (the English defaults until CodeLogic has loaded the translations).</summary>
    private NetUtilsStrings Strings
    {
        get
        {
            try { return _context?.Localization.Get<NetUtilsStrings>() ?? InputText.DefaultStrings; }
            catch { return InputText.DefaultStrings; }
        }
    }

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

        _initializeRan = true;

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return;
        }

        // DNSBL checker — created when enabled; otherwise Dnsbl hands out a checker that reports "disabled"
        if (config.Dnsbl.Enabled)
        {
            _dnsblChecker = new DnsblChecker(config.Dnsbl, context.Logger) { Strings = () => Strings };
            context.Logger.Info("DNSBL checker initialized");
        }

        // GeoIP service — optional; failure is a warning, not a fatal error
        if (config.GeoIp.Enabled)
        {
            var geoIp = new GeoIpService(config.GeoIp, context.Logger, context.DataDirectory) { Strings = () => Strings };

            if (!File.Exists(geoIp.DatabasePath) && !geoIp.CanDownload)
            {
                // Nothing to open and nothing to download with: GeoIP was simply never set up.
                context.Logger.Info("GeoIP not configured (no database file and no MaxMind credentials) — lookups are off");
                geoIp.Dispose();
                _geoIpState = "not configured";
            }
            else
            {
                try
                {
                    await geoIp.InitializeAsync();
                    _geoIpService = geoIp;
                    _geoIpState = "on";
                }
                catch (Exception ex)
                {
                    context.Logger.Warning(InputText.Format(Strings.GeoIpUnavailable, ex.Message));
                    geoIp.Dispose();
                    _geoIpState = $"failed: {ex.Message}";
                }
            }
        }

        _initialized = true;
        context.Logger.Info(Strings.LibraryInitialized);
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info(Strings.LibraryStarted);
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
        _initialized = false;
        _initializeRan = false;

        _context?.Logger.Info(Strings.LibraryStopped);
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _context?.Configuration.Get<NetUtilsConfig>();

        if (config is { Enabled: false })
            return Task.FromResult(HealthStatus.Healthy("NetUtils library is disabled"));

        if (!_initialized)
            return Task.FromResult(HealthStatus.Unhealthy("Not initialized"));

        // A service that is switched off or was never set up is not a fault; only a GeoIP that was
        // set up (database file or MaxMind credentials) and still failed to start degrades the library.
        var summary = $"DNSBL {(_dnsblChecker is not null ? "on" : "off")}, GeoIP {_geoIpState}";
        return Task.FromResult(_geoIpState.StartsWith("failed", StringComparison.Ordinal)
            ? HealthStatus.Degraded(summary)
            : HealthStatus.Healthy(summary));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The DNSBL checker service. Always available once the library has initialized: while DNSBL checking is switched
    /// off (<c>enabled</c> or <c>dnsbl.enabled</c> is <see langword="false"/>) it is a checker whose
    /// <see cref="DnsblChecker.IsEnabled"/> is <see langword="false"/> and whose checks return
    /// <see cref="DnsblCheckStatus.Error"/> with a "DNSBL checking is disabled" message, without any DNS query.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before the library has initialized.</exception>
    public DnsblChecker Dnsbl
    {
        get
        {
            if (_dnsblChecker is { } checker) return checker;
            if (!_initializeRan) throw new InvalidOperationException("NetUtils not initialized");
            return _disabledDnsbl ??= CreateDisabledDnsbl();
        }
    }

    private DnsblChecker CreateDisabledDnsbl()
    {
        var checker = DnsblChecker.CreateDisabled(_context?.Logger);
        checker.Strings = () => Strings;
        return checker;
    }

    /// <summary><see langword="true"/> when DNSBL checking is enabled and the checker is running.</summary>
    public bool HasDnsbl => _dnsblChecker is not null;

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
