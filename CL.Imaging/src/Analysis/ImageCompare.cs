using CL.Imaging.Errors;
using CL.Imaging.Internal;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging.Analysis;

/// <summary>Options for <see cref="ImageCompare.Difference"/>.</summary>
public sealed record ImageCompareOptions
{
    /// <summary>Largest per-channel difference (0–255) at which two pixels still count as equal. Default 8.</summary>
    public int Tolerance { get; init; } = 8;

    /// <summary>Resample the second image to the first one's size when they differ; when false, a size mismatch is an error. Default true.</summary>
    public bool ResizeToMatch { get; init; } = true;

    /// <summary>Produce <see cref="ImageComparison.DiffImage"/>. Default false.</summary>
    public bool CreateDiffImage { get; init; }

    /// <summary>Colour used for differing pixels in the diff image. Default red.</summary>
    public Color Highlight { get; init; } = Color.Red;
}

/// <summary>The result of comparing two images. Dispose it to release <see cref="DiffImage"/>.</summary>
public sealed class ImageComparison : IDisposable
{
    internal ImageComparison(double similarity, double meanError, long differentPixels, long totalPixels, ImageDocument? diff)
    {
        Similarity = similarity;
        MeanError = meanError;
        DifferentPixels = differentPixels;
        TotalPixels = totalPixels;
        DiffImage = diff;
    }

    /// <summary>1 − <see cref="MeanError"/>: 1 for identical images, towards 0 for opposite ones.</summary>
    public double Similarity { get; }

    /// <summary>Mean absolute difference over all RGBA channels, scaled to 0–1.</summary>
    public double MeanError { get; }

    /// <summary>Pixels where any channel differs by more than <see cref="ImageCompareOptions.Tolerance"/>.</summary>
    public long DifferentPixels { get; }

    /// <summary>Pixels compared.</summary>
    public long TotalPixels { get; }

    /// <summary>Fraction of pixels that differ, 0–1.</summary>
    public double DifferentRatio => TotalPixels == 0 ? 0 : (double)DifferentPixels / TotalPixels;

    /// <summary>
    /// When requested: the first image faded to grey with differing pixels in the highlight colour; otherwise null.
    /// </summary>
    public ImageDocument? DiffImage { get; }

    /// <summary>Releases <see cref="DiffImage"/>.</summary>
    public void Dispose() => DiffImage?.Dispose();
}

/// <summary>Pixel-level image comparison (for visual regression tests and change detection).</summary>
public static class ImageCompare
{
    /// <summary>Compares two images pixel by pixel.</summary>
    /// <param name="a">The reference image.</param>
    /// <param name="b">The image compared with it.</param>
    /// <param name="options">Tolerance, resizing and diff-image options.</param>
    /// <returns>The comparison, or <c>imaging.invalid_argument</c> for a size mismatch when resizing is off.</returns>
    public static Result<ImageComparison> Difference(ImageDocument a, ImageDocument b, ImageCompareOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        options ??= new ImageCompareOptions();
        if (options.Tolerance is < 0 or > 255) return ImagingErrors.InvalidArgument("Tolerance must be 0–255.");
        if ((a.Width != b.Width || a.Height != b.Height) && !options.ResizeToMatch)
            return ImagingErrors.InvalidArgument($"The images differ in size ({a.Width}×{a.Height} and {b.Width}×{b.Height}).");
        try
        {
            int w = a.Width, h = a.Height;
            var (pa, _, _) = SkiaOps.ReadPixels(a.Bitmap, w, h);
            var (pb, _, _) = SkiaOps.ReadPixels(b.Bitmap, w, h);
            long sum = 0, different = 0;
            byte[]? diff = options.CreateDiffImage ? new byte[pa.Length] : null;
            for (var i = 0; i < pa.Length; i += 4)
            {
                int dr = Math.Abs(pa[i] - pb[i]), dg = Math.Abs(pa[i + 1] - pb[i + 1]),
                    db = Math.Abs(pa[i + 2] - pb[i + 2]), da = Math.Abs(pa[i + 3] - pb[i + 3]);
                sum += dr + dg + db + da;
                var differs = Math.Max(Math.Max(dr, dg), Math.Max(db, da)) > options.Tolerance;
                if (differs) different++;
                if (diff is null) continue;
                if (differs)
                {
                    diff[i] = options.Highlight.R; diff[i + 1] = options.Highlight.G; diff[i + 2] = options.Highlight.B; diff[i + 3] = 255;
                }
                else
                {
                    var luma = (byte)((0.299 * pa[i] + 0.587 * pa[i + 1] + 0.114 * pa[i + 2]) * 0.3 + 255 * 0.7);
                    diff[i] = diff[i + 1] = diff[i + 2] = luma;
                    diff[i + 3] = 255;
                }
            }
            var total = (long)w * h;
            var meanError = total == 0 ? 0 : sum / (total * 4.0 * 255);
            ImageDocument? diffImage = diff is null ? null : FromRgba(diff, w, h);
            return new ImageComparison(1 - meanError, meanError, different, total, diffImage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    internal static ImageDocument FromRgba(byte[] rgba, int width, int height)
    {
        var bitmap = SkiaOps.Create(width, height, null);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var src = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var dst = bitmap.PeekPixels();
            src.ReadPixels(dst);
        }
        finally
        {
            handle.Free();
        }
        return ImageDocument.FromOwnedBitmap(bitmap);
    }
}
