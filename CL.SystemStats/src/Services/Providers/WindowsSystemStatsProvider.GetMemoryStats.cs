using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services.Providers;

public sealed partial class WindowsSystemStatsProvider
{

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
        var result = await GetAllProcessesAsync().ConfigureAwait(false);
        if (result.IsFailure) return result;
        // Per-process CPU% requires sampling processor time over an interval — a single
        // snapshot can't measure it. Delegate to the shared sampler.
        var top = await ProcessCpuSampler.TopByCpuAsync(
            result.Value!, _config.CpuSamplingIntervalMs, topCount).ConfigureAwait(false);
        return Result<IReadOnlyList<ProcessStats>>.Success(top);
    }

    /// <inheritdoc/>
    public async Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount)
    {
        var result = await GetAllProcessesAsync().ConfigureAwait(false);
        if (result.IsFailure) return result;
        var top = result.Value!.OrderByDescending(p => p.WorkingSetBytes).Take(topCount).ToList();
        return Result<IReadOnlyList<ProcessStats>>.Success(top);
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<SystemSnapshot>> GetSystemSnapshotAsync()
    {
        var cpuResult = await GetCpuStatsAsync().ConfigureAwait(false);
        if (cpuResult.IsFailure)
            return Result<SystemSnapshot>.Failure(cpuResult.Error!);

        var memResult = await GetMemoryStatsAsync().ConfigureAwait(false);
        if (memResult.IsFailure)
            return Result<SystemSnapshot>.Failure(memResult.Error!);

        var uptimeResult = await GetSystemUptimeAsync().ConfigureAwait(false);
        if (uptimeResult.IsFailure)
            return Result<SystemSnapshot>.Failure(uptimeResult.Error!);

        var topCpu = await GetTopProcessesByCpuAsync(_config.MaxTopProcesses).ConfigureAwait(false);
        var topMem = await GetTopProcessesByMemoryAsync(_config.MaxTopProcesses).ConfigureAwait(false);

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
