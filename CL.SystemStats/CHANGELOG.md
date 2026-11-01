# CL.SystemStats — Changelog

All notable changes to **CodeLogic.SystemStats** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). This library's API is unchanged, and
  its results can now be matched exhaustively with `switch`.

### Added

- `GetTemperaturesAsync()` on `SystemStatsService` and `SystemStatsLibrary`, the `TemperatureReading` record and a
  default `ISystemStatsProvider.GetTemperaturesAsync()` (unavailable). Linux reads `/sys/class/thermal` and
  `/sys/class/hwmon`; Windows reports `systemstats.temperature_unavailable`.
- `SystemStatsErrorCodes` with every error code the library returns.

### Changed

- `System.Diagnostics.PerformanceCounter` 11.0 (RC3 until .NET 11 GA).
- **`System.Management` dependency removed** (audit 2026-11-01): nothing used it.
- `GetSystemSnapshotAsync()` is composed by `SystemStatsService` (fresh CPU, memory and uptime, the top
  `MaxTopProcesses` processes) instead of the provider's `GetSystemSnapshotAsync`, so it honours the switches below.
- Windows per-core CPU counters are created once and read after the sampling interval; before, each core got a
  new counter sampled over 10 ms, one core after another.
- **Cache refreshes are coalesced.** When a cached value (CPU info and stats, memory info and stats, uptime,
  the process list) expired, every concurrent caller sampled the provider itself, so a burst of requests ran
  as many CPU samplings or process enumerations at once. Concurrent callers now share one refresh per key; a
  failed or throwing refresh is shared by the callers waiting on it and is not cached.

### Fixed

- **`EnableProcessMonitoring` is honoured** (audit 2026-11-01). It was never read. With it off, the process calls
  fail with an unavailable error `systemstats.process_monitoring_disabled` without touching any process, and
  snapshots have empty process lists.
- **`EnableTemperatureMonitoring` is honoured** (audit 2026-11-01): it gates the new `GetTemperaturesAsync()`
  (`systemstats.temperature_monitoring_disabled`).
- **`CpuSamplesForAverage` is honoured** (audit 2026-11-01). It was never read. A CPU reading (and the
  `HighCpuUsageEvent` threshold check) now uses the average of the last N samples, overall and per core; `1` gives
  raw samples.
- **Windows `CpuInfo` is real data** (audit 2026-11-01). It reported `Vendor = "Windows"`, `MaxSpeedMHz = 0` and the
  logical count as the physical core count. Vendor and nominal clock now come from the registry, physical cores and
  logical processors from `GetLogicalProcessorInformationEx`.
- **Linux `PhysicalCoreCount` counts cores, not sockets** (audit 2026-11-01): the distinct
  `(physical id, core id)` pairs of `/proc/cpuinfo`. `MaxSpeedMHz` prefers `cpufreq/cpuinfo_max_freq` over the
  current `cpu MHz`, and ARM model/vendor lines are recognised.
- The Linux `GetProcessStatsAsync` disposes the `Process` it opens.

### Documentation

- Configuration examples in the README and the docs use the camelCase keys the CodeLogic loader reads.
  The PascalCase keys shown before are matched case-sensitively and were ignored, leaving defaults active.

## 2026-10-24

### Changed

- Requires **CodeLogic 4.9+**. The official libraries move to the 4.9 line together with the framework,
  whose live config changes the stateful libraries now use. No API changes in this library.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- `GetTopProcessesByCpuAsync` now measures real per-process CPU usage by
  sampling each process's processor time over an interval, instead of sorting by
  a value that was always 0 (which returned an arbitrary order). Sampling uses
  the configured `CpuSamplingIntervalMs`.

### Documentation

- Full rewrite of the README and the system-stats guide to the shared house
  style: concise NuGet-friendly README (badges, tagline, install, quick start,
  features, configuration table, docs link, requirements) and a single deep docs
  page at `docs/libs/systemstats.md`.
- Documented the cross-platform provider split (Windows registry / PerformanceCounter /
  `GlobalMemoryStatusEx` / `TickCount64` vs. Linux `/proc`), with the unknown-platform
  fall back to the Linux provider.
- Covered every method as `Task<Result<T>>` and the `SystemStatsLibrary` forwarding
  surface alongside the `Stats` service (`ClearCache`, `GetPlatformInfo`, `IsInitialized`).
- Clarified that per-process `CpuUsagePercent` is `0.0` on both platforms
  (point-in-time, not sampled), that `HandleCount` is the fd count on Linux, and
  that `CachedBytes` / `BuffersBytes` are `0` on Windows.
- Documented which calls are cached vs. uncached, the published events and which
  methods trigger threshold checks, the validated `config.systemstats.json`
  fields with ranges, and the health-check states.

## [4.5.1] — 2026-06-20

### Documentation

- Rewrote the system-monitoring guide and refreshed the README to match the
  current API: `Result<T>`-returning methods, static info accessors
  (`GetCpuInfoAsync` / `GetMemoryInfoAsync`), `GetSystemUptimeAsync`,
  `GetAllProcessesAsync`, and the `CpuInfo` / `CpuStats` / `MemoryInfo` /
  `MemoryStats` / `ProcessStats` / `SystemSnapshot` record shapes (including
  MiB/GiB helper properties).
- Documented the published events (`SystemSnapshotTakenEvent`,
  `HighCpuUsageEvent`, `HighMemoryUsageEvent`) and which calls trigger threshold
  checks.
- Documented result caching behavior, the cached vs. uncached calls, and
  `ClearCache()` via the `Stats` property.
- Corrected the configuration reference to the real `config.systemstats.json`
  fields and value ranges.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated SystemStats configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

### Fixed

- Removed the Windows-only condition on transitive dependencies so the
  library actually loads on Linux containers.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Cross-platform CPU / memory / disk / network sampling
backed by Windows performance counters and Linux `/proc`.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.SystemStats).
