# CL.NetUtils — Changelog

All notable changes to **CodeLogic.NetUtils** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (2026-11-10)

### Changed

- Propagate cancellation and timeout through archive extraction reads; share bounded single-line log formatting across libraries.

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). Results can now be matched exhaustively
  with `switch`.
- **`DnsblCheckResult` and `IpLocationResult` are removed; DNSBL checks and GeoIP lookups return CodeLogic
  `Result<T>`.** A check or lookup that could not run is an `Error` whose `Code` is a `NetUtilsErrorCodes`
  constant, instead of a record with `ErrorMessage` set. This supersedes the 5.0 preview entries below that
  mention `DnsblCheckStatus.Error`, `DnsblCheckStatus.Cancelled`, `ErrorMessage` and `IsSuccessful`.

  | Old | New |
  |---|---|
  | `Task<DnsblCheckResult> DnsblChecker.CheckIpAsync(string, CancellationToken)` | `Task<Result<DnsblCheck>>` |
  | `Task<DnsblCheckResult> DnsblChecker.CheckIpAsync(string, DnsblCheckRequest, CancellationToken)` | `Task<Result<DnsblCheck>>` |
  | `Task<IpLocationResult> GeoIpService.LookupIpAsync(...)` | `Task<Result<IpLocation>>` |
  | `DnsblCheckResult.IpAddress`, `AddressType`, `Status`, `IsBlacklisted`, `MatchedService`, `FailedServices`, `CheckedAt` | the same members on `DnsblCheck` (via `result.Value` or a `switch`); `IsBlacklisted` is now computed from `Status` |
  | `DnsblCheckStatus.Error` (invalid address) | `ValidationError` `netutils.invalid_ip_address` |
  | `DnsblCheckStatus.Error` (DNSBL checking disabled, also from `NetUtilsLibrary.Dnsbl` while off) | `UnavailableError` `netutils.dnsbl_disabled` |
  | `DnsblCheckStatus.Error` (unexpected exception; `ErrorMessage` was the exception text) | `InternalError` `netutils.dnsbl_check_failed`, a fixed message and the exception type in `Details` |
  | `DnsblCheckStatus.Cancelled` | `UnavailableError` `netutils.cancelled` (nothing is thrown, as before) |
  | `DnsblCheckStatus.Indeterminate` with `ErrorMessage` | still a success with `Status = Indeterminate`; the zones are in `FailedServices` |
  | `DnsblCheckResult.Blacklisted/NotBlacklisted/Indeterminate/Error/Cancelled(...)` factories | removed (the `DnsblCheck` factories are internal) |
  | `IpLocationResult` data members | the same members on `IpLocation` |
  | `IpLocationResult.IsSuccessful` | `result.IsSuccess` |
  | `IpLocationResult.ErrorMessage` | `result.Error?.Message` |
  | `IpLocationResult.NotFound(...)`, and a lookup that found an entry without country, ASN or ISP | `NotFoundError` `netutils.location_not_found` |
  | `IpLocationResult.Error(...)` for an invalid address | `ValidationError` `netutils.invalid_ip_address` |
  | `IpLocationResult.Error(...)` before a database is open | `UnavailableError` `netutils.geoip_unavailable` |
  | `IpLocationResult.Error(...)` for a reader failure (exception text) | `InternalError` `netutils.geoip_lookup_failed`, a fixed message and the exception type in `Details` |
  | `NetUtilsErrors.XxxCode` constants (public) | `NetUtilsErrorCodes.Xxx` (`DownloadRefusedCode` → `DownloadRefused`, ...); the `NetUtilsErrors` factories are internal now |

  `DnsblCheckStatus` keeps only `NotBlacklisted`, `Blacklisted` and `Indeterminate`. New codes:
  `netutils.invalid_ip_address`, `netutils.dnsbl_disabled`, `netutils.dnsbl_check_failed`,
  `netutils.location_not_found`, `netutils.geoip_unavailable`, `netutils.geoip_lookup_failed`. New localized strings
  `NetUtilsStrings.DnsblCheckFailed` and `NetUtilsStrings.GeoIpLookupFailed`.
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
- **`GeoIpService.DownloadDatabaseAsync` returns `Result`**. It used to swallow every failure into a
  log line; it now reports a `netutils.*` error (`NetUtilsErrors`: `download_refused`, `download_failed`,
  `download_timeout`, `download_too_large`, `checksum_unavailable`, `checksum_mismatch`, `invalid_database`,
  `cancelled`). `await`-ing it as before still compiles.
- **GeoIP downloads are verified against MaxMind's published SHA-256 by default** (`GeoIp.VerifyChecksum`, new,
  default `true`): the `.sha256` next to the archive (`…download?suffix=tar.gz.sha256`) is fetched first and a
  mismatch installs nothing. A mirror in `TrustedDownloadHosts` that publishes no checksum needs
  `VerifyChecksum = false`.
- **`GeoIp.TimeoutSeconds` now covers the whole download** (checksum, request and the entire body; before it stopped
  at the response headers) and its default is 120 s instead of 30 s, enough for GeoLite2-City (about 70 MB) on a slow link.
- **`DnsblCheckStatus.Cancelled`** (new member): a check whose cancellation token fired reports it instead of
  `Status = Error`, so a caller can tell its own abort from a failed check. Switches over the enum need the case.
- **`GeoIp.DatabaseType` is validated.** It becomes the default file name, so a value with path separators or
  `..` could place the database outside the data directory. It must now be an edition id (letters, digits, `-`,
  `_`, `.`, at most 64 characters); otherwise configuration validation fails and `GeoIpService` throws
  `ArgumentException`.
- **Malformed DNSBL zones are no longer queried.** A zone that is not a DNS name (blank line, IP literal,
  spaces, control characters, a single label) is skipped and reported in `FailedServices`, so the check is
  `Indeterminate`. Zones are trimmed, and `MatchedService` / `FailedServices` hold the trimmed name.

### Added

- `NetUtilsLibrary.HasDnsbl` and `DnsblChecker.IsEnabled`.
- GeoIP serves the **Country, Enterprise, ASN and ISP** editions as well as City: the edition is read from the
  database metadata (`GeoIpService.LoadedDatabaseType`). New `IpLocationResult.AutonomousSystemNumber` and
  `AutonomousSystemOrganization`; `IsSuccessful` is also `true` for an ASN/ISP result without a country.
- `GeoIp.DownloadUrl` accepts a `{DatabaseType}` placeholder, and the default URL uses it, so the configured
  edition is downloaded (`GeoIpConfig.DatabaseTypePlaceholder`, `GeoIpConfig.IsValidDatabaseType`).
- `DnsblCheckStatus` (`NotBlacklisted`, `Blacklisted`, `Indeterminate`, `Error`), `DnsblCheckResult.Status`,
  `DnsblCheckResult.FailedServices` and `DnsblCheckResult.Indeterminate(...)`.
- `DnsblCheckRequest.ParallelQueries` (default `true`).
- `GeoIpConfig.TrustedDownloadHosts` and `GeoIpConfig.TryGetTrustedDownloadUri(...)`.
- **`NetUtilsLibrary` implements `IConfigReloadable`**: a change to `config.netutils.json` (admin UI,
  `LibraryManager.UpdateConfigAsync`, reload, file watcher) is applied without a restart. DNSBL settings replace
  `Dnsbl` at once; GeoIP settings build a new `GeoIpService` (downloading when needed) and swap it in, the old one
  closing after a grace period; a GeoIP configuration that fails to initialize keeps the previous database serving
  and reports Degraded; the library can be turned on and off. Read `Dnsbl` and `GeoIp` per use rather than caching them.
- `GeoIp.MaxDownloadBytes` (default 512 MB): the most a database download may be, enforced on the archive body as it
  arrives and again on the unpacked database, so neither a long body nor a small archive that inflates to gigabytes
  fills the disk (`netutils.download_too_large`).
- `NetUtilsErrors`: the `netutils.*` error codes and factories.
- `DnsblCheckResult.Cancelled(...)` and the localized string `NetUtilsStrings.DnsblCancelled`.

### Fixed

- **`DnsblCheckRequest.IsAllowedAsync` receives the canonical address**. It was given the caller's
  text, so an allowlist holding `1.2.3.4` did not match a check for `::ffff:1.2.3.4` or `0x01.0x02.0x03.0x04`; it now
  gets `ip.ToString()` of the parsed (IPv4-unmapped) address. `DnsblCheckResult.IpAddress` still echoes the input.
- **Caller cancellation of a DNSBL check is no longer reported as `Status = Error`**; see `DnsblCheckStatus.Cancelled`.
- **`GeoIpService.LookupIpAsync` no longer holds the service lock for the MaxMind lookup.** It snapshots the reader
  under the lock and looks up outside it, so lookups run concurrently and a database swap never waits for them; a
  replaced reader stays open for a grace period (10 s) so lookups that started on it finish.
- `NetUtilsLibrary` awaits the GeoIP initialization with `ConfigureAwait(false)` and a token that is cancelled when
  the library stops, so a download still running at shutdown gives up instead of outliving the library.

- **`NetUtilsLibrary.Dnsbl` no longer throws when DNSBL checking is disabled**. The docs
  said it is always available, but with `dnsbl.enabled` (or `enabled`) off it threw `InvalidOperationException`.
  It now returns a checker whose checks answer `Status = Error` with "DNSBL checking is disabled", without a
  DNS query. It still throws before the library has initialized.
- **GeoIP is no longer City-only**. `DatabaseType` was documented as any edition, but only a
  City database installed and every lookup called `City()`. A downloaded database must now be of the same kind
  as `DatabaseType` (a Country download never replaces a City database).
- **`NetUtilsStrings` is used**. The localization model was registered but nothing read it.
  Lifecycle and DNSBL/GeoIP log lines, and the error messages in `DnsblCheckResult` / `IpLocationResult`
  (invalid address, DNSBL disabled, GeoIP not initialized, location not found), now come from it; new strings
  `InvalidIpAddressFormat`, `DnsblDisabled`, `GeoIpNotInitialized`, `LocationNotFound`. The English defaults
  match the previous messages.
- **Caller input can no longer forge log lines.** IP addresses and zones are logged with control characters
  replaced and cut to 64 characters.
- **A corrupt GeoIP download can no longer replace a working database (re-audit R2).** The extracted file is opened with the MaxMind reader, must be a supported edition of the configured kind, and must answer a test lookup. Only then is it renamed into place. The open reader switches without a gap, and on any failure the old file and reader stay in use. Readers load the database into memory, so the file is never locked.
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
- The guide lists `DnsblCheckStatus.Cancelled` and the `Cancelled` factory, shows `DnsblCheckResult` and
  `IpLocationResult` as plain (unsealed) records, as in the code, and its status switch handles `Cancelled`.
  The setup examples add the `CL.NetUtils.Models` using they need.
- The README's last line, a relative link that broke on nuget.org, is removed.

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
