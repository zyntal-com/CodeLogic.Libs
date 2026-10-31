# Metadata

## Probing

`ImageInfo.Probe` reads the header only — nothing is decoded — so it is cheap even for huge files:

```csharp
Result<ImageInfo> info = ImageInfo.Probe(stream);   // also Probe(bytes), ProbeFile(path)
// info.Value: Format, Width, Height, Orientation, DisplayWidth, DisplayHeight, HasAlphaChannel, FrameCount, MimeType
```

A seekable stream is left where it was; non-seekable streams work too. `ImageFormatExtensions.Detect(bytes)` sniffs
the format from the first 32 bytes (including HEIF/AVIF brands).

## EXIF

`doc.Metadata` (filled on load) and `ImageMetadataReader.Read(bytes)` / `ReadAsync(stream)` return an
`ImageMetadata` record:

| Field | EXIF source |
|---|---|
| `CameraMake`, `CameraModel`, `Software`, `Artist`, `Copyright` | IFD0 Make, Model, Software, Artist, Copyright |
| `LensMake`, `LensModel` | LensMake, LensModel |
| `DateTaken`, `DateTakenOffset` | DateTimeOriginal (else DateTime), OffsetTimeOriginal |
| `Gps` (`Latitude`, `Longitude`, `Altitude`) | GPS IFD, signed by the N/S, E/W and altitude references |
| `Orientation` | Orientation (1–8) |
| `Iso`, `ExposureTime` (+ `ExposureTimeText`), `FNumber`, `FocalLength`, `FocalLength35mm`, `FlashFired` | Exif IFD |
| `ExifWidth`, `ExifHeight` | PixelXDimension, PixelYDimension |
| `Width`, `Height`, `Format`, `HasExif`, `HasIccProfile` | Container |

EXIF is read by a small, bounds-checked managed TIFF reader from JPEG APP1, PNG `eXIf` and WebP `EXIF` chunks, in
both byte orders. Malformed blocks yield fewer fields and never fail the image. For HEIF/AVIF only the orientation
(from Skia) is available.

## Orientation

Pixels are kept as stored: `doc.Orientation` says how they must be turned, `DisplayWidth`/`DisplayHeight` give the
upright size, and the pipeline's `AutoOrient()` applies it. `ImageTools.ProcessUpload`, `GenerateVariants` and
`Thumbnail` always orient.

## Stripping and keeping

Encoding writes only pixels unless asked otherwise:

- **EXIF** is dropped when `ImagingSettings.StripMetadata` is true (the default). `ImageEncodeOptions.KeepMetadata =
  true` writes the source EXIF back as JPEG APP1, PNG `eXIf` or WebP `EXIF` (converting the WebP to the extended
  format); after `AutoOrient()` its orientation tag is reset to 1. Other tags are not rewritten. The GPS position is
  kept too, unless `RemoveGps = true`, which zeroes the GPS values and empties the GPS IFD while keeping every other tag.
- **ICC profile**: by default pixels are converted to sRGB and no profile is written. `KeepIccProfile = true` keeps
  the pixels in the source colour space and writes the original profile back byte for byte (JPEG APP2 chunks, PNG
  `iCCP`, WebP `ICCP`).
- `StripMetadata()` in the pipeline overrides both options.

`ImageTools.ProcessUpload` always strips, which removes GPS positions from user uploads.
