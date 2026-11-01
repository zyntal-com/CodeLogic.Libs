using CL.SystemStats.Models;
using CodeLogic.Core.Results;

namespace CL.SystemStats.Abstractions;

/// <summary>
/// Platform-specific system statistics provider.
/// Implementations exist for Windows and Linux.
/// </summary>
public interface ISystemStatsProvider
{
    /// <summary>Gets static CPU information (model name, core count, vendor, etc.).</summary>
    Task<Result<CpuInfo>> GetCpuInfoAsync();

    /// <summary>Gets a live CPU usage snapshot (overall and per-core percentages).</summary>
    Task<Result<CpuStats>> GetCpuStatsAsync();

    /// <summary>Gets static memory information (total physical RAM).</summary>
    Task<Result<MemoryInfo>> GetMemoryInfoAsync();

    /// <summary>Gets a live memory usage snapshot (used, available, usage percent).</summary>
    Task<Result<MemoryStats>> GetMemoryStatsAsync();

    /// <summary>Gets the system uptime since last boot.</summary>
    Task<Result<TimeSpan>> GetSystemUptimeAsync();

    /// <summary>Gets statistics for the process identified by <paramref name="processId"/>.</summary>
    Task<Result<ProcessStats>> GetProcessStatsAsync(int processId);

    /// <summary>Gets statistics for all running processes.</summary>
    Task<Result<IReadOnlyList<ProcessStats>>> GetAllProcessesAsync();

    /// <summary>Gets the top <paramref name="topCount"/> processes sorted by CPU usage descending.</summary>
    Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByCpuAsync(int topCount);

    /// <summary>Gets the top <paramref name="topCount"/> processes sorted by memory usage descending.</summary>
    Task<Result<IReadOnlyList<ProcessStats>>> GetTopProcessesByMemoryAsync(int topCount);

    /// <summary>Gets a full system snapshot composed from all individual metrics.</summary>
    Task<Result<SystemSnapshot>> GetSystemSnapshotAsync();

    /// <summary>
    /// Gets the readings of the temperature sensors the platform exposes. The default implementation reports that
    /// temperatures are unavailable (<see cref="SystemStatsErrorCodes.TemperatureUnavailable"/>); the Linux provider
    /// reads sysfs.
    /// </summary>
    Task<Result<IReadOnlyList<TemperatureReading>>> GetTemperaturesAsync() =>
        Task.FromResult(Result<IReadOnlyList<TemperatureReading>>.Failure(Error.Unavailable(
            SystemStatsErrorCodes.TemperatureUnavailable, "Temperature sensors are not available on this platform")));

    /// <summary>Initializes the provider (e.g., warm up PerformanceCounters).</summary>
    Task InitializeAsync();

    /// <summary>Releases provider resources asynchronously.</summary>
    ValueTask DisposeAsync();
}
