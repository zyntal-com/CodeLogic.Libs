using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CL.SystemStats.Services;
using CL.SystemStats.Services.Providers;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.SystemStats;

/// <summary>
/// <b>CL.SystemStats</b> — CodeLogic library providing cross-platform system statistics
/// (CPU, memory, uptime, and process monitoring) for Windows and Linux.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="SystemStatsConfig"/>.</description></item>
///   <item><description><b>Initialize</b> — creates the appropriate platform provider and <see cref="SystemStatsService"/>.</description></item>
///   <item><description><b>Start</b> — logs that the library is ready.</description></item>
///   <item><description><b>Stop</b> — disposes the stats service and provider.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SystemStatsLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.SystemStats",
        Name = "System Stats Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Cross-platform system statistics (CPU, memory, processes) for CodeLogic 5 applications",
        Author = "Zyntal.com",
        Tags = ["system", "stats", "cpu", "memory", "processes", "monitoring"]
    };

    private LibraryContext? _context;
    private SystemStatsService? _service;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<SystemStatsConfig>("systemstats");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<SystemStatsConfig>();

        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join(", ", validation.Errors);
            context.Logger.Error($"{Manifest.Name} configuration is invalid: {errors}");
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        var detector = new PlatformDetector();
        context.Logger.Info($"Detected platform: {detector.Platform}");

        ISystemStatsProvider provider = CreateProvider(detector, config, context);

        _service = new SystemStatsService(provider, config, context.Events, context.Logger);

        await _service.InitializeAsync();

        context.Logger.Info($"{Manifest.Name} initialized — using {_service.GetPlatformInfo()}");
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
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        if (_service is not null)
        {
            await _service.DisposeAsync();
            _service = null;
        }

        _context?.Logger.Info($"{Manifest.Name} stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (_service is null || !_service.IsInitialized)
            return HealthStatus.Unhealthy("SystemStats service not initialized");

        try
        {
            var result = await _service.GetCpuStatsAsync();
            return result.IsSuccess
                ? HealthStatus.Healthy($"CPU: {result.Value!.OverallUsagePercent:F1}% | Platform: {_service.GetPlatformInfo()}")
                : HealthStatus.Degraded($"CPU stats unavailable: {result.Error}");
        }
        catch (Exception ex)
        {
            return HealthStatus.Unhealthy($"Health check failed: {ex.Message}");
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// The underlying <see cref="SystemStatsService"/> instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the library has not been initialized.</exception>
    public SystemStatsService Stats =>
        _service ?? throw new InvalidOperationException("SystemStats library not initialized");

    /// <summary>Gets static CPU information.</summary>
    public Task<Result<Models.CpuInfo>> GetCpuInfoAsync() => Stats.GetCpuInfoAsync();

    /// <summary>Gets a live CPU usage snapshot.</summary>
    public Task<Result<Models.CpuStats>> GetCpuStatsAsync() => Stats.GetCpuStatsAsync();

    /// <summary>Gets static memory information.</summary>
    public Task<Result<Models.MemoryInfo>> GetMemoryInfoAsync() => Stats.GetMemoryInfoAsync();

    /// <summary>Gets a live memory usage snapshot.</summary>
    public Task<Result<Models.MemoryStats>> GetMemoryStatsAsync() => Stats.GetMemoryStatsAsync();

    /// <summary>Gets the system uptime since last boot.</summary>
    public Task<Result<TimeSpan>> GetSystemUptimeAsync() => Stats.GetSystemUptimeAsync();

    /// <summary>Gets statistics for the specified process.</summary>
    public Task<Result<Models.ProcessStats>> GetProcessStatsAsync(int processId) =>
        Stats.GetProcessStatsAsync(processId);

    /// <summary>Gets statistics for all running processes.</summary>
    public Task<Result<IReadOnlyList<Models.ProcessStats>>> GetAllProcessesAsync() =>
        Stats.GetAllProcessesAsync();

    /// <summary>Gets the top processes sorted by CPU usage.</summary>
    public Task<Result<IReadOnlyList<Models.ProcessStats>>> GetTopProcessesByCpuAsync(int topCount) =>
        Stats.GetTopProcessesByCpuAsync(topCount);

    /// <summary>Gets the top processes sorted by memory usage.</summary>
    public Task<Result<IReadOnlyList<Models.ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount) =>
        Stats.GetTopProcessesByMemoryAsync(topCount);

    /// <summary>Gets the temperature sensor readings (honours <c>EnableTemperatureMonitoring</c>).</summary>
    public Task<Result<IReadOnlyList<Models.TemperatureReading>>> GetTemperaturesAsync() =>
        Stats.GetTemperaturesAsync();

    /// <summary>Gets a full system snapshot.</summary>
    public Task<Result<Models.SystemSnapshot>> GetSystemSnapshotAsync() =>
        Stats.GetSystemSnapshotAsync();

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _service?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _service = null;
    }

    // ── Provider factory ──────────────────────────────────────────────────────

    private static ISystemStatsProvider CreateProvider(
        PlatformDetector detector,
        SystemStatsConfig config,
        LibraryContext context)
    {
        if (detector.IsWindows)
        {
            if (OperatingSystem.IsWindows())
                return new WindowsSystemStatsProvider(config, context.Logger);
        }

        if (detector.IsLinux)
            return new LinuxSystemStatsProvider(config, context.Logger);

        // Fallback: use Linux provider on unknown platforms (e.g. macOS /proc is absent but
        // provider will fail gracefully on unsupported platforms)
        context.Logger.Warning($"Unsupported platform '{detector.Platform}' — falling back to Linux provider");
        return new LinuxSystemStatsProvider(config, context.Logger);
    }
}
