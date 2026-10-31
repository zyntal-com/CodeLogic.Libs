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
/// when configured thresholds are exceeded.
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
        await _provider.InitializeAsync();
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

    /// <summary>Gets a live CPU usage snapshot. Publishes <see cref="HighCpuUsageEvent"/> if threshold exceeded.</summary>
    public async Task<Result<CpuStats>> GetCpuStatsAsync()
    {
        var result = await GetCachedAsync("cpu_stats", () => _provider.GetCpuStatsAsync());

        if (result.IsSuccess)
            await CheckCpuThresholdAsync(result.Value!.OverallUsagePercent);

        return result;
    }

    /// <summary>Gets static memory information.</summary>
    public Task<Result<MemoryInfo>> GetMemoryInfoAsync() =>
        GetCachedAsync("mem_info", () => _provider.GetMemoryInfoAsync());

    /// <summary>Gets a live memory usage snapshot. Publishes <see cref="HighMemoryUsageEvent"/> if threshold exceeded.</summary>
    public async Task<Result<MemoryStats>> GetMemoryStatsAsync()
    {
        var result = await GetCachedAsync("mem_stats", () => _provider.GetMemoryStatsAsync());

        if (result.IsSuccess)
            await CheckMemoryThresholdAsync(result.Value!.UsagePercent);

        return result;
    }

    /// <summary>Gets the system uptime since last boot.</summary>
    public Task<Result<TimeSpan>> GetSystemUptimeAsync() =>
        GetCachedAsync("uptime", () => _provider.GetSystemUptimeAsync());

    /// <summary>Gets statistics for the specified process.</summary>
    public Task<Result<ProcessStats>> GetProcessStatsAsync(int processId) =>
        _provider.GetProcessStatsAsync(processId);   // process stats are not cached (volatile)

    /// <summary>Gets statistics for all running processes.</summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync() =>
        GetCachedAsync("all_processes", () => _provider.GetAllProcessesAsync());

    /// <summary>Gets the top processes sorted by CPU usage.</summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount) =>
        _provider.GetTopProcessesByCpuAsync(topCount);

    /// <summary>Gets the top processes sorted by memory usage.</summary>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount) =>
        _provider.GetTopProcessesByMemoryAsync(topCount);

    /// <summary>
    /// Gets a full system snapshot and publishes a <see cref="SystemSnapshotTakenEvent"/>.
    /// </summary>
    public async Task<Result<SystemSnapshot>> GetSystemSnapshotAsync()
    {
        var result = await _provider.GetSystemSnapshotAsync();

        if (result.IsSuccess)
        {
            var snap = result.Value!;

            await CheckCpuThresholdAsync(snap.CpuStats.OverallUsagePercent);
            await CheckMemoryThresholdAsync(snap.MemoryStats.UsagePercent);

            if (_eventBus is not null)
            {
                var evt = new SystemSnapshotTakenEvent(
                    snap.CpuStats.OverallUsagePercent,
                    snap.MemoryStats.UsagePercent,
                    snap.Uptime,
                    snap.TakenAt);

                await _eventBus.PublishAsync(evt);
            }
        }

        return result;
    }

    // ── Async dispose ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cache.Clear();
        await _provider.DisposeAsync();
        _logger?.Info("SystemStatsService disposed");
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private async Task<Result<T>> GetCachedAsync<T>(string key, Func<Task<Result<T>>> factory)
    {
        if (!_config.EnableCaching)
            return await factory();

        if (TryGetFresh<T>(key, out var cached))
            return cached;

        // One refresh per key at a time: concurrent callers that find the entry stale share the sampling
        // instead of each running it (CPU sampling waits CpuSamplingIntervalMs and process listings are costly).
        var mine = new Lazy<Task<Result<T>>>(() => RefreshAsync(key, factory));
        var refresh = (Lazy<Task<Result<T>>>)_refreshes.GetOrAdd(key, mine);
        try
        {
            return await refresh.Value;
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

        var result = await factory();
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
            await _eventBus.PublishAsync(new HighCpuUsageEvent(usagePct, _config.HighCpuThresholdPercent, DateTime.UtcNow));
        }
    }

    private async Task CheckMemoryThresholdAsync(double usagePct)
    {
        if (_eventBus is not null && usagePct >= _config.HighMemoryThresholdPercent)
        {
            _logger?.Warning($"High memory usage detected: {usagePct:F1}% (threshold: {_config.HighMemoryThresholdPercent}%)");
            await _eventBus.PublishAsync(new HighMemoryUsageEvent(usagePct, _config.HighMemoryThresholdPercent, DateTime.UtcNow));
        }
    }
}
