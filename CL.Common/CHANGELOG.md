# CL.Common — Changelog

All notable changes to **CodeLogic.Common** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

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

- Full rewrite of the README and docs to house style. The README is now concise
  Markdown-only (renders on both GitHub and NuGet): install, quick start showing the
  static helpers and the optional `CommonLibrary` load, a grouped feature list, and a
  link to the docs site. Notes that `CL.Common` is stateless and needs no configuration.
- Expanded the documentation site into a three-page set under `docs/libs/common/`:
  an **Overview** (toolkit map, how static helpers are organized by namespace, the full
  catalog table, health check), **Security & Data** (`Encryption`, `Hashing`,
  `IdGenerator`, `PasswordGenerator`/`PasswordStrength`, `JsonHelper`, `TypeConverter`,
  `CompressionHelper`), and **Utilities** (`StringHelper`/`StringValidator`,
  `DateTimeHelper`, the web helpers, the networking set, `CronParser`, `CLU_Imaging`,
  `ICache`/`MemoryCache`, `FileSystem`, and the reflection helpers). Each page documents
  per-member return types accurately — plain values vs. `Result`/`Result<T>`. No API changes.

## [4.5.2] — 2026-06-20

### Documentation

- Documented the full public surface of the toolkit in the README. Previously only
  imaging, hashing, caching, file handling, compression, and networking were listed;
  the README now also covers **Security** (`Encryption` AES-256-GCM and the `Hashing`
  PBKDF2/HMAC helpers), **Generators** (`IdGenerator`, `PasswordGenerator`),
  **Data** (`JsonHelper`), **Conversion** (`TypeConverter`), **Parser** (`CronParser`),
  **Time** (`DateTimeHelper`), **Strings** (`StringHelper`, `StringValidator`),
  **Web** (`UrlHelper`, `HtmlHelper`, `HttpClientHelper`, `HttpHeaderHelper`),
  the full **Networking** set (`NetworkPing`, `NetworkDns`, `SubnetCalculator`,
  `TraceRoute`), and **Reflection** (`AssemblyHelper`, `ReflectionHelper`).
- Corrected the Quick Start hashing example: the helper is `Hashing.Sha256(...)`
  (no `CLU_Hashing.SHA256` type exists), and clarified that GZip, Brotli, and LZ4
  are all available from `CompressionHelper`. No API changes.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh across every CodeLogic library for the v4
  baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from the assembly's `AssemblyVersion`
  attribute at runtime instead of a hard-coded string.

## [4.0.2] — 2026-04-09

### Changed

- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite — republished as v4.0.0 to reset the version line under the
unified v4 baseline. All public APIs refreshed.

### Notes

- Bundles cross-platform native assets transitively for libraries that need
  them (e.g. SkiaSharp for image handling).
- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.Common).
