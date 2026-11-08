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

- Prevent pooled-buffer reference resurrection during concurrent final release and acquisition; cover it with a race regression.

- **`EncodedImage.AsStream()` owns its bytes**. The stream shared the pooled array, so one kept past
  the image's `using` block (handed to a response, a background writer) read whatever the pool had since given that
  array to, possibly another request's image. The pooled buffer is now reference-counted: each stream holds a
  reference, the array goes back to the pool when the image and every stream are disposed, and a stream stays valid
  after the image is disposed. `Span`, `Memory`, `ToArray`, `ToBase64` and `AsStream` throw `ObjectDisposedException`
  after `Dispose` as before; `Dispose` is idempotent.
- **`ImageInfo.Probe` refuses a header that claims a side over 32 767 pixels** with `imaging.too_large`, decided from
  the header alone (nothing is allocated for pixels, also on a non-seekable stream). The decoder refused such images
  already; `Probe` now says so up front instead of reporting a size that cannot be loaded.
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

### Documentation

- Configuration tables in the README and the docs use the camelCase keys the CodeLogic loader reads
  (`defaultMaxPixels`, `defaultFormat: "webp"`, ...); the PascalCase keys shown before were ignored.
- API tables match the source: `Create(width, height, background)`, `DrawEllipse(bounds, …)`,
  `Composite(…, mode, …)`, `SmartCrop(w, h, quality)`; added `GetPixel`, `Clone`, `ImageLoadOptions.ReadMetadata`,
  `ToDataUri(bytes)`, `FitWithin`, the watermark defaults and an exhaustive `switch` over a result.
- The `AsStream()` race note moved from the metadata page to the encoded-output section. The README's relative
  link to the guide, which is broken on nuget.org, is replaced by a link to the docs site.
