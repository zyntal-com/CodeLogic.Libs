using CodeLogic;                          // Libraries, CodeLogicOptions
using CodeLogic.Framework.Libraries;      // HealthStatusLevel
using CL.SystemStats;
using CL.SystemStats.Abstractions;        // PlatformType
using CL.SystemStats.Models;
using CL.SystemStats.Services;            // PlatformDetector
using CodeLogic.Core.Results;
using Xunit;

namespace SystemStats.Tests;

// ── Smoke tests for CL.SystemStats ─────────────────────────────────────────────
// CL.SystemStats reads the LOCAL machine (CPU, memory, processes, uptime) with no
// external service, so the whole surface is testable on the host: we boot the real
// CodeLogic runtime and query the live machine.
//
// The CodeLogic runtime is a process-wide singleton, so EVERY test that needs the
// booted library lives in this one class behind a single shared fixture that boots
// once. The [Collection] attribute (with the matching CollectionDefinition below)
// serializes this class against any other CodeLogic-touching test assembly.
//
// CI/VM tolerance: perf counters may be limited and CPU% may read 0, so we assert
// ranges / non-null / success, never exact values.

// ── PlatformDetector (no boot required) ─────────────────────────────────────────

public sealed class PlatformDetectorTests
{
    [Fact]
    public void Detector_reports_exactly_one_platform_and_a_defined_enum()
    {
        var detector = new PlatformDetector();

        // Platform must be a defined enum value.
        Assert.True(Enum.IsDefined(detector.Platform), $"undefined platform: {detector.Platform}");

        // The three OS flags are mutually exclusive: at most one is true, and a flag
        // is true iff Platform matches it. On a recognized OS exactly one is set; on
        // an Unknown platform none are set.
        var trueCount =
            (detector.IsWindows ? 1 : 0) +
            (detector.IsLinux ? 1 : 0) +
            (detector.IsMacOS ? 1 : 0);

        if (detector.Platform == PlatformType.Unknown)
            Assert.Equal(0, trueCount);
        else
            Assert.Equal(1, trueCount);

        Assert.Equal(detector.Platform == PlatformType.Windows, detector.IsWindows);
        Assert.Equal(detector.Platform == PlatformType.Linux, detector.IsLinux);
        Assert.Equal(detector.Platform == PlatformType.MacOS, detector.IsMacOS);
    }
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────

public sealed class SystemStatsRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_systemstats_test_" + Guid.NewGuid().ToString("N"));

    public SystemStatsLibrary Library { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TempDir);

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        // Defaults are fine for reading the local machine; write a minimal enabled config.
        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.SystemStats");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.systemstats.json"), """
        {
          "enableCaching": true,
          "enableProcessMonitoring": true
        }
        """);

        await Libraries.LoadAsync<SystemStatsLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<SystemStatsLibrary>()
            ?? throw new InvalidOperationException("SystemStatsLibrary not available after start.");
    }

    public async Task DisposeAsync()
    {
        try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* ignore lingering files on Windows */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<SystemStatsRuntimeFixture> { }

// ── Runtime-dependent tests ─────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class SystemStatsTests
{
    private readonly SystemStatsRuntimeFixture _fx;
    private SystemStatsLibrary Lib => _fx.Library;

    public SystemStatsTests(SystemStatsRuntimeFixture fx) => _fx = fx;

    [Fact]
    public async Task GetCpuInfo_succeeds_with_positive_logical_cores()
    {
        var r = await Lib.GetCpuInfoAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.True(r.Value!.LogicalCoreCount > 0, $"LogicalCoreCount = {r.Value.LogicalCoreCount}");
    }

    [Fact]
    public async Task GetMemoryInfo_succeeds_with_positive_total()
    {
        var r = await Lib.GetMemoryInfoAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.True(r.Value!.TotalBytes > 0, $"TotalBytes = {r.Value.TotalBytes}");
    }

    [Fact]
    public async Task GetMemoryStats_succeeds_with_usage_in_range()
    {
        var r = await Lib.GetMemoryStatsAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.InRange(r.Value!.UsagePercent, 0.0, 100.0);
    }

    [Fact]
    public async Task GetSystemUptime_succeeds_and_is_positive()
    {
        var r = await Lib.GetSystemUptimeAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.True(r.Value > TimeSpan.Zero, $"uptime = {r.Value}");
    }

    [Fact]
    public async Task GetAllProcesses_succeeds_and_sees_at_least_one()
    {
        var r = await Lib.GetAllProcessesAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.True(r.Value!.Count >= 1, $"process count = {r.Value.Count}");
    }

    [Fact]
    public async Task GetTopProcessesByCpu_succeeds_and_respects_cap()
    {
        // Samples CPU over CpuSamplingIntervalMs (~100ms+); we only assert it returns OK.
        var r = await Lib.GetTopProcessesByCpuAsync(5);
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.True(r.Value!.Count <= 5, $"count = {r.Value.Count}");
    }

    [Fact]
    public async Task GetSystemSnapshot_succeeds_with_non_null_stats()
    {
        var r = await Lib.GetSystemSnapshotAsync();
        Assert.True(r.IsSuccess, r.Error?.ToString());
        Assert.NotNull(r.Value);
        Assert.NotNull(r.Value!.CpuStats);
        Assert.NotNull(r.Value.MemoryStats);
    }

    [Fact]
    public async Task HealthCheck_is_healthy_or_degraded()
    {
        var health = await Lib.HealthCheckAsync();
        // Degraded is acceptable on CI/VM where perf counters may be limited;
        // only Unhealthy is a failure.
        Assert.True(
            health.Status is HealthStatusLevel.Healthy or HealthStatusLevel.Degraded,
            $"unexpected status: {health.Status}");
    }
}

// ── Cache refresh coalescing (fake provider, no boot required) ──────────────────

public sealed class SystemStatsCacheCoalescingTests
{
    private sealed class GatedUptimeProvider : ISystemStatsProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<Result<TimeSpan>> Gate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Result<TimeSpan>> GetSystemUptimeAsync()
        {
            Interlocked.Increment(ref _calls);
            return Gate.Task;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<Result<CpuInfo>> GetCpuInfoAsync() => throw new NotSupportedException();
        public Task<Result<CpuStats>> GetCpuStatsAsync() => throw new NotSupportedException();
        public Task<Result<MemoryInfo>> GetMemoryInfoAsync() => throw new NotSupportedException();
        public Task<Result<MemoryStats>> GetMemoryStatsAsync() => throw new NotSupportedException();
        public Task<Result<ProcessStats>> GetProcessStatsAsync(int processId) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync() => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount) => throw new NotSupportedException();
        public Task<Result<SystemSnapshot>> GetSystemSnapshotAsync() => throw new NotSupportedException();
    }

    private static SystemStatsService Service(GatedUptimeProvider provider, bool caching = true) =>
        new(provider, new SystemStatsConfig { EnableCaching = caching, CacheDurationSeconds = 60 });

    [Fact]
    public async Task Concurrent_cache_misses_share_one_provider_call()
    {
        var provider = new GatedUptimeProvider();
        await using var service = Service(provider);

        var callers = Enumerable.Range(0, 32).Select(_ => Task.Run(service.GetSystemUptimeAsync)).ToArray();
        await Task.Delay(100);
        provider.Gate.SetResult(Result<TimeSpan>.Success(TimeSpan.FromHours(3)));
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, provider.Calls);
        Assert.All(results, r => Assert.Equal(TimeSpan.FromHours(3), r.Value));
        Assert.Equal(TimeSpan.FromHours(3), (await service.GetSystemUptimeAsync()).Value);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_is_shared_but_not_cached()
    {
        var provider = new GatedUptimeProvider();
        await using var service = Service(provider);

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(service.GetSystemUptimeAsync)).ToArray();
        await Task.Delay(100);
        provider.Gate.SetResult(Result<TimeSpan>.Failure(Error.Unavailable("systemstats.test", "sampling failed")));
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, provider.Calls);
        Assert.All(results, r => Assert.True(r.IsFailure));

        provider.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Gate.SetResult(Result<TimeSpan>.Success(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), (await service.GetSystemUptimeAsync()).Value);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task A_throwing_provider_releases_the_refresh_slot()
    {
        var provider = new GatedUptimeProvider();
        await using var service = Service(provider);
        provider.Gate.SetException(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(service.GetSystemUptimeAsync);

        provider.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Gate.SetResult(Result<TimeSpan>.Success(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(5), (await service.GetSystemUptimeAsync()).Value);
    }

    [Fact]
    public async Task Without_caching_every_call_reaches_the_provider()
    {
        var provider = new GatedUptimeProvider();
        provider.Gate.SetResult(Result<TimeSpan>.Success(TimeSpan.FromSeconds(1)));
        await using var service = Service(provider, caching: false);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.GetSystemUptimeAsync()));

        Assert.Equal(4, provider.Calls);
    }
}
