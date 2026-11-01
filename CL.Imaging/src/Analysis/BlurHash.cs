using CL.Imaging.Errors;
using CL.Imaging.Internal;
using CodeLogic.Core.Results;

namespace CL.Imaging.Analysis;

/// <summary>
/// <see href="https://blurha.sh">BlurHash</see>: a short string (20–30 characters for typical settings) that decodes to
/// a blurred placeholder of the image. Compatible with the reference implementations.
/// </summary>
public static class BlurHash
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
    private const int MaxDecodeSide = 2048;

    /// <summary>The most pixels <see cref="Decode"/> produces (for example 1024 x 1024 or 2048 x 512).</summary>
    public const int MaxDecodePixels = 1024 * 1024;

    /// <summary>Encodes an image (analysed at up to 64 pixels on the longest side).</summary>
    /// <param name="image">The image.</param>
    /// <param name="xComponents">Horizontal detail, 1–9. Default 4.</param>
    /// <param name="yComponents">Vertical detail, 1–9. Default 3.</param>
    /// <returns>The hash, or <c>imaging.invalid_argument</c>.</returns>
    public static Result<string> Encode(ImageDocument image, int xComponents = 4, int yComponents = 3)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (xComponents is < 1 or > 9 || yComponents is < 1 or > 9)
            return ImagingErrors.InvalidArgument("BlurHash components must be 1–9.");
        var (px, w, h) = SkiaOps.Sample(image.Bitmap, 64);

        var linear = new double[w * h * 3];
        for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 3; c++)
                linear[i * 3 + c] = SrgbToLinear(px[i * 4 + c]);

        var factors = new double[xComponents * yComponents * 3];
        for (var j = 0; j < yComponents; j++)
        {
            for (var i = 0; i < xComponents; i++)
            {
                var normalisation = i == 0 && j == 0 ? 1.0 : 2.0;
                double r = 0, g = 0, b = 0;
                for (var y = 0; y < h; y++)
                {
                    var cy = Math.Cos(Math.PI * j * y / h);
                    for (var x = 0; x < w; x++)
                    {
                        var basis = Math.Cos(Math.PI * i * x / w) * cy;
                        var p = (y * w + x) * 3;
                        r += basis * linear[p];
                        g += basis * linear[p + 1];
                        b += basis * linear[p + 2];
                    }
                }
                var scale = normalisation / (w * h);
                var f = (j * xComponents + i) * 3;
                factors[f] = r * scale;
                factors[f + 1] = g * scale;
                factors[f + 2] = b * scale;
            }
        }

        var hash = new System.Text.StringBuilder(4 + 2 * xComponents * yComponents);
        Encode83(hash, (xComponents - 1) + (yComponents - 1) * 9, 1);
        double maxValue;
        if (factors.Length > 3)
        {
            double actualMax = 0;
            for (var k = 3; k < factors.Length; k++) actualMax = Math.Max(actualMax, Math.Abs(factors[k]));
            var quantisedMax = (int)Math.Clamp(Math.Floor(actualMax * 166 - 0.5), 0, 82);
            maxValue = (quantisedMax + 1) / 166.0;
            Encode83(hash, quantisedMax, 1);
        }
        else
        {
            maxValue = 1;
            Encode83(hash, 0, 1);
        }
        Encode83(hash, (LinearToSrgb(factors[0]) << 16) + (LinearToSrgb(factors[1]) << 8) + LinearToSrgb(factors[2]), 4);
        for (var k = 3; k < factors.Length; k += 3)
        {
            var r = Quantise(factors[k] / maxValue);
            var g = Quantise(factors[k + 1] / maxValue);
            var b = Quantise(factors[k + 2] / maxValue);
            Encode83(hash, r * 19 * 19 + g * 19 + b, 2);
        }
        return hash.ToString();
    }

    /// <summary>Whether <paramref name="hash"/> is a well-formed BlurHash.</summary>
    /// <param name="hash">The text to check.</param>
    public static bool IsValid(string? hash)
    {
        if (hash is null || hash.Length < 6) return false;
        foreach (var ch in hash) if (Alphabet.IndexOf(ch) < 0) return false;
        var sizeFlag = Decode83(hash.AsSpan(0, 1));
        // 1-9 components per axis (size flag 0-80); the two digits above 80 would mean ten rows.
        if (sizeFlag > 80) return false;
        var (nx, ny) = (sizeFlag % 9 + 1, sizeFlag / 9 + 1);
        return hash.Length == 4 + 2 * nx * ny;
    }

    /// <summary>Decodes a hash into a placeholder image.</summary>
    /// <param name="hash">The BlurHash.</param>
    /// <param name="width">Output width, 1–2048 (32 is plenty; scale it up in the browser).</param>
    /// <param name="height">Output height, 1–2048; width x height at most <see cref="MaxDecodePixels"/>.</param>
    /// <param name="punch">Contrast boost, greater than 0. Default 1.</param>
    /// <returns>The image, or <c>imaging.invalid_argument</c> for a malformed hash or size.</returns>
    public static Result<ImageDocument> Decode(string hash, int width, int height, double punch = 1)
    {
        if (!IsValid(hash)) return ImagingErrors.InvalidArgument("The text is not a valid BlurHash.");
        if (width is < 1 or > MaxDecodeSide || height is < 1 or > MaxDecodeSide)
            return ImagingErrors.InvalidArgument($"BlurHash output size must be 1–{MaxDecodeSide} per side.");
        if ((long)width * height > MaxDecodePixels)
            return ImagingErrors.InvalidArgument($"BlurHash output size must be at most {MaxDecodePixels:N0} pixels; decode small and scale up.");
        if (!(punch > 0) || !double.IsFinite(punch)) return ImagingErrors.InvalidArgument("BlurHash punch must be greater than 0.");

        var sizeFlag = Decode83(hash.AsSpan(0, 1));
        var (nx, ny) = (sizeFlag % 9 + 1, sizeFlag / 9 + 1);
        var maxValue = (Decode83(hash.AsSpan(1, 1)) + 1) / 166.0 * punch;
        var colors = new double[nx * ny * 3];
        var dc = Decode83(hash.AsSpan(2, 4));
        colors[0] = SrgbToLinear(dc >> 16);
        colors[1] = SrgbToLinear((dc >> 8) & 255);
        colors[2] = SrgbToLinear(dc & 255);
        for (var k = 1; k < nx * ny; k++)
        {
            var value = Decode83(hash.AsSpan(4 + k * 2, 2));
            colors[k * 3] = SignPow(((value / (19 * 19)) - 9) / 9.0, 2) * maxValue;
            colors[k * 3 + 1] = SignPow((((value / 19) % 19) - 9) / 9.0, 2) * maxValue;
            colors[k * 3 + 2] = SignPow(((value % 19) - 9) / 9.0, 2) * maxValue;
        }

        try
        {
            // The cosine basis is separable: tabulate it once per axis, then mix each row's components down to nx
            // colours, so a pixel costs nx multiply-adds instead of nx * ny cosines.
            var cosX = new double[nx * width];
            for (var i = 0; i < nx; i++)
                for (var x = 0; x < width; x++)
                    cosX[i * width + x] = Math.Cos(Math.PI * x * i / width);
            var cosY = new double[ny * height];
            for (var j = 0; j < ny; j++)
                for (var y = 0; y < height; y++)
                    cosY[j * height + y] = Math.Cos(Math.PI * y * j / height);

            var pixels = new byte[width * height * 4];
            var row = new double[nx * 3];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(row);
                for (var j = 0; j < ny; j++)
                {
                    var cy = cosY[j * height + y];
                    for (var i = 0; i < nx; i++)
                    {
                        var c = (j * nx + i) * 3;
                        row[i * 3] += colors[c] * cy;
                        row[i * 3 + 1] += colors[c + 1] * cy;
                        row[i * 3 + 2] += colors[c + 2] * cy;
                    }
                }
                for (var x = 0; x < width; x++)
                {
                    double r = 0, g = 0, b = 0;
                    for (var i = 0; i < nx; i++)
                    {
                        var basis = cosX[i * width + x];
                        r += row[i * 3] * basis;
                        g += row[i * 3 + 1] * basis;
                        b += row[i * 3 + 2] * basis;
                    }
                    var p = (y * width + x) * 4;
                    pixels[p] = (byte)LinearToSrgb(r);
                    pixels[p + 1] = (byte)LinearToSrgb(g);
                    pixels[p + 2] = (byte)LinearToSrgb(b);
                    pixels[p + 3] = 255;
                }
            }
            return ImageCompare.FromRgba(pixels, width, height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    private static int Quantise(double value) => (int)Math.Clamp(Math.Floor(SignPow(value, 0.5) * 9 + 9.5), 0, 18);

    private static double SignPow(double value, double exponent) => Math.CopySign(Math.Pow(Math.Abs(value), exponent), value);

    private static double SrgbToLinear(int value)
    {
        var v = value / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static int LinearToSrgb(double value)
    {
        var v = Math.Clamp(value, 0, 1);
        return v <= 0.0031308 ? (int)(v * 12.92 * 255 + 0.5) : (int)((1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255 + 0.5);
    }

    private static void Encode83(System.Text.StringBuilder sb, int value, int length)
    {
        for (var i = 1; i <= length; i++)
        {
            var digit = value / (int)Math.Pow(83, length - i) % 83;
            sb.Append(Alphabet[digit]);
        }
    }

    private static int Decode83(ReadOnlySpan<char> text)
    {
        var value = 0;
        foreach (var ch in text) value = value * 83 + Alphabet.IndexOf(ch);
        return value;
    }
}
