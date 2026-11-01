namespace CL.SystemStats;

/// <summary>Stable <see cref="CodeLogic.Core.Results.Error.Code"/> values of the errors CL.SystemStats returns.</summary>
public static class SystemStatsErrorCodes
{
    /// <summary>CPU information could not be read.</summary>
    public const string CpuInfo = "systemstats.cpu_info";

    /// <summary>CPU usage could not be read.</summary>
    public const string CpuStats = "systemstats.cpu_stats";

    /// <summary>Memory information could not be read.</summary>
    public const string MemoryInfo = "systemstats.mem_info";

    /// <summary>Memory usage could not be read.</summary>
    public const string MemoryStats = "systemstats.mem_stats";

    /// <summary>System uptime could not be read.</summary>
    public const string Uptime = "systemstats.uptime";

    /// <summary>A process was not found or is not accessible.</summary>
    public const string Process = "systemstats.process";

    /// <summary>Processes could not be enumerated.</summary>
    public const string Processes = "systemstats.processes";

    /// <summary>A process call while <c>EnableProcessMonitoring</c> is <see langword="false"/> (an unavailable error).</summary>
    public const string ProcessMonitoringDisabled = "systemstats.process_monitoring_disabled";

    /// <summary>A temperature call while <c>EnableTemperatureMonitoring</c> is <see langword="false"/> (an unavailable error).</summary>
    public const string TemperatureMonitoringDisabled = "systemstats.temperature_monitoring_disabled";

    /// <summary>The platform offers no temperature sensors the library can read (an unavailable error).</summary>
    public const string TemperatureUnavailable = "systemstats.temperature_unavailable";
}
