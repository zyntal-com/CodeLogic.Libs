using System.Diagnostics;
using System.Runtime.InteropServices;
using CL.SystemStats.Abstractions;
using CL.SystemStats.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Services.Providers;

/// <summary>
/// Linux implementation of <see cref="ISystemStatsProvider"/>.
/// Reads system statistics from the <c>/proc</c> virtual filesystem.
/// </summary>
public sealed class LinuxSystemStatsProvider : ISystemStatsProvider
{
    private readonly SystemStatsConfig _config;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes the Linux stats provider.
    /// </summary>
    public LinuxSystemStatsProvider(SystemStatsConfig config, ILogger? logger = null)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        _logger?.Info("LinuxSystemStatsProvider initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── CPU Info ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<CpuInfo>> GetCpuInfoAsync()
    {
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/cpuinfo");
            double? maxMHz = null;
            try
            {
                const string maxFreq = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";
                if (File.Exists(maxFreq))
                    maxMHz = CpuInfoParsers.ParseMaxFrequencyKHz(await File.ReadAllTextAsync(maxFreq));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no cpufreq: use cpu MHz */ }

            return Result<CpuInfo>.Success(
                CpuInfoParsers.ParseProcCpuInfo(lines, RuntimeInformation.ProcessArchitecture.ToString(), maxMHz));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read /proc/cpuinfo: {ex.Message}");
            return Result<CpuInfo>.Failure(Error.Internal(SystemStatsErrorCodes.CpuInfo, "Failed to read CPU info", ex.Message));
        }
    }

    // ── Temperatures ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<Result<IReadOnlyList<TemperatureReading>>> GetTemperaturesAsync()
    {
        try
        {
            var readings = CpuInfoParsers.ReadSysfsTemperatures("/sys");
            return Task.FromResult(readings.Count > 0
                ? Result<IReadOnlyList<TemperatureReading>>.Success(readings)
                : Result<IReadOnlyList<TemperatureReading>>.Failure(Error.Unavailable(
                    SystemStatsErrorCodes.TemperatureUnavailable, "No temperature sensors found under /sys/class/thermal or /sys/class/hwmon")));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read temperatures: {ex.Message}");
            return Task.FromResult(Result<IReadOnlyList<TemperatureReading>>.Failure(Error.Unavailable(
                SystemStatsErrorCodes.TemperatureUnavailable, "Failed to read temperature sensors", ex.Message)));
        }
    }

    // ── CPU Stats ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<CpuStats>> GetCpuStatsAsync()
    {
        try
        {
            // Sample twice with a delay to compute a meaningful delta
            var (overall1, cores1) = await ReadProcStatAsync();
            await Task.Delay(_config.CpuSamplingIntervalMs);
            var (overall2, cores2) = await ReadProcStatAsync();

            double overallUsage = ComputeCpuUsage(overall1, overall2);

            var perCore = new List<double>();
            int coreCount = Math.Min(cores1.Count, cores2.Count);
            for (int i = 0; i < coreCount; i++)
                perCore.Add(ComputeCpuUsage(cores1[i], cores2[i]));

            return Result<CpuStats>.Success(new CpuStats(overallUsage, perCore, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read /proc/stat: {ex.Message}");
            return Result<CpuStats>.Failure(Error.Internal(SystemStatsErrorCodes.CpuStats, "Failed to read CPU stats", ex.Message));
        }
    }

    private static async Task<(long[] overall, List<long[]> cores)> ReadProcStatAsync()
    {
        var lines = await File.ReadAllLinesAsync("/proc/stat");
        long[] overall = [];
        var cores = new List<long[]>();

        foreach (var line in lines)
        {
            if (line.StartsWith("cpu ", StringComparison.Ordinal))
                overall = ParseStatLine(line);
            else if (line.StartsWith("cpu", StringComparison.Ordinal) && line.Length > 3 && char.IsDigit(line[3]))
                cores.Add(ParseStatLine(line));
        }

        return (overall, cores);
    }

    private static long[] ParseStatLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = new long[parts.Length - 1];
        for (int i = 1; i < parts.Length; i++)
            long.TryParse(parts[i], out values[i - 1]);
        return values;
    }

    private static double ComputeCpuUsage(long[] sample1, long[] sample2)
    {
        if (sample1.Length < 4 || sample2.Length < 4) return 0.0;

        // Fields: user nice system idle iowait irq softirq steal
        long idle1 = sample1.Length > 4 ? sample1[3] + sample1[4] : sample1[3];
        long idle2 = sample2.Length > 4 ? sample2[3] + sample2[4] : sample2[3];

        long total1 = sample1.Sum();
        long total2 = sample2.Sum();

        long totalDelta = total2 - total1;
        long idleDelta = idle2 - idle1;

        if (totalDelta <= 0) return 0.0;
        return Math.Round((1.0 - (double)idleDelta / totalDelta) * 100.0, 2);
    }

    // ── Memory Info ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<MemoryInfo>> GetMemoryInfoAsync()
    {
        try
        {
            var memInfo = await ReadMemInfoAsync();
            if (!memInfo.TryGetValue("MemTotal", out long total))
                return Result<MemoryInfo>.Failure(Error.Internal(SystemStatsErrorCodes.MemoryInfo, "MemTotal not found in /proc/meminfo"));

            return Result<MemoryInfo>.Success(new MemoryInfo(total * 1024));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read /proc/meminfo: {ex.Message}");
            return Result<MemoryInfo>.Failure(Error.Internal(SystemStatsErrorCodes.MemoryInfo, "Failed to read memory info", ex.Message));
        }
    }

    /// <inheritdoc/>
    public async Task<Result<MemoryStats>> GetMemoryStatsAsync()
    {
        try
        {
            var memInfo = await ReadMemInfoAsync();

            long GetKb(string key) => memInfo.TryGetValue(key, out long v) ? v : 0;

            long totalKb = GetKb("MemTotal");
            long availableKb = GetKb("MemAvailable");
            long cachedKb = GetKb("Cached");
            long buffersKb = GetKb("Buffers");

            long total = totalKb * 1024;
            long available = availableKb * 1024;
            long used = total - available;
            long cached = cachedKb * 1024;
            long buffers = buffersKb * 1024;
            double usagePct = total > 0 ? Math.Round((double)used / total * 100.0, 2) : 0;

            return Result<MemoryStats>.Success(new MemoryStats(total, available, used, cached, buffers, usagePct, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read /proc/meminfo: {ex.Message}");
            return Result<MemoryStats>.Failure(Error.Internal(SystemStatsErrorCodes.MemoryStats, "Failed to read memory stats", ex.Message));
        }
    }

    private static async Task<Dictionary<string, long>> ReadMemInfoAsync()
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync("/proc/meminfo");
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length < 2) continue;
            var key = parts[0].Trim();
            // Value format: "  16384 kB"
            var valStr = parts[1].Trim().Split(' ')[0];
            if (long.TryParse(valStr, out long val))
                result[key] = val;
        }
        return result;
    }

    // ── Uptime ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<Result<TimeSpan>> GetSystemUptimeAsync()
    {
        try
        {
            var content = await File.ReadAllTextAsync("/proc/uptime");
            var firstField = content.Trim().Split(' ')[0];
            if (!double.TryParse(firstField, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                return Result<TimeSpan>.Failure(Error.Internal(SystemStatsErrorCodes.Uptime, "Failed to parse /proc/uptime"));

            return Result<TimeSpan>.Success(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read /proc/uptime: {ex.Message}");
            return Result<TimeSpan>.Failure(Error.Internal(SystemStatsErrorCodes.Uptime, "Failed to read system uptime", ex.Message));
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
            0.0,   // point-in-time CPU % not available without sampling; callers can aggregate
            threads,
            handles,
            startTime,
            cpuTime,
            priority
        );
    }
}
