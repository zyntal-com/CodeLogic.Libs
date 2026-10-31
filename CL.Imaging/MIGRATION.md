# Migrating from CL.Common imaging (`CLU_Imaging`)

Imaging moved out of CodeLogic.Common into its own package, CodeLogic.Imaging, so CL.Common no longer depends on
SkiaSharp. `CL.Common.Imaging.CLU_Imaging`, `ImageProcessOptions` and `ProcessedImage` are gone.

```diff
- dotnet add package CodeLogic.Common      # imaging came with it
+ dotnet add package CodeLogic.Imaging
```

```diff
- using CL.Common.Imaging;
+ using CL.Imaging;
```

## Behaviour changes

- **Encoded output is an `EncodedImage`**, not a `MemoryStream` or `byte[]`. It holds a pooled buffer: dispose it.
  Use `ToArray()`, `AsStream()`, `WriteToAsync(...)` or `SaveAsync(path)` to get the bytes out.
- **Error codes** are now the `ImagingErrors` constants. `imaging.too_large` and `imaging.decode_failed` keep their
  names; `imaging.invalid_options` is now `imaging.invalid_argument`; the per-method failure codes
  (`imaging.convert_failed`, `imaging.resize_stream_failed`, `imaging.thumbnail_failed`, `imaging.process_failed`,
  `imaging.base64_failed`, `imaging.dimensions_failed`, ...) became `imaging.processing_failed` /
  `imaging.io_failed`, and `imaging.encode_failed` stays.
- **`ImageFormat`** gained `Unknown`, `Gif`, `Bmp`, `Ico`, `Heif` and `Avif` (decode-only), so the numeric values
  changed: `Jpeg = 1`, `Png = 2`, `Webp = 3`. Persisted numbers must be mapped; names are unchanged.
- **Defaults** come from `ImagingDefaults` (WebP, quality 85, 40 MP, 100 MB) instead of being fixed per method.
  `ConvertImage` used quality 90 and `ResizeImage` PNG at 100; pass those explicitly to keep the old output.
- **The byte limit is new.** Inputs over 100 MB are refused (`imaging.too_large`) unless you raise
  `MaxBytes` / `DefaultMaxBytes`.

## Old → new

| Old (`CLU_Imaging.…`) | New |
|---|---|
| `ProcessImage(stream, new ImageProcessOptions { … })` | `ImageTools.ProcessUpload(stream, new UploadOptions { … })` (same fields: `Format`, `Quality`, `MaxWidth`, `MaxHeight`, `MaxPixels`, `ApplyOrientation`, plus `MaxBytes`); returns `Result<EncodedImage>` (`Width`, `Height`, `Format`, bytes) instead of `ProcessedImage` |
| `ImageProcessOptions` | `UploadOptions` |
| `ProcessedImage(Data, Width, Height, Format)` | `EncodedImage` (`ToArray()`, `Width`, `Height`, `Format`) |
| `DefaultMaxPixels` | `ImagingSettings.BuiltIn.MaxPixels` / `ImagingDefaults.Current.MaxPixels` |
| `FitWithin(w, h, maxW, maxH)` | `ImageTools.FitWithin(w, h, maxW, maxH)` |
| `ConvertImage(stream, format)` | `ImageDocument.Load(stream)` then `doc.Edit().AutoOrient().StripMetadata().Encode(format, 90)` — or `ImageTools.ProcessUpload(stream, new UploadOptions { Format = format, Quality = 90 })` |
| `ResizeImage(stream, w, h, allowCrop: true)` | `doc.Edit().AutoOrient().Resize(w, h, ResizeMode.Fill).Encode(ImageFormat.Png)` |
| `ResizeImage(stream, w, h, allowCrop: false)` | `doc.Edit().AutoOrient().Resize(w, h, ResizeMode.Fit).Encode(ImageFormat.Png)` (`ResizeMode.Max` never scales up) |
| `ResizeImage(stream, w, h, allowCrop, format, quality)` | `doc.Edit().AutoOrient().Resize(w, h, allowCrop ? ResizeMode.Fill : ResizeMode.Fit).Encode(format, quality)` |
| `ResizeImage(inputPath, outputPath, w, h, maintainAspectRatio)` | `ImageDocument.LoadFileAsync(inputPath)` → `Edit().AutoOrient().Resize(w, h, maintainAspectRatio ? ResizeMode.Fit : ResizeMode.Fill).Encode(…)` → `SaveAsync(outputPath)` |
| `ConvertImageFormat(inputPath, outputPath, format)` | `ImageDocument.LoadFileAsync(inputPath)` → `Edit().AutoOrient().Encode(format)` → `SaveAsync(outputPath)` |
| `CreateThumbnail(inputPath, outputPath, size)` | `ImageTools.Thumbnail(doc, size, size)` (centre crop; `smartCrop: true` for the most detailed region) → `SaveAsync(outputPath)` |
| `ImageToBase64(path)` | `Convert.ToBase64String(await File.ReadAllBytesAsync(path))`, or `encoded.ToBase64()` / `ImageTools.ToDataUri(bytes)` |
| `GetImageDimensions(path)` | `ImageInfo.ProbeFile(path)` → `Width`, `Height` (stored size; `DisplayWidth`/`DisplayHeight` after orientation) |
| `IsValidSize(stream, maxW, maxH)` | `ImageInfo.Probe(stream)` → `info.Width <= maxW && info.Height <= maxH` |
| `IsValidAspectRatio(stream)` | `ImageInfo.Probe(stream).IsSuccess` (a probed image always has a non-zero size) |

## Example

Before:

```csharp
var result = CLU_Imaging.ProcessImage(upload, new ImageProcessOptions { MaxWidth = 1024, MaxHeight = 1024 });
if (result.IsSuccess) await File.WriteAllBytesAsync(path, result.Value!.Data);
```

After:

```csharp
var result = ImageTools.ProcessUpload(upload, new UploadOptions { MaxWidth = 1024, MaxHeight = 1024 });
if (result.IsSuccess)
{
    using var image = result.Value!;
    await image.SaveAsync(path);
}
```
