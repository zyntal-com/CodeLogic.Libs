# CL.NetUtils — Changelog

All notable changes to **CodeLogic.NetUtils** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). Results can now be matched exhaustively
  with `switch`.
- **A DNSBL check that could not query a zone is no longer reported as clean.** Timeouts, resolver
  failures and DNSBL error answers (Spamhaus `127.255.255.x`) used to end as `NotBlacklisted`. When no
  zone lists the address but some zone failed, the result is now `Status = DnsblCheckStatus.Indeterminate`
  with the zones in `FailedServices` and a message in `ErrorMessage`; `IsBlacklisted` stays `false`.
  NXDOMAIN is still "not listed". Check `Status` where an unchecked address must not pass.
- **`GeoIp.DownloadUrl` must be `https` on a trusted host.** The MaxMind credentials were sent to any
  configured URL. The host must now be listed in the new `GeoIp.TrustedDownloadHosts` (default
  `["download.maxmind.com"]`); otherwise the download is refused before any request and configuration
  validation fails.
- **SharpCompress dependency removed.** The GeoIP archive is read with the built-in `System.Formats.Tar`
  and `System.IO.Compression` (`tar.gz` and `zip`).

### Added

- `DnsblCheckStatus` (`NotBlacklisted`, `Blacklisted`, `Indeterminate`, `Error`), `DnsblCheckResult.Status`,
  `DnsblCheckResult.FailedServices` and `DnsblCheckResult.Indeterminate(...)`.
- `DnsblCheckRequest.ParallelQueries` (default `true`).
- `GeoIpConfig.TrustedDownloadHosts` and `GeoIpConfig.TryGetTrustedDownloadUri(...)`.

### Fixed

- **A corrupt GeoIP download can no longer replace a working database (re-audit R2).** The extracted file is opened with the MaxMind reader, must be a City database, and must answer a test lookup. Only then is it renamed into place. The open reader switches without a gap, and on any failure the old file and reader stay in use. Readers load the database into memory, so the file is never locked.
- **`GeoIp.AutoUpdate` now works:** before, nothing read it. On startup it refreshes an existing database when credentials are set; a failed refresh is logged and the existing database stays in use. A missing database is downloaded regardless.
- **`Dnsbl.ParallelQueries` is honoured.** Zones were always queried one at a time. Now the zones of a
  list are queried concurrently by default, and with `ParallelQueries = false` one at a time, stopping at
  the first listing. DNS lookups use the asynchronous resolver, so the per-zone timeout is enforced.
- **GeoIP database replacement is selective and atomic.** Every archive entry (licence, readme, ...) was
  extracted straight into the database directory, over the live file. Now the archive is unpacked in a
  private temporary directory, only the `.mmdb` entry is extracted (by content, never by entry path),
  checked for the MaxMind metadata marker, and moved over the database in one rename; an open reader is
  switched to the new file. A failed or invalid download leaves the existing database untouched. A custom
  `DatabasePath` with a different file name than the archive's `.mmdb` now gets the database too.
- `LookupIpAsync` reads the database reader under its lock, so a concurrent reload or dispose cannot
  race it.
- Package versions are pinned (`MaxMind.GeoIP2` 6.1.0) instead of floating.
- **DNSBL queries are built from the parsed address.** The IPv4 query label reversed the dot-separated input
  text, so a shorthand that `IPAddress.TryParse` accepts (`1.2.3` = 1.2.0.3, hex `0x01.0x02.0x03.0x04`,
  a single 32-bit number) queried the wrong name and was never found listed. The label now comes from the
  address bytes, and an IPv4-mapped IPv6 address (`::ffff:a.b.c.d`) is checked against the IPv4 zones.
- **More non-routable addresses skip the DNSBL query:** IPv4 `0.0.0.0/8`, link-local `169.254/16`, shared
  address space (CGNAT) `100.64/10`, `192.0.0/24`, the documentation ranges (`192.0.2/24`, `198.51.100/24`,
  `203.0.113/24`), benchmarking `198.18/15`, multicast `224/4`, reserved `240/4` and broadcast; IPv6 unspecified
  `::` and documentation `2001:db8::/32`. Like the existing private ranges they return *not blacklisted*.

### Documentation

- Configuration examples use the camelCase keys the CodeLogic loader reads (PascalCase keys were ignored).
- The DNSBL guide describes the actual query order, fallback rule and failure handling.

## 2026-10-30

### Fixed

- **Health no longer reports optional features that are off as faults.** GeoIP with neither a database file
  nor MaxMind credentials is now *not configured* and Healthy (it was Degraded "GeoIP unavailable"), and is
  logged at Info instead of a startup warning. Degraded now means GeoIP was set up (database file or
  credentials) and still failed to start; the message carries the reason. The health message lists both
  services, e.g. `DNSBL on, GeoIP not configured`. A library with both services switched off is Healthy too.

### Added

- `GeoIpService.DatabasePath` and `GeoIpService.CanDownload`.

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

### Documentation

- Full rewrite of the README and the deep docs page to house style: NuGet + MIT
  badges, blockquote tagline, concise quick-start, feature list, and a complete
  configuration table covering every `Dnsbl.*` and `GeoIp.*` field.
- Documented the correct load pattern (`Libraries.LoadAsync<NetUtilsLibrary>()`
  → `ConfigureAsync` → `StartAsync` → `Libraries.Get`) and both `Dnsbl` /
  `GeoIp` accessors, with the `HasGeoIp` guard for the optional GeoIP service.
- Documented both `CheckIpAsync` overloads, the default IPv4/IPv6 blacklist
  zones, and the per-call `DnsblCheckRequest` (per-call zone lists, timeout, and
  async allowlist predicate).
- Documented the `DnsblCheckResult` and `IpLocationResult` records field by
  field, including `IpLocationResult.IsSuccessful`.
- Documented GeoIP database resolution and auto-download: `DatabasePath` vs the
  `{dataDir}/geoip/{DatabaseType}.mmdb` fallback, and `DownloadDatabaseAsync`
  fetching/extracting the `.mmdb` via HTTP Basic Auth with `AccountId` +
  `LicenseKey`.

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README Quick Start to match the real API: `Dnsbl.CheckIpAsync`
  (was `CheckAsync`) returning `DnsblCheckResult.IsBlacklisted` / `MatchedService`
  (was `IsListed`), and the async `GeoIp.LookupIpAsync` returning
  `IpLocationResult.CountryName` / `CityName` (was a synchronous `Lookup`).
- Documented the `DnsblCheckRequest` overload of `CheckIpAsync` — per-call
  service lists plus an optional async allowlist predicate (`IsAllowedAsync`).
- Documented the `DnsblCheckResult` and `IpLocationResult` result records and
  the `IpLocationResult.IsSuccessful` helper.
- Documented that local/private addresses are short-circuited as not blacklisted.
- Added the `GeoIp.DownloadUrl` field to the sample `config.netutils.json`.

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

### Added

- `DnsblCheckRequest` overload that accepts a caller-supplied DNSBL service
  list — useful when the calling app stores its own service registry instead
  of relying on the bundled defaults.

### Changed

- Annotated NetUtils configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. GeoIP, DNSBL, and IP/CIDR utilities.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.NetUtils).
