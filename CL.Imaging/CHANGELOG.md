# Changelog

## 5.0 (unreleased)

### Breaking

- **New package, moved from CodeLogic.Common.** Imaging lived in CL.Common as `CL.Common.Imaging.CLU_Imaging`
  (with `ImageProcessOptions`, `ProcessedImage` and `ImageFormat`). It is now CodeLogic.Imaging, namespace
  `CL.Imaging`, with a new API; CL.Common no longer references SkiaSharp. `MIGRATION.md` maps every old method.
  `ImageFormat` gained members, so its numeric values changed; `imaging.invalid_options` is now
  `imaging.invalid_argument`.
- Targets **.NET 11** and requires **CodeLogic 5.0** (`Result<T>` / `Error` unions).

### Added

- `ImageDocument`: load from streams, bytes and files (sync and async) with a header pixel limit and a byte limit;
  `Create`, `FromSkiaBitmap`, `GetPixel`, `Clone`. Decodes JPEG, PNG, WebP, GIF (first frame), BMP, ICO, and HEIF/AVIF
  where the platform's Skia has the codec.
- `ImagePipeline` (`doc.Edit()`): a lazy fluent pipeline with `AutoOrient`, `Resize` (Fit, Fill, Stretch, Pad, Max;
  anchor; resample quality), `Crop`, `SmartCrop`, `Rotate`, `FlipHorizontal`/`FlipVertical`, `Brightness`,
  `Contrast`, `Saturation`, `Gamma`, `Hue`, `Exposure`, `Grayscale`, `Sepia`, `Invert`, `Tint` (fused into one pass),
  `GaussianBlur`, `Sharpen`, `Pixelate`, `RoundCorners`, `Border`, `Pad`, `Watermark` (image or text), `DrawText`,
  `DrawRectangle`, `DrawEllipse`, `DrawLine`, `Composite` (blend modes, opacity) and `StripMetadata`. Argument errors
  surface from `Apply()` / `Encode()` as `imaging.invalid_argument`.
- Encoding to JPEG, PNG and WebP (lossy and lossless) into pooled buffers (`EncodedImage`, with `ToArray`,
  `AsStream`, `WriteToAsync`, `SaveAsync`, `ToBase64`, `ToDataUri`); `ImageEncodeOptions` with `KeepIccProfile`
  (original profile written back byte for byte), `KeepMetadata` (original EXIF written back, orientation updated), `RemoveGps` (drop only the GPS position from
  kept EXIF) and
  `Background` for JPEG.
- `ImageInfo.Probe` (format, size, orientation, alpha, frames from the header only) and
  `ImageFormatExtensions` (`Detect`, `MimeType`, `FileExtension`, `CanEncode`).
- `ImageMetadata` / `ImageMetadataReader`: camera, lens, software, artist, copyright, date taken and offset, GPS,
  orientation, ISO, exposure, f-number, focal length, flash, read by a managed EXIF/TIFF reader from JPEG, PNG and WebP.
- Analysis: `ImageAnalysis.AverageColor`, `DominantColor`, `Palette`, `HasAlpha`, `IsMostlyTransparent`;
  `BlurHash.Encode`/`Decode`; `PerceptualHash.DHash`/`PHash` with `HammingDistance`, `Similarity`,
  `IsNearDuplicate`; `SmartCrop.FindRegion`; `ImageCompare.Difference` with an optional diff image.
- `ImageTools`: `ProcessUpload(Async)` (the former `ProcessImage`), `GenerateVariants(Async)` with `ToSrcSet`,
  `Thumbnail(Async)`, `ToDataUri`, `FitWithin`.
- `ImagingErrors` with `imaging.*` codes, `ImagingDefaults` / `ImagingSettings`, and an optional `ImagingLibrary`
  whose `imaging` config section (`DefaultMaxPixels`, `DefaultMaxBytes`, `DefaultQuality`, `DefaultFormat`,
  `StripMetadataByDefault`) sets the defaults, live on config changes.

### Changed

- Package versions are pinned (SkiaSharp and SkiaSharp.NativeAssets.Linux.NoDependencies 4.154.0) instead of floating (`4.*`).
- **Kept EXIF no longer carries the source thumbnail or MakerNote.** `KeepMetadata = true` zeroes the IFD1 thumbnail
  (and unlinks IFD1) and the MakerNote before writing the EXIF back. Both describe the source pixels (MakerNotes can
  embed previews and a vendor GPS copy), so a blurred face stayed visible in the thumbnail and coordinates survived
  `RemoveGps`. Other tags are still written back as they were.
- `BlurHash.IsValid`/`Decode` reject size flags above 80 (ten component rows; the spec allows 1-9 per axis), and
  `Decode` caps the output at `BlurHash.MaxDecodePixels` (1 048 576 pixels, new constant) and evaluates the
  separable cosine basis from tables, so a hash from a request cannot buy seconds of CPU per call.
- Reading a stream with no byte limit (`MaxBytes = 0`) rents at most 16 MiB up front instead of whatever the stream
  reports as its length; larger images grow the buffer as they are read.

