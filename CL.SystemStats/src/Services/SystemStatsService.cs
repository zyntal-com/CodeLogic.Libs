using System.Collections.Concurrent;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Events;
using CL.SystemStats.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services;

/// <summary>
/// Caching facade around an <see cref="ISystemStatsProvider"/>.
/// Automatically publishes <see cref="HighCpuUsageEvent"/> and <see cref="HighMemoryUsageEvent"/>
/// when configured thresholds are exceeded, smooths CPU readings over
/// <see cref="SystemStatsConfig.CpuSamplesForAverage"/> samples, and enforces
/// <see cref="SystemStatsConfig.EnableProcessMonitoring"/> and <see cref="SystemStatsConfig.EnableTemperatureMonitoring"/>.
/// </summary>
public sealed class SystemStatsService : IAsyncDisposable
{
    private readonly ISystemStatsProvider _provider;
    private readonly SystemStatsConfig _config;
    private readonly IEventBus? _eventBus;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, (object Value, DateTime CachedAt)> _cache = new();
    // In-flight refreshes per cache key (a Lazy<Task<Result<T>>>), so concurrent misses share one provider call.
    private readonly ConcurrentDictionary<string, object> _refreshes = new();

    // The last CpuSamplesForAverage provider readings; a CPU reading is their average.
    private readonly Queue<CpuStats> _cpuWindow = new();
    private readonly Lock _cpuWindowLock = new();

    // One provider CPU sampling at a time: a snapshot samples directly (it bypasses the cache so its reading
    // is fresh) while GetCpuStatsAsync refreshes the cache, and concurrent PerformanceCounter.NextValue()
    // calls on the Windows counters corrupt both readings. Never disposed (a disposed SemaphoreSlim leaves
    // waiters hanging); it holds no unmanaged resources.
    private readonly SemaphoreSlim _cpuSampling = new(1, 1);

    /// <summary>
    /// Returns <see langword="true"/> after <see cref="InitializeAsync"/> has completed successfully.
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Initializes a new <see cref="SystemStatsService"/>.
    /// </summary>
    /// <param name="provider">The underlying platform-specific provider.</param>
    /// <param name="config">Library configuration.</param>
    /// <param name="eventBus">Optional event bus for threshold alerts.</param>
    /// <param name="logger">Optional logger.</param>
    public SystemStatsService(
        ISystemStatsProvider provider,
        SystemStatsConfig config,
        IEventBus? eventBus = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _config = config;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the underlying provider.
    /// Must be called before any stats methods are used.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _provider.InitializeAsync().ConfigureAwait(false);
        IsInitialized = true;
        _logger?.Info("SystemStatsService initialized");
    }

    /// <summary>Clears all cached values.</summary>
    public void ClearCache() => _cache.Clear();

    /// <summary>Returns a human-readable string describing the active platform provider.</summary>
    public string GetPlatformInfo()
    {
        return _provider.GetType().Name switch
        {
            "WindowsSystemStatsProvider" => "Windows (PerformanceCounters + P/Invoke GlobalMemoryStatusEx)",
            "LinuxSystemStatsProvider"   => "Linux (/proc filesystem)",
            _                            => _provider.GetType().Name
        };
    }

    // ── Public API — all calls go through the cache ───────────────────────────

    /// <summary>Gets static CPU information.</summary>
    public Task<Result<CpuInfo>> GetCpuInfoAsync() =>
        GetCachedAsync("cpu_info", () => _provider.GetCpuInfoAsync());

    /// <summary>
    /// Gets a live CPU usage reading: the average of the last <see cref="SystemStatsConfig.CpuSamplesForAverage"/>
    /// provider samples (the newest included), so a momentary spike does not dominate. Publishes
    /// <see cref="HighCpuUsageEvent"/> if the smoothed value reaches the threshold.
    /// </summary>
    public async Task<Result<CpuStats>> GetCpuStatsAsync()
    {
        var result = await GetCachedAsync("cpu_stats", SampleCpuAsync).ConfigureAwait(false);

        if (result.IsSuccess)
            await CheckCpuThresholdAsync(result.Value!.OverallUsagePercent).ConfigureAwait(false);

        return result;
    }

    /// <summary>Gets static memory information.</summary>
    public Task<Result<MemoryInfo>> GetMemoryInfoAsync() =>
        GetCachedAsync("mem_info", () => _provider.GetMemoryInfoAsync());

    /// <summary>Gets a live memory usage snapshot. Publishes <see cref="HighMemoryUsageEvent"/> if threshold exceeded.</summary>
    public async Task<Result<MemoryStats>> GetMemoryStatsAsync()
    {
        var result = await GetCachedAsync("mem_stats", () => _provider.GetMemoryStatsAsync()).ConfigureAwait(false);

        if (result.IsSuccess)
            await CheckMemoryThresholdAsync(result.Value!.UsagePercent).ConfigureAwait(false);

        return result;
    }

    /// <summary>Gets the system uptime since last boot.</summary>
    public Task<Result<TimeSpan>> GetSystemUptimeAsync() =>
        GetCachedAsync("uptime", () => _provider.GetSystemUptimeAsync());

    /// <summary>
    /// Gets statistics for the specified process. Fails with <see cref="SystemStatsErrorCodes.ProcessMonitoringDisabled"/>
    /// when <see cref="SystemStatsConfig.EnableProcessMonitoring"/> is off.
    /// </summary>
    public Task<Result<ProcessStats>> GetProcessStatsAsync(int processId) =>
        _config.EnableProcessMonitoring
            ? _provider.GetProcessStatsAsync(processId)   // process stats are not cached (volatile)
            : Task.FromResult(Result<ProcessStats>.Failure(ProcessMonitoringDisabled()));

    /// <summary>
    /// Gets statistics for all running processes. Fails with <see cref="SystemStatsErrorCodes.ProcessMonitoringDisabled"/>
    /// when <see cref="SystemStatsConfig.EnableProcessMonitoring"/> is off.
    /// </summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync() =>
        _config.EnableProcessMonitoring
            ? GetCachedAsync("all_processes", () => _provider.GetAllProcessesAsync())
            : Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Failure(ProcessMonitoringDisabled()));

    /// <summary>
    /// Gets the top processes sorted by CPU usage. Fails with <see cref="SystemStatsErrorCodes.ProcessMonitoringDisabled"/>
    /// when <see cref="SystemStatsConfig.EnableProcessMonitoring"/> is off.
    /// </summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount) =>
        _config.EnableProcessMonitoring
            ? _provider.GetTopProcessesByCpuAsync(topCount)
            : Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Failure(ProcessMonitoringDisabled()));

    /// <summary>
    /// Gets the top processes sorted by memory usage. Fails with <see cref="SystemStatsErrorCodes.ProcessMonitoringDisabled"/>
    /// when <see cref="SystemStatsConfig.EnableProcessMonitoring"/> is off.
    /// </summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount) =>
        _config.EnableProcessMonitoring
            ? _provider.GetTopProcessesByMemoryAsync(topCount)
            : Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Failure(ProcessMonitoringDisabled()));

    /// <summary>
    /// Gets the temperature sensor readings (cached like the other reads). Fails with
    /// <see cref="SystemStatsErrorCodes.TemperatureMonitoringDisabled"/> when
    /// <see cref="SystemStatsConfig.EnableTemperatureMonitoring"/> is off, and with
    /// <see cref="SystemStatsErrorCodes.TemperatureUnavailable"/> where the platform exposes no sensors the library can
    /// read (Windows; Linux without <c>/sys/class/thermal</c> or <c>/sys/class/hwmon</c> sensors).
    /// </summary>
    public Task<Result<IReadOnlyList<TemperatureReading>>> GetTemperaturesAsync() =>
        _config.EnableTemperatureMonitoring
            ? GetCachedAsync("temperatures", () => _provider.GetTemperaturesAsync())
            : Task.FromResult(Result<IReadOnlyList<TemperatureReading>>.Failure(Error.Unavailable(
                SystemStatsErrorCodes.TemperatureMonitoringDisabled,
                "Temperature monitoring is disabled (systemstats.enableTemperatureMonitoring)")));

    /// <summary>
    /// Gets a full system snapshot (fresh CPU, memory and uptime readings, and the top
    /// <see cref="SystemStatsConfig.MaxTopProcesses"/> processes by CPU and memory) and publishes a
    /// <see cref="SystemSnapshotTakenEvent"/>. The CPU reading is smoothed like <see cref="GetCpuStatsAsync"/>; with
    /// <see cref="SystemStatsConfig.EnableProcessMonitoring"/> off the process lists are empty and no process is sampled.
    /// </summary>
    public async Task<Result<SystemSnapshot>> GetSystemSnapshotAsync()
    {
        var result = await TakeSnapshotAsync().ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var snap = result.Value!;

            await CheckCpuThresholdAsync(snap.CpuStats.OverallUsagePercent).ConfigureAwait(false);
            await CheckMemoryThresholdAsync(snap.MemoryStats.UsagePercent).ConfigureAwait(false);

            if (_eventBus is not null)
            {
                var evt = new SystemSnapshotTakenEvent(
                    snap.CpuStats.OverallUsagePercent,
                    snap.MemoryStats.UsagePercent,
                    snap.Uptime,
                    snap.TakenAt);

                await _eventBus.PublishAsync(evt).ConfigureAwait(false);
            }
        }

        return result;
    }

    private async Task<Result<SystemSnapshot>> TakeSnapshotAsync()
    {
        var cpu = await SampleCpuAsync().ConfigureAwait(false);
        if (cpu.IsFailure)
            return Result<SystemSnapshot>.Failure(cpu.Error!);

        var mem = await _provider.GetMemoryStatsAsync().ConfigureAwait(false);
        if (mem.IsFailure)
            return Result<SystemSnapshot>.Failure(mem.Error!);

        var uptime = await _provider.GetSystemUptimeAsync().ConfigureAwait(false);
        if (uptime.IsFailure)
            return Result<SystemSnapshot>.Failure(uptime.Error!);

        IReadOnlyList<ProcessStats> topCpu = [], topMem = [];
        if (_config.EnableProcessMonitoring)
        {
            var byCpu = await _provider.GetTopProcessesByCpuAsync(_config.MaxTopProcesses).ConfigureAwait(false);
            var byMem = await _provider.GetTopProcessesByMemoryAsync(_config.MaxTopProcesses).ConfigureAwait(false);
            if (byCpu.IsSuccess) topCpu = byCpu.Value!;
            if (byMem.IsSuccess) topMem = byMem.Value!;
        }

        return Result<SystemSnapshot>.Success(new SystemSnapshot(
            cpu.Value!, mem.Value!, uptime.Value!, topCpu, topMem, DateTime.UtcNow));
    }

    private static Error ProcessMonitoringDisabled() => Error.Unavailable(
        SystemStatsErrorCodes.ProcessMonitoringDisabled,
        "Process monitoring is disabled (systemstats.enableProcessMonitoring)");

    // ── CPU smoothing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Takes a provider CPU sample and returns the average over the sample window. Samplings are serialised:
    /// a snapshot and a cache refresh that overlap take their samples one after the other.
    /// </summary>
    private async Task<Result<CpuStats>> SampleCpuAsync()
    {
        Result<CpuStats> sample;
        await _cpuSampling.WaitAsync().ConfigureAwait(false);
        try
        {
            sample = await _provider.GetCpuStatsAsync().ConfigureAwait(false);
        }
        finally
        {
            _cpuSampling.Release();
        }
        return sample.IsSuccess ? Result<CpuStats>.Success(AddCpuSample(sample.Value!)) : sample;
    }

    /// <summary>
    /// Adds a sample to the window of the last <see cref="SystemStatsConfig.CpuSamplesForAverage"/> samples and returns
    /// their average (overall and per core, rounded to 2 decimals, stamped with the newest sample's time). A sample whose
    /// core count differs from the window's restarts the window.
    /// </summary>
    internal CpuStats AddCpuSample(CpuStats sample)
    {
        var size = Math.Max(1, _config.CpuSamplesForAverage);
        lock (_cpuWindowLock)
        {
            if (_cpuWindow.Count > 0 && _cpuWindow.Peek().PerCoreUsagePercent.Count != sample.PerCoreUsagePercent.Count)
                _cpuWindow.Clear();
            _cpuWindow.Enqueue(sample);
            while (_cpuWindow.Count > size)
                _cpuWindow.Dequeue();

            if (_cpuWindow.Count == 1)
                return sample;

            var overall = Math.Round(_cpuWindow.Average(s => s.OverallUsagePercent), 2);
            var perCore = new double[sample.PerCoreUsagePercent.Count];
            for (var i = 0; i < perCore.Length; i++)
                perCore[i] = Math.Round(_cpuWindow.Average(s => s.PerCoreUsagePercent[i]), 2);
            return new CpuStats(overall, perCore, sample.SampledAt);
        }
    }

    // ── Async dispose ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cache.Clear();
        await _provider.DisposeAsync().ConfigureAwait(false);
        _logger?.Info("SystemStatsService disposed");
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private async Task<Result<T>> GetCachedAsync<T>(string key, Func<Task<Result<T>>> factory)
    {
        if (!_config.EnableCaching)
            return await factory().ConfigureAwait(false);

        if (TryGetFresh<T>(key, out var cached))
            return cached;

        // One refresh per key at a time: concurrent callers that find the entry stale share the sampling
        // instead of each running it (CPU sampling waits CpuSamplingIntervalMs and process listings are costly).
        var mine = new Lazy<Task<Result<T>>>(() => RefreshAsync(key, factory));
        var refresh = (Lazy<Task<Result<T>>>)_refreshes.GetOrAdd(key, mine);
        try
        {
            return await refresh.Value.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(refresh, mine))
                _refreshes.TryRemove(new KeyValuePair<string, object>(key, mine));
        }
    }

    private async Task<Result<T>> RefreshAsync<T>(string key, Func<Task<Result<T>>> factory)
    {
        // A refresh that finished just before this one started already has a fresh value.
        if (TryGetFresh<T>(key, out var cached))
            return cached;

        var result = await factory().ConfigureAwait(false);
        if (result.IsSuccess)
            _cache[key] = (result, DateTime.UtcNow);
        return result;
    }

    private bool TryGetFresh<T>(string key, out Result<T> value)
    {
        if (_cache.TryGetValue(key, out var entry) &&
            (DateTime.UtcNow - entry.CachedAt).TotalSeconds < _config.CacheDurationSeconds)
        {
            value = (Result<T>)entry.Value;
            return true;
        }
        value = default!;
        return false;
    }

    private async Task CheckCpuThresholdAsync(double usagePct)
    {
        if (_eventBus is not null && usagePct >= _config.HighCpuThresholdPercent)
        {
            _logger?.Warning($"High CPU usage detected: {usagePct:F1}% (threshold: {_config.HighCpuThresholdPercent}%)");
            await _eventBus.PublishAsync(new HighCpuUsageEvent(usagePct, _config.HighCpuThresholdPercent, DateTime.UtcNow)).ConfigureAwait(false);
        }
    }

    private async Task CheckMemoryThresholdAsync(double usagePct)
    {
        if (_eventBus is not null && usagePct >= _config.HighMemoryThresholdPercent)
        {
            _logger?.Warning($"High memory usage detected: {usagePct:F1}% (threshold: {_config.HighMemoryThresholdPercent}%)");
            await _eventBus.PublishAsync(new HighMemoryUsageEvent(usagePct, _config.HighMemoryThresholdPercent, DateTime.UtcNow)).ConfigureAwait(false);
        }
    }
}
