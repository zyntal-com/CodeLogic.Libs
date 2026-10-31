using CL.Imaging.Internal;

namespace CL.Imaging.Analysis;

/// <summary>A palette entry: a colour and the share of (visible) pixels closest to it.</summary>
/// <param name="Color">The colour.</param>
/// <param name="Share">Fraction of the counted pixels, 0–1.</param>
public readonly record struct PaletteColor(Color Color, double Share);

/// <summary>Colour statistics and transparency checks.</summary>
/// <remarks>
/// Colour statistics run on a reduced copy (at most 128 pixels on the longest side for palettes, 256 for the average),
/// so they cost about the same for any image size. Fully transparent pixels are ignored by the palette.
/// </remarks>
public static class ImageAnalysis
{
    /// <summary>The alpha-weighted average colour; its alpha is the average alpha.</summary>
    /// <param name="image">The image.</param>
    public static Color AverageColor(ImageDocument image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (px, w, h) = SkiaOps.Sample(image.Bitmap, 256);
        double r = 0, g = 0, b = 0, a = 0;
        for (var i = 0; i < w * h; i++)
        {
            double alpha = px[i * 4 + 3];
            r += px[i * 4] * alpha;
            g += px[i * 4 + 1] * alpha;
            b += px[i * 4 + 2] * alpha;
            a += alpha;
        }
        if (a == 0) return Color.Transparent;
        return new Color(ToByte(r / a), ToByte(g / a), ToByte(b / a), ToByte(a / (w * h)));
    }

    /// <summary>The most common colour cluster (the first entry of <see cref="Palette"/>).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The dominant colour; <see cref="Color.Transparent"/> for a fully transparent image.</returns>
    public static Color DominantColor(ImageDocument image)
    {
        var palette = Palette(image, 5);
        return palette.Count == 0 ? Color.Transparent : palette[0].Color;
    }

    /// <summary>
    /// The <paramref name="count"/> main colours, largest share first: median-cut clusters refined with k-means in RGB.
    /// Deterministic for a given image.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="count">Number of colours, 1–64. Fewer are returned when the image has fewer distinct colours.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 1–64.</exception>
    public static IReadOnlyList<PaletteColor> Palette(ImageDocument image, int count = 5)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        var (px, w, h) = SkiaOps.Sample(image.Bitmap, 128);
        var points = new List<(int R, int G, int B)>(w * h);
        for (var i = 0; i < w * h; i++)
            if (px[i * 4 + 3] >= 128) points.Add((px[i * 4], px[i * 4 + 1], px[i * 4 + 2]));
        if (points.Count == 0) return [];

        var centroids = MedianCut(points, count);
        var assignment = new int[points.Count];
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var changed = false;
            for (var p = 0; p < points.Count; p++)
            {
                var nearest = Nearest(centroids, points[p]);
                if (nearest != assignment[p] || iteration == 0) { changed |= nearest != assignment[p]; assignment[p] = nearest; }
            }
            var sums = new double[centroids.Count, 4];
            for (var p = 0; p < points.Count; p++)
            {
                var c = assignment[p];
                sums[c, 0] += points[p].R; sums[c, 1] += points[p].G; sums[c, 2] += points[p].B; sums[c, 3]++;
            }
            for (var c = 0; c < centroids.Count; c++)
                if (sums[c, 3] > 0) centroids[c] = (sums[c, 0] / sums[c, 3], sums[c, 1] / sums[c, 3], sums[c, 2] / sums[c, 3]);
            if (!changed && iteration > 0) break;
        }

        var counts = new int[centroids.Count];
        foreach (var a in assignment) counts[a]++;
        var result = new List<PaletteColor>(centroids.Count);
        for (var c = 0; c < centroids.Count; c++)
        {
            if (counts[c] == 0) continue;
            var color = new Color(ToByte(centroids[c].R), ToByte(centroids[c].G), ToByte(centroids[c].B));
            var existing = result.FindIndex(e => e.Color == color);
            if (existing >= 0) result[existing] = result[existing] with { Share = result[existing].Share + (double)counts[c] / points.Count };
            else result.Add(new PaletteColor(color, (double)counts[c] / points.Count));
        }
        result.Sort((x, y) => y.Share != x.Share ? y.Share.CompareTo(x.Share) : x.Color.Luminance.CompareTo(y.Color.Luminance));
        return result;
    }

    /// <summary>Whether any pixel is not fully opaque.</summary>
    /// <param name="image">The image.</param>
    public static bool HasAlpha(ImageDocument image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return !Encoder.IsOpaque(image.Bitmap);
    }

    /// <summary>Whether at least <paramref name="threshold"/> of the pixels are (nearly) transparent.</summary>
    /// <param name="image">The image.</param>
    /// <param name="threshold">Fraction of pixels, 0–1. Default 0.9.</param>
    /// <param name="alphaCutoff">Alpha below which a pixel counts as transparent, 1–255. Default 16.</param>
    public static bool IsMostlyTransparent(ImageDocument image, double threshold = 0.9, byte alphaCutoff = 16)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 1);
        var span = image.Bitmap.GetPixelSpan();
        long transparent = 0, total = span.Length / 4;
        for (var i = 3; i < span.Length; i += 4)
            if (span[i] < alphaCutoff) transparent++;
        return total > 0 && (double)transparent / total >= threshold;
    }

    private static List<(double R, double G, double B)> MedianCut(List<(int R, int G, int B)> points, int count)
    {
        var boxes = new List<List<(int R, int G, int B)>> { points };
        while (boxes.Count < count)
        {
            // Split the box with the widest channel range (weighted by its size).
            int bestBox = -1, bestChannel = 0;
            double bestScore = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Count < 2) continue;
                for (var ch = 0; ch < 3; ch++)
                {
                    int min = 255, max = 0;
                    foreach (var p in box)
                    {
                        var v = Channel(p, ch);
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    var score = (double)(max - min) * Math.Sqrt(box.Count);
                    if (score > bestScore) { bestScore = score; bestBox = i; bestChannel = ch; }
                }
            }
            if (bestBox < 0) break;
            var split = boxes[bestBox];
            var channel = bestChannel;
            split.Sort((x, y) => Channel(x, channel).CompareTo(Channel(y, channel)));
            var mid = split.Count / 2;
            boxes[bestBox] = split.GetRange(0, mid);
            boxes.Add(split.GetRange(mid, split.Count - mid));
        }
        return boxes.ConvertAll(box => (box.Average(p => (double)p.R), box.Average(p => (double)p.G), box.Average(p => (double)p.B)));
    }

    private static int Channel((int R, int G, int B) p, int channel) => channel switch { 0 => p.R, 1 => p.G, _ => p.B };

    private static int Nearest(List<(double R, double G, double B)> centroids, (int R, int G, int B) p)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var c = 0; c < centroids.Count; c++)
        {
            double dr = centroids[c].R - p.R, dg = centroids[c].G - p.G, db = centroids[c].B - p.B;
            var d = dr * dr + dg * dg + db * db;
            if (d < bestDistance) { bestDistance = d; best = c; }
        }
        return best;
    }

    internal static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
