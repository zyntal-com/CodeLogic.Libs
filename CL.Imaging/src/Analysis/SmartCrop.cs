using CL.Imaging.Internal;
using SkiaSharp;

namespace CL.Imaging.Analysis;

/// <summary>
/// Finds the most detailed region of an image for a target aspect ratio. Detail is measured on a reduced copy as
/// edge energy (luminance gradient magnitude) plus local colour saturation; the window with the most energy wins, with
/// a slight preference for the centre when regions score alike.
/// </summary>
public static class SmartCrop
{
    private const int AnalysisSide = 160;

    /// <summary>The region of <paramref name="image"/> (in its pixels) with the aspect ratio of <paramref name="targetWidth"/>×<paramref name="targetHeight"/> that holds the most detail.</summary>
    /// <param name="image">The image.</param>
    /// <param name="targetWidth">Target width (only the ratio matters).</param>
    /// <param name="targetHeight">Target height (only the ratio matters).</param>
    /// <returns>The crop rectangle, as large as the aspect ratio allows.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A target dimension is not positive.</exception>
    public static PixelRect FindRegion(ImageDocument image, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(image);
        return FindRegion(image.Bitmap, targetWidth, targetHeight);
    }

    internal static PixelRect FindRegion(SKBitmap source, int targetWidth, int targetHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        double aspect = (double)targetWidth / targetHeight;
        int W = source.Width, H = source.Height;
        int cw, ch;
        if ((double)W / H > aspect) { ch = H; cw = Math.Clamp((int)Math.Round(H * aspect), 1, W); }
        else { cw = W; ch = Math.Clamp((int)Math.Round(W / aspect), 1, H); }
        if (cw == W && ch == H) return new PixelRect(0, 0, W, H);

        var (pixels, sw, sh) = SkiaOps.Sample(source, AnalysisSide);
        var energy = Energy(pixels, sw, sh);

        // Integral image of the energy map.
        var integral = new double[(sw + 1) * (sh + 1)];
        for (var y = 0; y < sh; y++)
        {
            double rowSum = 0;
            for (var x = 0; x < sw; x++)
            {
                rowSum += energy[y * sw + x];
                integral[(y + 1) * (sw + 1) + x + 1] = integral[y * (sw + 1) + x + 1] + rowSum;
            }
        }

        double scaleX = (double)sw / W, scaleY = (double)sh / H;
        var ww = Math.Clamp((int)Math.Round(cw * scaleX), 1, sw);
        var wh = Math.Clamp((int)Math.Round(ch * scaleY), 1, sh);
        double total = integral[^1];
        double best = double.MinValue;
        int bestX = (sw - ww) / 2, bestY = (sh - wh) / 2;
        double cx = (sw - ww) / 2.0, cy = (sh - wh) / 2.0;
        var maxDistance = Math.Max(1, Math.Sqrt(cx * cx + cy * cy));
        for (var y = 0; y + wh <= sh; y++)
        {
            for (var x = 0; x + ww <= sw; x++)
            {
                var sum = integral[(y + wh) * (sw + 1) + x + ww] - integral[y * (sw + 1) + x + ww]
                        - integral[(y + wh) * (sw + 1) + x] + integral[y * (sw + 1) + x];
                // Centre bias worth at most 2% of the total energy, so it only breaks near-ties.
                var distance = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxDistance;
                var score = sum - distance * 0.02 * total;
                if (score > best) { best = score; bestX = x; bestY = y; }
            }
        }

        var rx = Math.Clamp((int)Math.Round(bestX / scaleX), 0, W - cw);
        var ry = Math.Clamp((int)Math.Round(bestY / scaleY), 0, H - ch);
        return new PixelRect(rx, ry, cw, ch);
    }

    private static double[] Energy(byte[] rgba, int w, int h)
    {
        var luma = new double[w * h];
        var sat = new double[w * h];
        for (var i = 0; i < w * h; i++)
        {
            double r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3] / 255.0;
            luma[i] = (0.2126 * r + 0.7152 * g + 0.0722 * b) * a;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            sat[i] = max == 0 ? 0 : (max - min) / max * a;
        }
        var energy = new double[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var l = luma[y * w + Math.Max(0, x - 1)];
                var r = luma[y * w + Math.Min(w - 1, x + 1)];
                var t = luma[Math.Max(0, y - 1) * w + x];
                var b = luma[Math.Min(h - 1, y + 1) * w + x];
                energy[y * w + x] = Math.Abs(r - l) + Math.Abs(b - t) + 8 * sat[y * w + x];
            }
        }
        return energy;
    }
}
