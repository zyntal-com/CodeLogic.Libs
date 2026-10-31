# Editing

`doc.Edit()` returns an `ImagePipeline`. Its methods only record a step and check the arguments; nothing runs until
`Apply()` (a new `ImageDocument`) or `Encode(...)` (an `EncodedImage`). The first invalid argument is what
`Apply`/`Encode` return, as `imaging.invalid_argument`; no method throws on bad input. The source document is never
changed and a pipeline can be run more than once.

```csharp
Result<EncodedImage> result = doc.Edit()
    .AutoOrient()
    .Resize(1200, 800, ResizeMode.Fit, ResampleQuality.High)
    .Contrast(0.1).Saturation(-0.2).Hue(10)
    .Sharpen(0.5)
    .Border(4, Color.White)
    .Watermark(new WatermarkText("© Example"), Anchor.BottomRight, opacity: 0.6, margin: 16)
    .Encode(ImageFormat.Webp, quality: 82, ct);
```

**How it runs.** Steps run in order on one working bitmap. Consecutive colour steps are composed into a single
Skia colour filter and applied in one pass; drawing steps draw in place; geometry and filter steps produce one new
bitmap each. The `CancellationToken` is checked between steps (`imaging.cancelled`).

## Geometry

| Step | Notes |
|---|---|
| `AutoOrient()` | Applies the EXIF orientation (all 8 values). |
| `Resize(w, h, mode, quality, background, anchor)` | `Fit` (scale to fit, up or down), `Max` (fit, never up), `Fill` (cover and crop at `anchor`), `Stretch`, `Pad` (fit, then pad with `background` at `anchor`). `0` for one side keeps the aspect ratio (not with `Fill`/`Pad`). |
| `Crop(PixelRect)` | Must lie inside the image when the step runs. |
| `SmartCrop(w, h)` | Crops the most detailed region with that aspect ratio, then scales to exactly `w`×`h`. |
| `Rotate(degrees, background)` | Clockwise. Right angles are exact; other angles grow the canvas and fill corners with `background` (default transparent). |
| `FlipHorizontal()`, `FlipVertical()` | Mirror. |

`ResampleQuality`: `Nearest`, `Medium` (bilinear + mipmaps), `High` (bicubic Mitchell; reductions below one half are
pre-halved to avoid aliasing).

## Colour

| Step | Range | Effect |
|---|---|---|
| `Brightness(a)` | −1…1 | Adds `a` to each channel. |
| `Contrast(a)` | −1…1 | Scales the distance from mid grey by `1 + a`. |
| `Saturation(a)` | −1…1 | −1 is grey, 1 doubles saturation. |
| `Gamma(g)` | 0.1…10 | `out = in^(1/g)`; above 1 brightens mid-tones. |
| `Hue(degrees)` | −360…360 | Rotates hues. |
| `Exposure(stops)` | −10…10 | Multiplies light by `2^stops`. |
| `Grayscale(amount)`, `Sepia(amount)` | 0…1 | Partial or full. |
| `Invert()` | | Colours only; alpha kept. |
| `Tint(color, amount)` | 0…1 | Luminance coloured with `color`, mixed in by `amount`. |

## Filters

| Step | Notes |
|---|---|
| `GaussianBlur(sigma)` | Edges clamped (no dark border). |
| `Sharpen(amount, radius)` | Unsharp mask: `src + amount × (src − blur(src, radius))`. |
| `Pixelate(blockSize)` | Exact block averages. |

## Frames and drawing

| Step | Notes |
|---|---|
| `RoundCorners(radius)` | Anti-aliased transparent corners (flattened onto the background for JPEG). |
| `Border(thickness, color)` | Painted inside the edges; size unchanged. |
| `Pad(amount, color)` / `Pad(l, t, r, b, color)` | Grows the canvas. |
| `Watermark(watermark, anchor, opacity, margin)` | `WatermarkImage(doc, RelativeWidth)` (fraction of the image width, or null for natural size) or `WatermarkText(text, font, color)`. |
| `DrawText(text, font, position, color, align)` | `position` is the top of the line box. |
| `DrawRectangle(rect, color, strokeWidth, cornerRadius)`, `DrawEllipse(rect, color, strokeWidth)`, `DrawLine(from, to, color, width)` | `strokeWidth` 0 fills. |
| `Composite(overlay, position, BlendMode, opacity)` | 17 blend modes (`Normal`, `Multiply`, `Screen`, `Overlay`, …). |

`ImageFont` is a record: `Family`, `Size`, `Bold`, `Italic`, or font file bytes via `ImageFont.FromFile` /
`FromBytes`. Servers without fonts (minimal Linux containers) draw nothing for a family name; ship a font file.

## Encoding

```csharp
using var webp = doc.Edit().Encode(new ImageEncodeOptions
{
    Format = ImageFormat.Webp,
    Lossless = true,
    KeepIccProfile = true,
    KeepMetadata = false,
}).Value!;
```

| Option | Default | Meaning |
|---|---|---|
| `Format` | `ImagingDefaults` (WebP) | `Jpeg`, `Png` or `Webp`; others give `imaging.unsupported_format`. |
| `Quality` | `ImagingDefaults` (85) | 1–100 for JPEG and lossy WebP (effort for lossless WebP). |
| `Lossless` | `false` | Lossless WebP. |
| `KeepIccProfile` | `false` | Write the source ICC profile back unchanged; otherwise pixels are converted to sRGB. |
| `KeepMetadata` | `!StripMetadataByDefault` | Write the source EXIF back. |
| `Background` | white | JPEG flattens transparency onto it. |
