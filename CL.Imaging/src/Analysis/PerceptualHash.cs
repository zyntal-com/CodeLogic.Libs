using System.Numerics;
using CL.Imaging.Internal;

namespace CL.Imaging.Analysis;

/// <summary>
/// 64-bit perceptual hashes for near-duplicate detection: similar-looking images (resized, re-encoded, slightly
/// edited) give hashes a small Hamming distance apart. Transparent pixels are judged as if on white.
/// </summary>
public static class PerceptualHash
{
    private const int DctSize = 32;
    private static readonly double[] DctCos = CreateDctTable();

    /// <summary>Difference hash: each bit says whether a pixel is brighter than its right neighbour on a 9×8 thumbnail. Fast; robust to scaling and compression.</summary>
    /// <param name="image">The image.</param>
    public static ulong DHash(ImageDocument image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var gray = Grayscale(image, 9, 8);
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (gray[y * 9 + x] > gray[y * 9 + x + 1]) hash |= 1UL << bit;
        return hash;
    }

    /// <summary>
    /// DCT hash: the 8×8 lowest frequencies of a 32×32 grey thumbnail's DCT, each bit set when above their median.
    /// More robust than <see cref="DHash"/> to brightness/contrast changes and mild edits.
    /// </summary>
    /// <param name="image">The image.</param>
    public static ulong PHash(ImageDocument image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var gray = Grayscale(image, DctSize, DctSize);

        // Separable 2-D DCT-II, keeping only the 8×8 low-frequency block.
        var rows = new double[DctSize * 8];
        for (var y = 0; y < DctSize; y++)
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var x = 0; x < DctSize; x++) sum += gray[y * DctSize + x] * DctCos[u * DctSize + x];
                rows[y * 8 + u] = sum;
            }
        var coefficients = new double[64];
        for (var v = 0; v < 8; v++)
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var y = 0; y < DctSize; y++) sum += rows[y * 8 + u] * DctCos[v * DctSize + y];
                coefficients[v * 8 + u] = sum;
            }

        // Median of the 63 AC terms (the DC term only carries the average brightness).
        var ac = coefficients[1..].ToArray();
        Array.Sort(ac);
        var median = ac[ac.Length / 2];
        ulong hash = 0;
        for (var i = 1; i < 64; i++)
            if (coefficients[i] > median) hash |= 1UL << i;
        return hash;
    }

    /// <summary>The number of differing bits, 0 (identical) to 64.</summary>
    /// <param name="a">First hash.</param>
    /// <param name="b">Second hash.</param>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Similarity from 0 (opposite) to 1 (identical hashes): <c>1 − distance / 64</c>.</summary>
    /// <param name="a">First hash.</param>
    /// <param name="b">Second hash.</param>
    public static double Similarity(ulong a, ulong b) => 1 - HammingDistance(a, b) / 64.0;

    /// <summary>Whether two hashes are within <paramref name="maxDistance"/> bits (10 is a common near-duplicate threshold).</summary>
    /// <param name="a">First hash.</param>
    /// <param name="b">Second hash.</param>
    /// <param name="maxDistance">Largest Hamming distance that still counts as a match.</param>
    public static bool IsNearDuplicate(ulong a, ulong b, int maxDistance = 10) => HammingDistance(a, b) <= maxDistance;

    private static double[] Grayscale(ImageDocument image, int width, int height)
    {
        var (px, _, _) = SkiaOps.ReadPixels(image.Bitmap, width, height);
        var gray = new double[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var a = px[i * 4 + 3] / 255.0;
            var luma = 0.299 * px[i * 4] + 0.587 * px[i * 4 + 1] + 0.114 * px[i * 4 + 2];
            gray[i] = luma * a + 255 * (1 - a);
        }
        return gray;
    }

    private static double[] CreateDctTable()
    {
        var table = new double[8 * DctSize];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < DctSize; x++)
                table[u * DctSize + x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * DctSize));
        return table;
    }
}
