# Analysis

The analysis types live in `CL.Imaging.Analysis`. Colour statistics run on a reduced copy, so they cost about the
same for any image size, and every result is deterministic.

## Colours

```csharp
Color average  = ImageAnalysis.AverageColor(doc);     // alpha-weighted
Color dominant = ImageAnalysis.DominantColor(doc);
IReadOnlyList<PaletteColor> palette = ImageAnalysis.Palette(doc, 5);   // (Color, Share), largest first
bool alpha  = ImageAnalysis.HasAlpha(doc);
bool hollow = ImageAnalysis.IsMostlyTransparent(doc, threshold: 0.9);
```

The palette uses median cut to seed clusters and k-means to refine them; transparent pixels are ignored. `Color` is
an 8-bit sRGB record with `ToHex()` / `TryParseHex`.

## BlurHash

```csharp
string hash = BlurHash.Encode(doc, xComponents: 4, yComponents: 3).Value!;    // e.g. "LEHV6nWB2yk8pyo0adR*.7kCMdnj"
using var placeholder = BlurHash.Decode(hash, 32, 32).Value!;
```

Compatible with the reference implementations ([blurha.sh](https://blurha.sh)). `BlurHash.IsValid(text)` checks a hash.

## Perceptual hashes

```csharp
ulong a = PerceptualHash.PHash(doc1), b = PerceptualHash.PHash(doc2);
bool duplicate = PerceptualHash.IsNearDuplicate(a, b, maxDistance: 10);
int distance = PerceptualHash.HammingDistance(a, b);
```

- `DHash`: brightness gradients on a 9×8 thumbnail. Fast; survives resizing and recompression.
- `PHash`: the 8×8 lowest DCT frequencies of a 32×32 thumbnail against their median. Also survives small brightness
  and contrast changes.

Both are 64-bit; store them as `ulong` and compare by Hamming distance. Mirrored or different images are far apart.

## Smart crop

`SmartCrop.FindRegion(doc, width, height)` returns the largest rectangle with that aspect ratio that holds the most
detail (luminance edges plus colour saturation, measured on a reduced copy, with a slight preference for the centre).
The pipeline's `SmartCrop(w, h)` and `ImageTools.Thumbnail(..., smartCrop: true)` use it.

## Comparison

```csharp
using var cmp = ImageCompare.Difference(expected, actual, new ImageCompareOptions { Tolerance = 8, CreateDiffImage = true }).Value!;
// cmp.Similarity (1 = identical), cmp.MeanError, cmp.DifferentPixels, cmp.DifferentRatio, cmp.DiffImage
```

A different-sized second image is resampled to the first one's size unless `ResizeToMatch = false`
(`imaging.invalid_argument`). The diff image shows the reference faded to grey with differing pixels in
`Highlight` (red).
