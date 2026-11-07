using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class MatrixOperation(float[] matrix) : ColorOperation
{
    public override SKColorFilter CreateFilter() => SKColorFilter.CreateColorMatrix(matrix);

    // Translation terms are normalised (0–1) in Skia's colour matrix.
    public static MatrixOperation Brightness(double amount)
    {
        var a = (float)amount;
        return new([1, 0, 0, 0, a, 0, 1, 0, 0, a, 0, 0, 1, 0, a, 0, 0, 0, 1, 0]);
    }

    public static MatrixOperation Contrast(double amount)
    {
        var c = 1 + (float)amount;
        var t = 0.5f * (1 - c);
        return new([c, 0, 0, 0, t, 0, c, 0, 0, t, 0, 0, c, 0, t, 0, 0, 0, 1, 0]);
    }

    public static MatrixOperation Saturation(double amount) => new(SaturationMatrix(1 + (float)amount));

    public static MatrixOperation Exposure(double stops)
    {
        var m = (float)Math.Pow(2, stops);
        return new([m, 0, 0, 0, 0, 0, m, 0, 0, 0, 0, 0, m, 0, 0, 0, 0, 0, 1, 0]);
    }

    public static MatrixOperation Hue(double degrees)
    {
        var rad = degrees * Math.PI / 180;
        float cos = (float)Math.Cos(rad), sin = (float)Math.Sin(rad);
        return new(
        [
            0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
            0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
            0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    public static MatrixOperation Grayscale(double amount) => new(SaturationMatrix(1 - (float)amount));

    public static MatrixOperation Sepia(double amount) => new(Lerp(
    [
        0.393f, 0.769f, 0.189f, 0, 0,
        0.349f, 0.686f, 0.168f, 0, 0,
        0.272f, 0.534f, 0.131f, 0, 0,
        0, 0, 0, 1, 0,
    ], (float)amount));

    public static MatrixOperation Invert() => new([-1, 0, 0, 0, 1, 0, -1, 0, 0, 1, 0, 0, -1, 0, 1, 0, 0, 0, 1, 0]);

    public static MatrixOperation Tint(Color color, double amount)
    {
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        return new(Lerp(
        [
            Lr * r, Lg * r, Lb * r, 0, 0,
            Lr * g, Lg * g, Lb * g, 0, 0,
            Lr * b, Lg * b, Lb * b, 0, 0,
            0, 0, 0, 1, 0,
        ], (float)amount));
    }
}
