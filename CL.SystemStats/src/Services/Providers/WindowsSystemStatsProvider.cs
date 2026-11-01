using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services.Providers;

/// <summary>
/// Windows implementation of <see cref="ISystemStatsProvider"/>.
/// Uses <see cref="System.Diagnostics.Process"/>, PerformanceCounters (with graceful fallback),
/// and <c>GlobalMemoryStatusEx</c> P/Invoke for accurate memory figures.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemStatsProvider : ISystemStatsProvider
{
    // ── P/Invoke ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, byte[]? buffer, ref uint returnedLength);

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly SystemStatsConfig _config;
    private readonly ILogger? _logger;
    private PerformanceCounter? _overallCpuCounter;
    private PerformanceCounter?[] _coreCounters = [];
    private bool _countersAvailable;

    /// <summary>
    /// Initializes the Windows stats provider.
    /// </summary>
    public WindowsSystemStatsProvider(SystemStatsConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        try
        {
            _overallCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            // First read is always 0 — prime the counter
            _overallCpuCounter.NextValue();
            // Per-core counters live as long as the provider, so each reading covers the whole sampling interval.
            _coreCounters = new PerformanceCounter?[Environment.ProcessorCount];
            for (var i = 0; i < _coreCounters.Length; i++)
            {
                try
                {
                    var counter = new PerformanceCounter("Processor", "% Processor Time", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    counter.NextValue();
                    _coreCounters[i] = counter;
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    _coreCounters[i] = null;   // that core reads 0
                }
            }
            _countersAvailable = true;
            _logger?.Info("WindowsSystemStatsProvider initialized with PerformanceCounters");
        }
        catch (Exception ex)
        {
            _countersAvailable = false;
            _logger?.Warning($"PerformanceCounters unavailable (containers/VMs may not support them): {ex.Message}. CPU stats will return 0.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _overallCpuCounter?.Dispose();
        _overallCpuCounter = null;
        foreach (var counter in _coreCounters) counter?.Dispose();
        _coreCounters = [];
        return ValueTask.CompletedTask;
    }

    // ── CPU Info ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Model, vendor and nominal clock come from the registry (<c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>:
    /// <c>ProcessorNameString</c>, <c>VendorIdentifier</c>, <c>~MHz</c>); physical cores and logical processors from
    /// <c>GetLogicalProcessorInformationEx</c>, falling back to <see cref="Environment.ProcessorCount"/>.
    /// </remarks>
    public Task<Result<CpuInfo>> GetCpuInfoAsync()
    {
        try
        {
            string modelName = "Unknown";
            string vendor = "Unknown";
            double mhz = 0;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key is not null)
                {
                    if (key.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                        modelName = name.Trim();
                    if (key.GetValue("VendorIdentifier") is string id && !string.IsNullOrWhiteSpace(id))
                        vendor = id.Trim();
                    if (key.GetValue("~MHz") is int clock && clock > 0)
                        mhz = clock;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _logger?.Debug($"CPU registry key not readable: {ex.Message}");
            }

            var (physical, logical) = ReadCoreCounts();
            if (logical <= 0) logical = Environment.ProcessorCount;
            if (physical <= 0) physical = logical;

            return Task.FromResult(Result<CpuInfo>.Success(new CpuInfo(
                modelName, physical, logical, vendor, mhz, RuntimeInformation.ProcessArchitecture.ToString())));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetCpuInfoAsync failed: {ex.Message}");
            return Task.FromResult(Result<CpuInfo>.Failure(
                Error.Internal(SystemStatsErrorCodes.CpuInfo, "Failed to read CPU info", ex.Message)));
        }
    }

    /// <summary>Physical cores and logical processors from <c>GetLogicalProcessorInformationEx</c>; (0, 0) when it fails.</summary>
    private (int Physical, int Logical) ReadCoreCounts()
    {
        try
        {
            uint length = 0;
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length)
                && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                return (0, 0);
            if (length == 0 || length > 16 * 1024 * 1024) return (0, 0);

            var buffer = new byte[length];
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                return (0, 0);

            return CpuInfoParsers.CountWindowsCores(buffer.AsSpan(0, (int)Math.Min(length, (uint)buffer.Length)), IntPtr.Size);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _logger?.Debug($"GetLogicalProcessorInformationEx unavailable: {ex.Message}");
            return (0, 0);
        }
    }

    // ── CPU Stats ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<CpuStats>> GetCpuStatsAsync()
    {
        try
        {
            double overall = 0;
            var perCore = new List<double>();

            if (_countersAvailable && _overallCpuCounter is not null)
            {
                // Allow the counter to stabilise
                await Task.Delay(_config.CpuSamplingIntervalMs);
                overall = Math.Round(_overallCpuCounter.NextValue(), 2);

                // Per-core counters were primed at initialization, so each reading covers the same interval.
                foreach (var counter in _coreCounters)
                {
                    try
                    {
                        perCore.Add(counter is null ? 0.0 : Math.Round(counter.NextValue(), 2));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        perCore.Add(0.0);
                    }
                }
            }
            else
            {
                // Fallback: no PerformanceCounters available
                overall = 0;
                perCore = Enumerable.Repeat(0.0, Environment.ProcessorCount).ToList();
            }

            return Result<CpuStats>.Success(new CpuStats(overall, perCore, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetCpuStatsAsync failed: {ex.Message}");
            return Result<CpuStats>.Failure(
                Error.Internal(SystemStatsErrorCodes.CpuStats, "Failed to read CPU stats", ex.Message));
        }
    }

    // ── Memory Info ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<MemoryInfo>> GetMemoryInfoAsync()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref memStatus))
                return Task.FromResult(Result<MemoryInfo>.Success(new MemoryInfo((long)memStatus.ullTotalPhys)));

            // Fallback to GC info
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return Task.FromResult(Result<MemoryInfo>.Success(new MemoryInfo(total)));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetMemoryInfoAsync failed: {ex.Message}");
            return Task.FromResult(Result<MemoryInfo>.Failure(
                Error.Internal(SystemStatsErrorCodes.MemoryInfo, "Failed to read memory info", ex.Message)));
        }
    }

    /// <inheritdoc/>
    public Task<Result<MemoryStats>> GetMemoryStatsAsync()
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref memStatus))
            {
                // Fallback
                var gcInfo = GC.GetGCMemoryInfo();
                long totalFb = gcInfo.TotalAvailableMemoryBytes;
                long usedFb = GC.GetTotalMemory(false);
                long availFb = totalFb - usedFb;
                double pctFb = totalFb > 0 ? Math.Round((double)usedFb / totalFb * 100.0, 2) : 0;
                return Task.FromResult(Result<MemoryStats>.Success(
                    new MemoryStats(totalFb, availFb, usedFb, 0, 0, pctFb, DateTime.UtcNow)));
            }

            long total = (long)memStatus.ullTotalPhys;
            long available = (long)memStatus.ullAvailPhys;
            long used = total - available;
            double usagePct = total > 0 ? Math.Round((double)memStatus.dwMemoryLoad, 2) : 0;

            return Task.FromResult(Result<MemoryStats>.Success(
                new MemoryStats(total, available, used, 0, 0, usagePct, DateTime.UtcNow)));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetMemoryStatsAsync failed: {ex.Message}");
            return Task.FromResult(Result<MemoryStats>.Failure(
                Error.Internal(SystemStatsErrorCodes.MemoryStats, "Failed to read memory stats", ex.Message)));
        }
    }

    // ── Uptime ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<TimeSpan>> GetSystemUptimeAsync()
    {
        try
        {
            // Environment.TickCount64 returns milliseconds since system boot on Windows
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return Task.FromResult(Result<TimeSpan>.Success(uptime));
        }
        catch (Exception ex)
        {
            _logger?.Error($"GetSystemUptimeAsync failed: {ex.Message}");
            return Task.FromResult(Result<TimeSpan>.Failure(
                Error.Internal(SystemStatsErrorCodes.Uptime, "Failed to read system uptime", ex.Message)));
        }
    }

    // ── Process Stats ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<ProcessStats>> GetProcessStatsAsync(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return Task.FromResult(Result<ProcessStats>.Success(MapProcess(proc)));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to get process {processId}: {ex.Message}");
            return Task.FromResult(Result<ProcessStats>.Failure(
                Error.NotFound(SystemStatsErrorCodes.Process, $"Process {processId} not found or inaccessible", ex.Message)));
        }
    }

    /// <inheritdoc/>
    public Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync()
    {
        try
        {
            var processes = Process.GetProcesses();
            var stats = new List<ProcessStats>(processes.Length);
            foreach (var proc in processes)
            {
                try { stats.Add(MapProcess(proc)); }
                catch { /* skip inaccessible processes */ }
                finally { proc.Dispose(); }
            }

            return Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Success(stats));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to enumerate processes: {ex.Message}");
            return Task.FromResult(Result<IReadOnlyList<ProcessStats>>.Failure(
                Error.Internal(SystemStatsErrorCodes.Processes, "Failed to enumerate processes", ex.Message)));
        }
    }

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount)
    {
        var result = await GetAllProcessesAsync();
        if (result.IsFailure) return result;
        // Per-process CPU% requires sampling processor time over an interval — a single
        // snapshot can't measure it. Delegate to the shared sampler.
        var top = await ProcessCpuSampler.TopByCpuAsync(
            result.Value!, _config.CpuSamplingIntervalMs, topCount);
        return Result<IReadOnlyList<ProcessStats>>.Success(top);
    }

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount)
    {
        var result = await GetAllProcessesAsync();
        if (result.IsFailure) return result;
        var top = result.Value!.OrderByDescending(p => p.WorkingSetBytes).Take(topCount).ToList();
        return Result<IReadOnlyList<ProcessStats>>.Success(top);
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<SystemSnapshot>> GetSystemSnapshotAsync()
    {
        var cpuResult = await GetCpuStatsAsync();
        if (cpuResult.IsFailure)
            return Result<SystemSnapshot>.Failure(cpuResult.Error!);

        var memResult = await GetMemoryStatsAsync();
        if (memResult.IsFailure)
            return Result<SystemSnapshot>.Failure(memResult.Error!);

        var uptimeResult = await GetSystemUptimeAsync();
        if (uptimeResult.IsFailure)
            return Result<SystemSnapshot>.Failure(uptimeResult.Error!);

        var topCpu = await GetTopProcessesByCpuAsync(_config.MaxTopProcesses);
        var topMem = await GetTopProcessesByMemoryAsync(_config.MaxTopProcesses);

        var snapshot = new SystemSnapshot(
            cpuResult.Value!,
            memResult.Value!,
            uptimeResult.Value!,
            topCpu.IsSuccess ? topCpu.Value! : [],
            topMem.IsSuccess ? topMem.Value! : [],
            DateTime.UtcNow
        );

        return Result<SystemSnapshot>.Success(snapshot);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcessStats MapProcess(Process proc)
    {
        long workingSet = 0;
        int threads = 0;
        int handles = 0;
        DateTime? startTime = null;
        TimeSpan? cpuTime = null;
        string priority = "Unknown";

        try { workingSet = proc.WorkingSet64; } catch { }
        try { threads = proc.Threads.Count; } catch { }
        try { handles = proc.HandleCount; } catch { }
        try { startTime = proc.StartTime.ToUniversalTime(); } catch { }
        try { cpuTime = proc.TotalProcessorTime; } catch { }
        try { priority = proc.PriorityClass.ToString(); } catch { }

        return new ProcessStats(
            proc.Id,
            proc.ProcessName,
            workingSet,
            0.0,
            threads,
            handles,
            startTime,
            cpuTime,
            priority
        );
    }
}
