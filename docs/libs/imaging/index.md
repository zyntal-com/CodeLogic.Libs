# CL.Imaging

> Image decoding, a lazy editing pipeline, EXIF metadata, colour analysis and web helpers — built on SkiaSharp.

`CL.Imaging` loads images safely (pixel limit from the header, byte limit while reading), edits them through a
chainable pipeline, reads their metadata and analyses them. Every fallible call returns a CodeLogic `Result` /
`Result<T>` whose `Error.Code` is one of `ImagingErrors` (`imaging.*`); bad input never throws. It moved out of
`CL.Common` in 5.0 (see [Migrating from CL.Common](#migrating-from-clcommon)).

| | |
|---|---|
| **Package** | [`CodeLogic.Imaging`](https://www.nuget.org/packages/CodeLogic.Imaging) |
| **Library class** | `CL.Imaging.ImagingLibrary` (optional) |
| **Config file** | `config.imaging.json` |
| **Dependencies** | SkiaSharp 4.x · SkiaSharp.NativeAssets.Linux.NoDependencies |

Sub-pages:

- **[Editing](editing.md)** — the pipeline: geometry, colour, filters, drawing, watermarks, encoding.
- **[Metadata](metadata.md)** — probing, EXIF, orientation, ICC profiles, stripping.
- **[Analysis](analysis.md)** — colours and palettes, BlurHash, perceptual hashes, smart crop, comparison.

## Install

```bash
dotnet add package CodeLogic.Imaging
```

Nothing has to be loaded: `ImageDocument`, `ImageTools` and the analysis classes work on their own. Load the library
only to drive the defaults from configuration:

```csharp
await Libraries.LoadAsync<CL.Imaging.ImagingLibrary>();   // optional
```

## Loading

```csharp
Result<ImageDocument> loaded = await ImageDocument.LoadAsync(stream, new ImageLoadOptions { MaxPixels = 40_000_000 }, ct);
using var doc = loaded.Value!;
```

| Member | Purpose |
|---|---|
| `Load(Stream)` / `LoadAsync(Stream, options, ct)` | Read to the end (byte limit), check the header's pixel count, decode. |
| `Load(ReadOnlySpan<byte>)` | Decode bytes. |
| `LoadFile(path)` / `LoadFileAsync(path, options, ct)` | Decode a file (`imaging.file_not_found` when missing). |
| `Create(width, height, color)` | A blank image. |
| `FromSkiaBitmap(bitmap)` / `ToSkiaBitmap()` | Interop with SkiaSharp (copies). |
| `Width`, `Height`, `DisplayWidth`, `DisplayHeight` | Stored size and size once upright. |
| `Orientation`, `Metadata`, `IccProfile`, `ExifData`, `SourceFormat` | What was loaded. |
| `Edit()` | Start a pipeline. |
| `Encode(format, quality)` / `Encode(ImageEncodeOptions)` | Encode as is. |

`ImageLoadOptions.MaxPixels` / `MaxBytes` default to `ImagingDefaults.Current` (40 MP, 100 MB); 0 disables a limit.
Decodable: JPEG, PNG, WebP, GIF (first frame), BMP, ICO, and HEIF/AVIF where the platform's Skia has the codec
(otherwise `imaging.unsupported_format`).

## One-call helpers (`ImageTools`)

```csharp
// Upload: limits, upright, scale down into the box, re-encode. EXIF (including GPS) is dropped.
using var upload = ImageTools.ProcessUpload(stream, new UploadOptions { MaxWidth = 1600, MaxHeight = 1600 }).Value!;

// Responsive variants from one decode; widths above the image's own collapse to one full-size variant.
using var variants = ImageTools.GenerateVariants(doc, [320, 640, 1280], ImageFormat.Webp).Value!;
var srcset = variants.ToSrcSet(v => $"/img/{id}-{v.Width}.webp");

using var thumb = ImageTools.Thumbnail(doc, 256, 256, ResizeMode.Fill, smartCrop: true).Value!;
var dataUri = ImageTools.ToDataUri(doc, ImageFormat.Png).Value!;
```

`ProcessUploadAsync`, `GenerateVariantsAsync` and `ThumbnailAsync` take a stream and a `CancellationToken`.

## Encoded output

`EncodedImage` holds the bytes in an `ArrayPool` buffer. Dispose it; copy with `ToArray()` to keep the bytes longer.

| Member | Purpose |
|---|---|
| `Format`, `Width`, `Height`, `Length`, `MimeType` | Describe the output. |
| `Span`, `Memory` | The bytes (valid until disposed). |
| `ToArray()`, `AsStream()`, `ToBase64()`, `ToDataUri()` | Get the bytes out. |
| `WriteToAsync(stream, ct)`, `SaveAsync(path, ct)` | Write them. |

## Configuration

`config.imaging.json` (only read when `ImagingLibrary` is loaded; applied live on change):

| Setting | Default | Meaning |
|---|---|---|
| `DefaultMaxPixels` | `40000000` | Largest image decoded (0 = no limit). |
| `DefaultMaxBytes` | `104857600` | Largest encoded input read (0 = no limit). |
| `DefaultQuality` | `85` | Lossy quality when a call gives none. |
| `DefaultFormat` | `Webp` | Output format when a call gives none (`Jpeg`, `Png`, `Webp`). |
| `StripMetadataByDefault` | `true` | Drop EXIF on encode unless a call keeps it. |

Without the library: `ImagingDefaults.Current = ImagingSettings.BuiltIn with { Quality = 90 };`.

## Errors

| Code | Kind | When |
|---|---|---|
| `imaging.too_large` | Validation | Over the pixel or byte limit, or beyond the 16384² output cap. |
| `imaging.unsupported_format` | Validation | HEIF/AVIF without a platform codec; encoding to anything but JPEG/PNG/WebP. |
| `imaging.decode_failed` | Validation | Not an image, corrupt or truncated. |
| `imaging.invalid_argument` | Validation | A pipeline argument out of range, a crop outside the image, a bad BlurHash. |
| `imaging.encode_failed` | Internal | The encoder produced nothing. |
| `imaging.cancelled` | Unavailable | The `CancellationToken` was cancelled. |
| `imaging.file_not_found` | NotFound | A missing input file. |
| `imaging.io_failed` | Internal | Stream or file I/O failed. |
| `imaging.processing_failed` | Internal | Unexpected engine failure. |

## Limitations

- No animated output: animated GIF/WebP decode their first frame.
- HEIF/AVIF decoding depends on the platform's Skia build; no decoder is bundled.
- Text uses the platform's fonts; minimal Linux containers may have none (use `ImageFont.FromFile`).

## Migrating from CL.Common

`CL.Common.Imaging.CLU_Imaging` is gone. `ProcessImage` → `ImageTools.ProcessUpload`, `ResizeImage` →
`doc.Edit().Resize(...)`, `CreateThumbnail` → `ImageTools.Thumbnail`, `GetImageDimensions` / `IsValidSize` →
`ImageInfo.Probe`. The full table is in the package's
[MIGRATION.md](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/CL.Imaging/MIGRATION.md).
