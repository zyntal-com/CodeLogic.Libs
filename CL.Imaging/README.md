# CodeLogic.Imaging

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Imaging)](https://www.nuget.org/packages/CodeLogic.Imaging)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Image decoding, a lazy editing pipeline, EXIF metadata, colour analysis and web helpers for
> [CodeLogic 5](https://github.com/zyntal-com/CodeLogic), built on [SkiaSharp](https://github.com/mono/SkiaSharp).

Every fallible call returns a CodeLogic `Result` / `Result<T>` whose `Error` carries a stable `imaging.*` code
(`ImagingErrors`); bad input never throws. Uploads are checked against a pixel limit read from the header *before*
any pixel memory is allocated, and against a byte limit while reading. Linux servers are covered by
`SkiaSharp.NativeAssets.Linux.NoDependencies`.

Imaging used to live in CodeLogic.Common (`CL.Common.Imaging.CLU_Imaging`); see [MIGRATION.md](MIGRATION.md).

## Install

```bash
dotnet add package CodeLogic.Imaging
```

No library has to be loaded: the static and fluent APIs work on their own. Loading `ImagingLibrary` only applies
the `imaging` config section to the process-wide defaults.

## Quick start

```csharp
using CL.Imaging;

Result<ImageDocument> loaded = await ImageDocument.LoadAsync(stream, new ImageLoadOptions { MaxPixels = 40_000_000 }, ct);
if (loaded.IsFailure) return loaded.Error;           // imaging.decode_failed, imaging.too_large, ...
using var doc = loaded.Value!;

using var logo = (await ImageDocument.LoadFileAsync("logo.png", ct: ct)).Value!;

Result<EncodedImage> output = doc.Edit()
    .AutoOrient()
    .Resize(1200, 800, ResizeMode.Fit, ResampleQuality.High)
    .Brightness(0.05).Contrast(0.1).Saturation(-0.2)
    .Sharpen(0.4)
    .RoundCorners(24)
    .Watermark(new WatermarkImage(logo, RelativeWidth: 0.2), Anchor.BottomRight, opacity: 0.6, margin: 16)
    .StripMetadata()
    .Encode(ImageFormat.Webp, quality: 82, ct);

using var encoded = output.Value!;                   // pooled buffer: dispose when done
await encoded.SaveAsync("out.webp", ct);
```

One-call helpers:

```csharp
// Safe upload handling: limits, upright, fit into a box, re-encode (drops EXIF and GPS).
using var avatar = ImageTools.ProcessUpload(upload, new UploadOptions { MaxWidth = 512, MaxHeight = 512 }).Value!;

// Responsive srcset variants from one decode.
using var variants = ImageTools.GenerateVariants(doc, [320, 640, 1280], ImageFormat.Webp).Value!;
string srcset = variants.ToSrcSet(v => $"/img/photo-{v.Width}.webp");

using var thumb = ImageTools.Thumbnail(doc, 200, 200, smartCrop: true).Value!;
string dataUri = ImageTools.ToDataUri(doc, ImageFormat.Png).Value!;
```

## Features

- **Formats.** Decodes JPEG, PNG, WebP, GIF (first frame), BMP and ICO; HEIF/HEIC and AVIF where the platform's
  Skia build has those codecs (otherwise `imaging.unsupported_format`; the signature is still recognised). Encodes
  JPEG, PNG and WebP (lossy, or lossless with `ImageEncodeOptions.Lossless`). JPEG output flattens transparency onto
  `ImageEncodeOptions.Background` (white).
- **Probe without decoding.** `ImageInfo.Probe(stream)` reads format, size, orientation, alpha and frame count from
  the header; `ImageFormatExtensions.Detect` sniffs the signature.
- **Editing pipeline.** `doc.Edit()` records steps; `Apply()` returns a new `ImageDocument`, `Encode(...)` returns an
  `EncodedImage`. Steps:
  - Geometry: `AutoOrient`, `Resize` (`Fit`, `Fill` (cover, with anchor), `Stretch`, `Pad`, `Max`), `Crop`,
    `SmartCrop` (edge/saturation energy), `Rotate` (exact for right angles, any angle with background), `FlipHorizontal`, `FlipVertical`.
  - Colour: `Brightness`, `Contrast`, `Saturation`, `Gamma`, `Hue`, `Exposure`, `Grayscale`, `Sepia`, `Invert`, `Tint`
    — consecutive colour steps are fused into one pass.
  - Filters: `GaussianBlur`, `Sharpen` (unsharp mask), `Pixelate`.
  - Frames and drawing: `RoundCorners`, `Border`, `Pad`, `Watermark` (image or text), `DrawText`, `DrawRectangle`,
    `DrawEllipse`, `DrawLine`, `Composite` (17 blend modes, opacity).
  - `StripMetadata`.
  Each step validates its arguments when recorded; the first invalid one is returned by `Apply`/`Encode` as
  `imaging.invalid_argument`. The source document is never modified, and a pipeline can be run again.
- **Metadata.** `doc.Metadata` / `ImageMetadataReader.Read(...)` give an `ImageMetadata` record: camera make and model,
  lens, software, artist, copyright, date taken (+ UTC offset), GPS (latitude, longitude, altitude), orientation,
  ISO, exposure time, f-number, focal length (+ 35 mm), flash, EXIF pixel size. It comes from a small bounds-checked
  managed TIFF/EXIF reader for JPEG APP1, PNG `eXIf` and WebP `EXIF` chunks; malformed EXIF yields fewer fields, never
  a failure.
- **Metadata on encode.** By default EXIF is dropped (`ImagingSettings.StripMetadata`). `KeepMetadata = true` writes
  the original EXIF back (orientation reset to 1 after `AutoOrient`), GPS included unless `RemoveGps = true`; `KeepIccProfile = true` writes the original ICC
  profile back byte for byte and keeps the pixels in that colour space. Without it, pixels are converted to sRGB.
- **Analysis** (`CL.Imaging.Analysis`): `ImageAnalysis.AverageColor`, `DominantColor`, `Palette(n)` (median cut +
  k-means, deterministic), `HasAlpha`, `IsMostlyTransparent`; `BlurHash.Encode/Decode`; `PerceptualHash.DHash/PHash`
  with `HammingDistance`, `Similarity`, `IsNearDuplicate`; `SmartCrop.FindRegion`; `ImageCompare.Difference`
  (similarity, differing pixels, optional diff image).
- **Safety.** Pixel limit from the header (default 40 MP), byte limit (default 100 MB), hard output cap of
  16384² pixels, `CancellationToken` on every async method and between pipeline steps, all Skia objects disposed,
  encoder output in `ArrayPool` buffers.

## Configuration (optional)

```csharp
await Libraries.LoadAsync<CL.Imaging.ImagingLibrary>();
```

`config.imaging.json`:

| Setting | Default | Meaning |
|---|---|---|
| `DefaultMaxPixels` | `40000000` | Largest image decoded (0 = no limit) |
| `DefaultMaxBytes` | `104857600` | Largest encoded input read (0 = no limit) |
| `DefaultQuality` | `85` | Lossy quality when a call doesn't give one |
| `DefaultFormat` | `Webp` | Output format when a call doesn't give one |
| `StripMetadataByDefault` | `true` | Drop EXIF unless a call keeps it |

Without the library, set `ImagingDefaults.Current = ImagingSettings.BuiltIn with { ... }` yourself.

## Errors

| Code | Kind | When |
|---|---|---|
| `imaging.too_large` | Validation | Over the pixel or byte limit, or beyond the output cap |
| `imaging.unsupported_format` | Validation | HEIF/AVIF without a platform codec; encoding to a format other than JPEG/PNG/WebP |
| `imaging.decode_failed` | Validation | Not an image, corrupt or truncated |
| `imaging.invalid_argument` | Validation | A step's argument out of range, crop outside the image, bad BlurHash |
| `imaging.encode_failed` | Internal | The encoder produced nothing |
| `imaging.cancelled` | Unavailable | The `CancellationToken` was cancelled |
| `imaging.file_not_found` | NotFound | `LoadFile`/`ProbeFile` on a missing path |
| `imaging.io_failed` | Internal | Stream or file I/O failed |
| `imaging.processing_failed` | Internal | Unexpected engine failure |

## Limitations

- **No animated output.** Animated GIF/WebP input decodes its first frame; animated encoding is out of scope.
- **HEIF/AVIF** decode only where the platform's Skia build includes those codecs; CL.Imaging does not bundle
  decoders. Metadata for HEIF/AVIF comes from Skia (orientation only); the EXIF reader covers JPEG, PNG and WebP.
- **Text** uses the platform's fonts. Minimal Linux containers (NoDependencies assets) may have none, so a family name
  can draw nothing; ship a font and use `ImageFont.FromFile` / `FromBytes`.
- **EXIF on re-encode** is written back as it was (only the orientation tag is updated); tags such as the EXIF pixel
  size or thumbnails are not rewritten after resizing. XMP and IPTC are not carried over.
- An `ImageDocument` is not meant for concurrent mutation; reading the same document from several pipelines at once
  is fine because pipelines never write to their source.
