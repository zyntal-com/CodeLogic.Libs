# CodeLogic.SystemStats

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SystemStats)](https://www.nuget.org/packages/CodeLogic.SystemStats)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Cross-platform CPU, memory, uptime, and process statistics for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — one API over Windows performance counters and Linux `/proc`.

Samples live system metrics through platform-specific providers: Windows reads CPU info from the registry, CPU usage from `PerformanceCounter`, and total/available RAM via the `GlobalMemoryStatusEx` P/Invoke; Linux reads `/proc/cpuinfo`, `/proc/stat`, `/proc/meminfo`, and `/proc/uptime`. Every data method returns a `Result<T>`, results are cached for a short window, and breaching CPU/memory thresholds publishes events on the CodeLogic event bus.

## Install

```bash
dotnet add package CodeLogic.SystemStats
```

## Quick start

```csharp
using CL.SystemStats;

await Libraries.LoadAsync<SystemStatsLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var sys = Libraries.Get<SystemStatsLibrary>();

// Every data method returns Result<T>: check IsSuccess, then read Value.
Result<CpuStats> cpu = await sys.GetCpuStatsAsync();
if (cpu.IsSuccess)
    Console.WriteLine($"CPU: {cpu.Value!.OverallUsagePercent:F1}%");

Result<MemoryStats> mem = await sys.GetMemoryStatsAsync();
if (mem.IsSuccess)
    Console.WriteLine($"RAM: {mem.Value!.UsagePercent:F1}% ({mem.Value.UsedMiB} MiB used)");

// One combined reading: CPU + memory + uptime + top processes.
Result<SystemSnapshot> snap = await sys.GetSystemSnapshotAsync();
```

## Features

- **CPU** — static `CpuInfo` (model, cores, vendor, max speed, architecture) via `GetCpuInfoAsync()`, and live `CpuStats` (overall + per-core usage) via `GetCpuStatsAsync()` with configurable sampling.
- **Memory** — static `MemoryInfo` (total RAM) and live `MemoryStats` (used / available / cached / buffers / percent), each with MiB/GiB helper properties.
- **Uptime** — system uptime since last boot via `GetSystemUptimeAsync()`.
- **Processes** — enumerate all (`GetAllProcessesAsync`), query by PID (`GetProcessStatsAsync`), or rank by CPU / memory (`GetTopProcessesByCpuAsync` / `GetTopProcessesByMemoryAsync`).
- **Snapshots** — `GetSystemSnapshotAsync()` rolls CPU, memory, uptime, and top processes into one `SystemSnapshot`.
- **Threshold events** — publishes `HighCpuUsageEvent` / `HighMemoryUsageEvent` when usage crosses the configured limits, and `SystemSnapshotTakenEvent` after each snapshot.
- **Result caching** — short-lived caching of common reads; clear it with `sys.Stats.ClearCache()`.

## Configuration

Auto-generated on first run as `config.systemstats.json` (section `systemstats`):

```json
{
  "EnableCaching": true,
  "CacheDurationSeconds": 5,
  "CpuSamplingIntervalMs": 100,
  "CpuSamplesForAverage": 3,
  "EnableTemperatureMonitoring": true,
  "EnableProcessMonitoring": true,
  "MaxTopProcesses": 10,
  "HighCpuThresholdPercent": 90.0,
  "HighMemoryThresholdPercent": 90.0
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableCaching` | `true` | Cache common reads for `CacheDurationSeconds`. |
| `CacheDurationSeconds` | `5` | Cache lifetime in seconds (1–600). |
| `CpuSamplingIntervalMs` | `100` | Delay between CPU samples in ms (1–10000). |
| `CpuSamplesForAverage` | `3` | Number of CPU samples averaged per reading (1–20). |
| `EnableTemperatureMonitoring` | `true` | Reserved for temperature sampling support. |
| `EnableProcessMonitoring` | `true` | Enable process enumeration / ranking. |
| `MaxTopProcesses` | `10` | Default count for top-process queries (1–200). |
| `HighCpuThresholdPercent` | `90.0` | CPU usage that triggers `HighCpuUsageEvent` (1–100). |
| `HighMemoryThresholdPercent` | `90.0` | Memory usage that triggers `HighMemoryUsageEvent` (1–100). |

## Documentation

Full guide: **[CL.SystemStats documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/systemstats.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- Windows: `System.Diagnostics.PerformanceCounter` 9.x · `System.Management` 9.x
- Linux: read access to the `/proc` filesystem

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
