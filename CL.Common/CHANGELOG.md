# CL.Common — Changelog

All notable changes to **CodeLogic.Common** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). Results can now be matched
  exhaustively with `switch`.
- `NetworkPing.PingAsync` returns `Result<PingResponse>` instead of `Result<PingResult>`. The old
  `PingResult(bool Success, long RoundTripMs, string Host)` reported "no reply" as a *successful* result
  with `Success = false`; now success always means the host replied, and
  `PingResponse(string Host, string Address, long RoundTripMs)` adds the answering address. No reply is a
  failure: `TimeoutError` `networking.ping_timeout`, `UnavailableError` `networking.ping_unreachable`
  (unreachable host/network or unresolvable name; `Details` carries the `IPStatus`), otherwise
  `InternalError` `networking.ping_failed`.

### Added

- `CompressionHelper.CompressZstandard` / `DecompressZstandard` → `Result<byte[]>`: Zstandard via the
  .NET 11 built-in `System.IO.Compression.ZstandardStream` (no extra package). Same shape and error
  handling as the Brotli pair; failures are `compression.zstandard_compress_failed` /
  `compression.zstandard_decompress_failed`.

## 2026-10-27 (4.9.104)

### Added

- `CLU_Imaging.ProcessImage(stream, ImageProcessOptions?)` → `Result<ProcessedImage>` for user uploads:
  checks the pixel count from the header **before** decoding (`MaxPixels`, default
  `CLU_Imaging.DefaultMaxPixels` = 40 megapixels; `imaging.too_large`), turns photos upright from their EXIF
  orientation (`ApplyOrientation`), scales down (never up) into `MaxWidth` × `MaxHeight`, and encodes to
  `Format` (default WebP) at `Quality` (default 85). Re-encoding drops metadata such as GPS positions.
  `ProcessedImage` carries the bytes, the final width and height, and the format.
- `CLU_Imaging.FitWithin(width, height, maxWidth, maxHeight)`: the scaled-down size for a box.
- `ResizeImage(stream, targetWidth, targetHeight, allowCrop, format, quality = 90)`: resize to another
  format than PNG.

### Fixed

- **Decompression bombs.** `ConvertImage` and `ResizeImage` decoded any image, so a small PNG claiming
  50,000 × 50,000 pixels allocated about 10 GB. They now refuse images over `DefaultMaxPixels` with
  `imaging.too_large`; `ProcessImage` makes the limit configurable.
- **Sideways phone photos.** `ConvertImage` and `ResizeImage` ignored the EXIF orientation, so portrait
  photos came out rotated. They now turn the image upright first.
- **`ResizeImage(..., allowCrop: false)` stretched the image** to exactly the target size instead of
  keeping the aspect ratio as documented; it now fits inside the target. The file overload's
  `maintainAspectRatio: true` (which maps to it) is fixed with it, and its doc for `false` now says what it
  does: scale to cover and crop.
- **Crop edges.** The cover-and-crop path truncated the scaled size and could leave a transparent row or
  column; it now rounds up. Resizing uses Mitchell cubic sampling instead of nearest-neighbour.
- `IsValidSize`, `IsValidAspectRatio` and `GetImageDimensions` read only the header instead of decoding the
  whole image (fast, and safe for huge images). Their `catch` no longer swallows `OutOfMemoryException`.

### Upgrade notes

- Images over 40 megapixels now fail `ConvertImage`/`ResizeImage` with `imaging.too_large`. Use
  `ProcessImage` with a larger `MaxPixels` if you need them.
- Output is upright: an EXIF-rotated photo comes out with width and height swapped compared to before.
- `ResizeImage(stream, w, h, allowCrop: false)` (and the file overload with `maintainAspectRatio: true`) may
  return an image smaller than `w` × `h` on one side.
- `GetImageDimensions` reports the stored size (before EXIF orientation), as it did before.

## 2026-10-27

### Security

- `HtmlHelper.Sanitize` is now an allowlist sanitizer. It used to remove only `<script>`/`<style>`
  blocks and double-quoted `on*="…"` attributes, so unquoted or single-quoted handlers
  (`<img src=x onerror=alert(1)>`), `javascript:` links, `<iframe>`, `<object>`, `<svg>`, `<form>` and
  inline styles got through. It now rebuilds the markup: the formatting elements in the new
  `SanitizeAllowedTags` (p, br, b, strong, i, em, lists, headings, blockquote, code, …) without
  attributes, links with an `href` only when it is http, https, mailto or relative (plus
  `rel="nofollow noopener noreferrer"`); script, style, iframe, object, svg and similar elements are
  removed with their content, other tags are removed and their text kept.

### Upgrade notes

- Output changes: attributes (`class`, `style`, `title`, `src`), images, tables and embeds are no longer
  kept. For rich text that needs them, use a full allowlist sanitizer such as CL.WebLogic's
  `WebHtmlSanitizer`.

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
