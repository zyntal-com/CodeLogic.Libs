# CL.SystemStats

> Cross-platform CPU, memory, uptime, and process statistics — one API over Windows performance counters and Linux `/proc`.

`CL.SystemStats` gives a CodeLogic application a single surface for reading live system metrics. It samples CPU usage, memory pressure, uptime, and per-process resource use through platform-specific providers, returns every reading as a `Result<T>`, caches common reads for a short window, and raises events when CPU or memory usage crosses a configured threshold. The library exposes a `SystemStatsLibrary` that forwards every stats method directly and also surfaces the underlying service through its `Stats` property.

| | |
|---|---|
| **Package** | [`CodeLogic.SystemStats`](https://www.nuget.org/packages/CodeLogic.SystemStats) |
| **Library class** | `CL.SystemStats.SystemStatsLibrary` |
| **Config file** | `config.systemstats.json` (section `systemstats`) |
| **Dependencies** | System.Diagnostics.PerformanceCounter 11.x |

## Install & load

```bash
dotnet add package CodeLogic.SystemStats
```

```csharp
using CL.SystemStats;

await Libraries.LoadAsync<SystemStatsLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var sys = Libraries.Get<SystemStatsLibrary>();
```

The library forwards all stats methods, so `sys.GetCpuStatsAsync()` and `sys.Stats.GetCpuStatsAsync()` are equivalent. The `Stats` property (a `SystemStatsService`) additionally exposes `ClearCache()`, `GetPlatformInfo()`, and `IsInitialized`.

## How readings work across platforms

There is one API but two providers, selected at runtime. Unknown platforms fall back to the Linux provider.

| Metric | Windows | Linux |
|--------|---------|-------|
| CPU info | Registry (model, vendor, nominal MHz) + `GetLogicalProcessorInformationEx` (cores) | `/proc/cpuinfo` + `cpufreq/cpuinfo_max_freq` |
| CPU usage | `PerformanceCounter` | `/proc/stat` (dual-sample delta) |
| Memory | P/Invoke `GlobalMemoryStatusEx` | `/proc/meminfo` |
| Uptime | `Environment.TickCount64` | `/proc/uptime` |
| Temperatures | not available | `/sys/class/thermal`, `/sys/class/hwmon` |

Because the providers read different sources, a few fields differ by platform; these are called out alongside the models below. Every data method returns a `Task<Result<T>>` — check `IsSuccess` / `IsFailure`, read `.Value` on success, and `.Error?.Message` on failure.

```csharp
Result<CpuStats> r = await sys.GetCpuStatsAsync();
if (r.IsFailure)
    logger.Warn(r.Error?.Message);
else
    Use(r.Value!);
```

## CPU

`GetCpuInfoAsync()` returns static hardware facts; `GetCpuStatsAsync()` returns a live usage reading sampled per the configuration.

```csharp
Result<CpuInfo>  info  = await sys.GetCpuInfoAsync();
Result<CpuStats> stats = await sys.GetCpuStatsAsync();

if (info.IsSuccess && stats.IsSuccess)
{
    Console.WriteLine($"{info.Value!.ModelName} ({info.Value.LogicalCoreCount} threads)");
    Console.WriteLine($"Overall: {stats.Value!.OverallUsagePercent:F1}%");
    for (int core = 0; core < stats.Value.PerCoreUsagePercent.Count; core++)
        Console.WriteLine($"  core {core}: {stats.Value.PerCoreUsagePercent[core]:F1}%");
}
```

Each `GetCpuStatsAsync()` reading is the average of the last `CpuSamplesForAverage` provider samples (default 3, the newest included), overall and per core, so one spike does not trip an alert; with caching on, a new sample is taken once per `CacheDurationSeconds`. Set it to `1` for raw samples. `GetCpuStatsAsync()` publishes `HighCpuUsageEvent` when the smoothed `OverallUsagePercent` reaches `HighCpuThresholdPercent`.

`CpuInfo.PhysicalCoreCount` counts physical cores: on Linux the distinct `(physical id, core id)` pairs in `/proc/cpuinfo` (every processor when the kernel reports no topology, as on many ARM boards); on Windows the processor-core records of `GetLogicalProcessorInformationEx`. `LogicalCoreCount` counts hardware threads. `MaxSpeedMHz` is the registry's nominal clock on Windows and `cpuinfo_max_freq` on Linux (the highest current `cpu MHz` when cpufreq is absent).

```csharp
public sealed record CpuInfo(
    string ModelName,
    int    PhysicalCoreCount,
    int    LogicalCoreCount,
    string Vendor,
    double MaxSpeedMHz,
    string Architecture);

public sealed record CpuStats(
    double                OverallUsagePercent,
    IReadOnlyList<double> PerCoreUsagePercent,
    DateTime              SampledAt);
```

## Memory

`GetMemoryInfoAsync()` returns total installed RAM; `GetMemoryStatsAsync()` returns a live breakdown. Both records carry MiB/GiB helper properties so you don't have to divide by 1024 yourself.

```csharp
Result<MemoryInfo>  info = await sys.GetMemoryInfoAsync();
Result<MemoryStats> mem  = await sys.GetMemoryStatsAsync();

if (info.IsSuccess) Console.WriteLine($"Installed: {info.Value!.TotalGiB:F1} GiB");
if (mem.IsSuccess)  Console.WriteLine($"Used: {mem.Value!.UsedMiB} MiB ({mem.Value.UsagePercent:F1}%)");
```

`GetMemoryStatsAsync()` publishes `HighMemoryUsageEvent` when `UsagePercent` reaches `HighMemoryThresholdPercent`.

```csharp
public sealed record MemoryInfo(long TotalBytes)
{
    public long   TotalMiB { get; }   // helper
    public double TotalGiB { get; }   // helper
}

public sealed record MemoryStats(
    long     TotalBytes,
    long     AvailableBytes,
    long     UsedBytes,
    long     CachedBytes,
    long     BuffersBytes,
    double   UsagePercent,
    DateTime SampledAt)
{
    public long   UsedMiB      { get; }   // helper
    public double TotalGiB     { get; }   // helper
    public double AvailableGiB { get; }   // helper
}
```

> `CachedBytes` and `BuffersBytes` are `0` on Windows and populated on Linux (from `/proc/meminfo`).

## Uptime

```csharp
Result<TimeSpan> up = await sys.GetSystemUptimeAsync();
if (up.IsSuccess)
    Console.WriteLine($"Up {up.Value.Days}d {up.Value.Hours}h {up.Value.Minutes}m");
```

## Processes

Enumerate every process, look one up by PID, or rank the heaviest by CPU or memory. With `EnableProcessMonitoring` set to `false`, all four calls fail with an unavailable error (`systemstats.process_monitoring_disabled`) without touching any process, and snapshots carry empty process lists. Snapshots rank `MaxTopProcesses` processes; the ranking methods take an explicit count.

```csharp
Result<IReadOnlyList<ProcessStats>> all     = await sys.GetAllProcessesAsync();
Result<IReadOnlyList<ProcessStats>> topCpu  = await sys.GetTopProcessesByCpuAsync(5);
Result<IReadOnlyList<ProcessStats>> topMem  = await sys.GetTopProcessesByMemoryAsync(5);
Result<ProcessStats>                one     = await sys.GetProcessStatsAsync(processId: 1234);

if (one.IsFailure)
    Console.WriteLine(one.Error?.Message);   // NotFound if the PID no longer exists

if (topMem.IsSuccess)
    foreach (var p in topMem.Value!)
        Console.WriteLine($"{p.Name} (pid {p.ProcessId}) — {p.WorkingSetMiB} MiB");
```

```csharp
public sealed record ProcessStats(
    int       ProcessId,
    string    Name,
    long      WorkingSetBytes,
    double    CpuUsagePercent,
    int       ThreadCount,
    int       HandleCount,
    DateTime? StartTime,
    TimeSpan? TotalCpuTime,
    string    PriorityClass)
{
    public long WorkingSetMiB { get; }   // helper
}
```

> **Per-process CPU is sampled, so these calls take time to return.** `GetTopProcessesByCpuAsync` measures each process's `CpuUsagePercent` by sampling its processor time twice over `CpuSamplingIntervalMs` (default 100 ms), so the call blocks for roughly that interval; `GetSystemSnapshotAsync` does the same when it ranks top processes. Use `TotalCpuTime` for cumulative CPU consumption, and rank by `WorkingSetBytes` for memory. On Linux, `HandleCount` is the open file-descriptor count; `StartTime` and `TotalCpuTime` are `null` when unavailable.

`GetProcessStatsAsync`, `GetTopProcessesByCpuAsync`, and `GetTopProcessesByMemoryAsync` bypass the cache so they always return fresh data; `GetAllProcessesAsync` is cached.

## Temperatures

```csharp
Result<IReadOnlyList<TemperatureReading>> temps = await sys.GetTemperaturesAsync();
if (temps.IsSuccess)
    foreach (var t in temps.Value!)
        Console.WriteLine($"{t.Sensor}: {t.Celsius:F1} °C");
```

On Linux the readings come from `/sys/class/thermal/thermal_zone*` (sensor `thermal_zone0:x86_pkg_temp`) and `/sys/class/hwmon/hwmon*/temp*_input` (sensor `coretemp:Package id 0`). Windows offers no sensors the library can read without WMI and administrator rights, so the call fails with `systemstats.temperature_unavailable` there, and on Linux machines without sensors. With `EnableTemperatureMonitoring` set to `false` it fails with `systemstats.temperature_monitoring_disabled` and reads nothing. Readings are cached like the other reads (key `temperatures`).

```csharp
public record TemperatureReading(string Sensor, double Celsius);
```

## System snapshot

`GetSystemSnapshotAsync()` collects everything in one call — useful for a dashboard tick or a periodic health log.

```csharp
Result<SystemSnapshot> snap = await sys.GetSystemSnapshotAsync();
if (snap.IsSuccess)
{
    var s = snap.Value!;
    Console.WriteLine($"CPU {s.CpuStats.OverallUsagePercent:F0}%  " +
                      $"RAM {s.MemoryStats.UsagePercent:F0}%  " +
                      $"up {s.Uptime.TotalHours:F0}h");
}
```

```csharp
public sealed record SystemSnapshot(
    CpuStats                    CpuStats,
    MemoryStats                 MemoryStats,
    TimeSpan                    Uptime,
    IReadOnlyList<ProcessStats> TopProcessesByCpu,
    IReadOnlyList<ProcessStats> TopProcessesByMemory,
    DateTime                    TakenAt);
```

A snapshot takes fresh (uncached) CPU, memory and uptime readings; its CPU reading is smoothed like `GetCpuStatsAsync()`. Taking a snapshot publishes `SystemSnapshotTakenEvent`, and because it reads CPU and memory stats it also runs the CPU and memory threshold checks (so it can publish `HighCpuUsageEvent` / `HighMemoryUsageEvent`).

## Caching

When `EnableCaching` is on, these reads are cached for `CacheDurationSeconds`: `cpu_info`, `cpu_stats`, `mem_info`, `mem_stats`, `uptime`, `temperatures`, and `all_processes`. Per-process lookups and the top-N rankings always bypass the cache. Clear everything manually through the service:

```csharp
sys.Stats.ClearCache();
```

## Events

All three implement `IEvent` (namespace `CL.SystemStats.Events`) and are published to the CodeLogic event bus when one is available.

| Event | Carries | Published when |
|-------|---------|----------------|
| `SystemSnapshotTakenEvent` | `CpuUsagePercent`, `MemoryUsagePercent`, `Uptime`, `TakenAt` | After a successful `GetSystemSnapshotAsync()`. |
| `HighCpuUsageEvent` | `UsagePercent`, `ThresholdPercent`, `DetectedAt` | Overall CPU usage reaches `HighCpuThresholdPercent` (from `GetCpuStatsAsync` / `GetSystemSnapshotAsync`). |
| `HighMemoryUsageEvent` | `UsagePercent`, `ThresholdPercent`, `DetectedAt` | Memory usage reaches `HighMemoryThresholdPercent` (from `GetMemoryStatsAsync` / `GetSystemSnapshotAsync`). |

## Configuration

The library writes `config.systemstats.json` (section `systemstats`) with defaults on first run.

```json
{
  "enableCaching": true,
  "cacheDurationSeconds": 5,
  "cpuSamplingIntervalMs": 100,
  "cpuSamplesForAverage": 3,
  "enableTemperatureMonitoring": true,
  "enableProcessMonitoring": true,
  "maxTopProcesses": 10,
  "highCpuThresholdPercent": 90.0,
  "highMemoryThresholdPercent": 90.0
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `EnableCaching` | `bool` | `true` | Cache common reads for `CacheDurationSeconds`. When off, every call samples live. |
| `CacheDurationSeconds` | `int` | `5` | Cache lifetime in seconds (1–600). |
| `CpuSamplingIntervalMs` | `int` | `100` | Delay between consecutive CPU samples in ms (1–10000). |
| `CpuSamplesForAverage` | `int` | `3` | A CPU reading is the average of the last N samples (1–20); `1` = no smoothing. |
| `EnableTemperatureMonitoring` | `bool` | `true` | `false`: `GetTemperaturesAsync` fails with `systemstats.temperature_monitoring_disabled`. |
| `EnableProcessMonitoring` | `bool` | `true` | `false`: process calls fail with `systemstats.process_monitoring_disabled`; snapshots have empty process lists. |
| `MaxTopProcesses` | `int` | `10` | Default count for the top-process queries (1–200). |
| `HighCpuThresholdPercent` | `double` | `90.0` | Overall CPU usage that triggers `HighCpuUsageEvent` (1–100). |
| `HighMemoryThresholdPercent` | `double` | `90.0` | Memory usage that triggers `HighMemoryUsageEvent` (1–100). |

## Error codes

Failures carry a stable `Error.Code` from `SystemStatsErrorCodes`: `systemstats.cpu_info`, `cpu_stats`, `mem_info`, `mem_stats`, `uptime`, `process` (not found), `processes`, and the unavailable errors `process_monitoring_disabled`, `temperature_monitoring_disabled` and `temperature_unavailable`.

## Health check

`HealthCheckAsync()` confirms the active provider can read CPU stats.

```csharp
var status = await sys.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

- **Healthy** — CPU stats are accessible.
- **Degraded** — CPU stats could not be read.
- **Unhealthy** — the service is not initialized.

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.SystemStats)
