using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CL.NetUtils.Services;
using CodeLogic.Core.Configuration;
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
///   <item><description><b>Stop</b> — cancels a GeoIP download still running and disposes the <see cref="GeoIpService"/> database reader.</description></item>
/// </list>
/// Configuration changes are applied live; see <see cref="OnConfigChangedAsync"/>.
/// </para>
/// </summary>
public sealed class NetUtilsLibrary : ILibrary, IConfigReloadable
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

    /// <summary>How long a replaced <see cref="GeoIpService"/> stays open after a config change, so lookups already running on it finish.</summary>
    private static readonly TimeSpan RetireGrace = TimeSpan.FromSeconds(10);

    private LibraryContext? _context;
    private DnsblChecker? _dnsblChecker;
    private GeoIpService? _geoIpService;
    private bool _initialized;
    private bool _initializeRan;
    private string _geoIpState = "off";   // "on", "off", "not configured" or "failed: …"
    private DnsblChecker? _disabledDnsbl;
    // The configuration the running services were built from (a change is compared with what runs, not with the previous file).
    private NetUtilsConfig? _applied;
    // Cancelled when the library stops, so an initialization or reload still downloading a database gives up with it.
    private CancellationTokenSource? _stopping;

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
        _stopping?.Dispose();
        _stopping = new CancellationTokenSource();

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            _applied = config;
            return;
        }

        BuildDnsbl(context, config);
        await BuildGeoIpAsync(context, config, keepPreviousOnFailure: false, _stopping.Token).ConfigureAwait(false);
        _applied = config;

        _initialized = true;
        context.Logger.Info(Strings.LibraryInitialized);
    }

    /// <summary>Creates the DNSBL checker for <paramref name="config"/>, or none when DNSBL checking is off.</summary>
    private void BuildDnsbl(LibraryContext context, NetUtilsConfig config)
    {
        if (config is { Enabled: true, Dnsbl.Enabled: true })
        {
            _dnsblChecker = new DnsblChecker(config.Dnsbl, context.Logger) { Strings = () => Strings };
            context.Logger.Info("DNSBL checker initialized");
        }
        else
        {
            // Dnsbl hands out a checker that reports "disabled" meanwhile.
            _dnsblChecker = null;
        }
    }

    /// <summary>
    /// Builds and initializes the GeoIP service for <paramref name="config"/> (downloading a missing database when
    /// credentials allow). A failure is a warning, not a fatal error: the state goes to "failed" and, when
    /// <paramref name="keepPreviousOnFailure"/> is set (a live config change), the service built before keeps serving.
    /// </summary>
    private async Task BuildGeoIpAsync(LibraryContext context, NetUtilsConfig config, bool keepPreviousOnFailure, CancellationToken cancellationToken)
    {
        if (config is not { Enabled: true, GeoIp.Enabled: true })
        {
            _geoIpService = null;
            _geoIpState = "off";
            return;
        }

        var geoIp = new GeoIpService(config.GeoIp, context.Logger, context.DataDirectory) { Strings = () => Strings };

        if (!File.Exists(geoIp.DatabasePath) && !geoIp.CanDownload)
        {
            // Nothing to open and nothing to download with: GeoIP was simply never set up.
            context.Logger.Info("GeoIP not configured (no database file and no MaxMind credentials) — lookups are off");
            geoIp.Dispose();
            _geoIpService = null;
            _geoIpState = "not configured";
            return;
        }

        try
        {
            await geoIp.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _geoIpService = geoIp;
            _geoIpState = "on";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            geoIp.Dispose();
            _geoIpState = "off";
            throw;
        }
        catch (Exception ex)
        {
            context.Logger.Warning(InputText.Format(Strings.GeoIpUnavailable, ex.Message));
            geoIp.Dispose();
            if (keepPreviousOnFailure && _geoIpService is not null)
            {
                _geoIpState = $"failed: {ex.Message} (the previous database stays in use)";
            }
            else
            {
                _geoIpService = null;
                _geoIpState = $"failed: {ex.Message}";
            }
        }
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

        _stopping?.Cancel();
        _stopping?.Dispose();
        _stopping = null;
        _geoIpService?.Dispose();
        _geoIpService = null;
        _dnsblChecker = null;
        _applied = null;
        _initialized = false;
        _initializeRan = false;

        _context?.Logger.Info(Strings.LibraryStopped);
        return Task.CompletedTask;
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart, rebuilding only what changed: DNSBL settings replace
    /// <see cref="Dnsbl"/> at once (zone lists, timeout, parallelism, logging, on/off); GeoIP settings build a new
    /// <see cref="GeoIpService"/> (downloading a database when needed) and swap it in, the old one closing after a
    /// grace period so lookups already running on it finish. A GeoIP configuration that fails to initialize is logged,
    /// the previous service keeps serving, and health reports Degraded. Turning the whole library on or off is applied
    /// too. A configuration that fails <see cref="NetUtilsConfig.Validate"/> is logged and ignored: the running
    /// services keep the last valid one. A new <c>GeoIp.DatabasePath</c> takes effect as well, so the field's
    /// "requires restart" marker is only advice.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (change.NewValue is not NetUtilsConfig current) return;

        var validation = current.Validate();
        if (!validation.IsValid)
        {
            context.Logger.Error($"{Manifest.Name}: ignored invalid configuration, keeping the current one: {string.Join("; ", validation.Errors)}");
            return;
        }

        // Compare with what is actually running, not with the previous (possibly ignored) value.
        var previous = _applied ?? change.GetOld<NetUtilsConfig>();
        _applied = current;

        if (!ConfigDiff.ValueEquals(ActiveDnsbl(previous), ActiveDnsbl(current)))
        {
            BuildDnsbl(context, current);
            context.Logger.Info(_dnsblChecker is null ? "DNSBL checking turned off" : "DNSBL settings applied");
        }

        if (!ConfigDiff.ValueEquals(ActiveGeoIp(previous), ActiveGeoIp(current)))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping?.Token ?? CancellationToken.None);
            var old = _geoIpService;
            await BuildGeoIpAsync(context, current, keepPreviousOnFailure: true, linked.Token).ConfigureAwait(false);
            if (!ReferenceEquals(old, _geoIpService))
            {
                RetireLater(old);
                context.Logger.Info(_geoIpService is null ? $"GeoIP service turned off ({_geoIpState})" : "GeoIP settings applied");
            }
        }

        _initialized = current.Enabled;
    }

    private static DnsblConfig? ActiveDnsbl(NetUtilsConfig? config) =>
        config is { Enabled: true, Dnsbl.Enabled: true } ? config.Dnsbl : null;

    private static GeoIpConfig? ActiveGeoIp(NetUtilsConfig? config) =>
        config is { Enabled: true, GeoIp.Enabled: true } ? config.GeoIp : null;

    /// <summary>Disposes a replaced GeoIP service after <see cref="RetireGrace"/>, so lookups that already hold it finish.</summary>
    private static void RetireLater(GeoIpService? old)
    {
        if (old is null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(RetireGrace).ConfigureAwait(false);
            old.Dispose();
        });
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var config = _applied ?? _context?.Configuration.Get<NetUtilsConfig>();

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
    /// Read it per use rather than caching it: a config change replaces it.
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
    /// The GeoIP lookup service. Read it per use rather than caching it: a config change replaces it.
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
        _stopping?.Cancel();
        _stopping?.Dispose();
        _stopping = null;
        _geoIpService?.Dispose();
        _geoIpService = null;
    }
}
