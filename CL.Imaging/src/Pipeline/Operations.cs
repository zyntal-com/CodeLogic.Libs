using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>A recorded pipeline step. Arguments are validated when recorded; <see cref="Apply"/> may still fail for the actual image.</summary>
internal abstract class Operation
{
    public abstract void Apply(PipelineState state);

    protected static OperationFailedException Invalid(string message) => new(ImagingErrors.InvalidArgument(message));

    protected static void DrawImage(SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, x, y, SKSamplingOptions.Default, paint);
    }
}

/// <summary>A per-pixel colour change expressed as a Skia colour filter. Consecutive colour operations run in one pass.</summary>
internal abstract class ColorOperation : Operation
{
    public abstract SKColorFilter CreateFilter();

    public override void Apply(PipelineState state) => ApplyAll(state, [this]);

    /// <summary>Composes the filters and draws the image through them once.</summary>
    public static void ApplyAll(PipelineState state, IReadOnlyList<ColorOperation> operations)
    {
        SKColorFilter? combined = null;
        try
        {
            foreach (var op in operations)
            {
                var next = op.CreateFilter();
                if (combined is null) { combined = next; continue; }
                var composed = SKColorFilter.CreateCompose(next, combined);   // inner (earlier) first
                combined.Dispose();
                next.Dispose();
                combined = composed;
            }
            var source = state.Current;
            var result = SkiaOps.CreateLike(source, source.Width, source.Height);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { ColorFilter = combined, BlendMode = SKBlendMode.Src })
                DrawImage(canvas, source, 0, 0, paint);
            state.Replace(result);
        }
        finally
        {
            combined?.Dispose();
        }
    }

    // Rec. 709 luma weights, as used by the W3C filter-effects matrices.
    protected const float Lr = 0.2126f, Lg = 0.7152f, Lb = 0.0722f;

    protected static float[] Identity() => [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    protected static float[] Lerp(float[] target, float amount)
    {
        var id = Identity();
        var m = new float[20];
        for (var i = 0; i < 20; i++) m[i] = id[i] + (target[i] - id[i]) * amount;
        return m;
    }

    protected static float[] SaturationMatrix(float s) =>
    [
        Lr + (1 - Lr) * s, Lg - Lg * s, Lb - Lb * s, 0, 0,
        Lr - Lr * s, Lg + (1 - Lg) * s, Lb - Lb * s, 0, 0,
        Lr - Lr * s, Lg - Lg * s, Lb + (1 - Lb) * s, 0, 0,
        0, 0, 0, 1, 0,
    ];
}

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

internal sealed class GammaOperation(double gamma) : ColorOperation
{
    public override SKColorFilter CreateFilter()
    {
        var table = new byte[256];
        var identity = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            identity[i] = (byte)i;
            table[i] = (byte)Math.Clamp(Math.Round(255 * Math.Pow(i / 255.0, 1 / gamma)), 0, 255);
        }
        return SKColorFilter.CreateTable(identity, table, table, table);
    }
}

internal sealed class AutoOrientOperation : Operation
{
    public override void Apply(PipelineState state)
    {
        if (state.Orientation != ImageOrientation.Normal)
            state.Replace(SkiaOps.Orient(state.Current, state.Orientation));
        state.Orientation = ImageOrientation.Normal;
        state.OrientationApplied = true;
    }
}

internal sealed class ResizeOperation(int width, int height, ResizeMode mode, ResampleQuality quality, Color background, Anchor anchor) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        double sw = src.Width, sh = src.Height;
        var (w, h) = (width, height);
        if (w == 0) w = Math.Max(1, (int)Math.Round(sw * h / sh));
        if (h == 0) h = Math.Max(1, (int)Math.Round(sh * w / sw));
        Check(w, h);

        switch (mode)
        {
            case ResizeMode.Stretch:
                state.Replace(SkiaOps.Resize(src, w, h, quality));
                break;
            case ResizeMode.Fit:
            case ResizeMode.Max:
            {
                var scale = Math.Min(w / sw, h / sh);
                if (mode == ResizeMode.Max) scale = Math.Min(scale, 1);
                var (fw, fh) = (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));
                if (fw != src.Width || fh != src.Height) state.Replace(SkiaOps.Resize(src, fw, fh, quality));
                break;
            }
            case ResizeMode.Fill:
            {
                var scale = Math.Max(w / sw, h / sh);
                float cw = (float)Math.Min(sw, w / scale), ch = (float)Math.Min(sh, h / scale);
                var (x, y) = SkiaOps.Place(anchor, (float)sw, (float)sh, cw, ch);
                state.Replace(SkiaOps.Resize(src, w, h, quality, SKRect.Create(x, y, cw, ch)));
                break;
            }
            case ResizeMode.Pad:
            {
                var scale = Math.Min(w / sw, h / sh);
                var (fw, fh) = (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));
                using var fitted = SkiaOps.Resize(src, fw, fh, quality);
                var result = SkiaOps.CreateLike(src, w, h);
                using (var canvas = new SKCanvas(result))
                {
                    canvas.Clear(background);
                    var (x, y) = SkiaOps.Place(anchor, w, h, fw, fh);
                    DrawImage(canvas, fitted, MathF.Round(x), MathF.Round(y));
                }
                state.Replace(result);
                break;
            }
        }
    }

    internal static void Check(int w, int h)
    {
        if (w > SkiaOps.MaxDimension || h > SkiaOps.MaxDimension || (long)w * h > SkiaOps.MaxOutputPixels)
            throw Invalid($"The output size {w}×{h} is beyond the supported maximum ({SkiaOps.MaxDimension} per side, {SkiaOps.MaxOutputPixels:N0} pixels).");
    }
}

internal sealed class CropOperation(PixelRect rect) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        if (!rect.FitsIn(src.Width, src.Height))
            throw Invalid($"The crop rectangle {rect} does not lie inside the {src.Width}×{src.Height} image.");
        state.Replace(Crop(src, rect));
    }

    public static SKBitmap Crop(SKBitmap src, PixelRect rect)
    {
        var result = SkiaOps.CreateLike(src, rect.Width, rect.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        DrawImage(canvas, src, -rect.X, -rect.Y, paint);
        return result;
    }
}

internal sealed class SmartCropOperation(int width, int height, ResampleQuality quality) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var region = SmartCrop.FindRegion(src, width, height);
        state.Replace(SkiaOps.Resize(src, width, height, quality, region.ToSkiaF()));
    }
}

internal sealed class RotateOperation(double degrees, Color background) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var d = degrees % 360;
        if (d < 0) d += 360;
        if (d == 0) return;
        var quarter = d % 90 == 0;
        var rad = d * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(rad)), Math.Abs(Math.Sin(rad)));
        var w = quarter ? (d == 180 ? src.Width : src.Height) : (int)Math.Ceiling(src.Width * cos + src.Height * sin - 1e-6);
        var h = quarter ? (d == 180 ? src.Height : src.Width) : (int)Math.Ceiling(src.Width * sin + src.Height * cos - 1e-6);
        ResizeOperation.Check(w, h);
        var result = SkiaOps.CreateLike(src, w, h);
        using (var canvas = new SKCanvas(result))
        {
            if (quarter)
            {
                // Whole-pixel transforms, so odd sizes map pixel to pixel.
                switch (d)
                {
                    case 90: canvas.Translate(w, 0); break;
                    case 180: canvas.Translate(w, h); break;
                    default: canvas.Translate(0, h); break;
                }
                canvas.RotateDegrees((float)d);
            }
            else
            {
                canvas.Clear(background);
                canvas.Translate(w / 2f, h / 2f);
                canvas.RotateDegrees((float)d);
                canvas.Translate(-src.Width / 2f, -src.Height / 2f);
            }
            using var image = SKImage.FromBitmap(src);
            using var paint = new SKPaint { IsAntialias = !quarter };
            canvas.DrawImage(image, 0, 0, quarter ? SKSamplingOptions.Default : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        state.Replace(result);
    }
}

internal sealed class FlipOperation(bool horizontal) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var result = SkiaOps.CreateLike(src, src.Width, src.Height);
        using (var canvas = new SKCanvas(result))
        {
            if (horizontal) canvas.Scale(-1, 1, src.Width / 2f, 0);
            else canvas.Scale(1, -1, 0, src.Height / 2f);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            DrawImage(canvas, src, 0, 0, paint);
        }
        state.Replace(result);
    }
}

internal sealed class BlurOperation(double sigma) : Operation
{
    public override void Apply(PipelineState state)
    {
        using var filter = SKImageFilter.CreateBlur((float)sigma, (float)sigma, SKShaderTileMode.Clamp);
        state.Replace(Filter(state.Current, filter));
    }

    public static SKBitmap Filter(SKBitmap src, SKImageFilter filter)
    {
        var result = SkiaOps.CreateLike(src, src.Width, src.Height);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { ImageFilter = filter, BlendMode = SKBlendMode.Src };
        DrawImage(canvas, src, 0, 0, paint);
        return result;
    }
}

internal sealed class SharpenOperation(double amount, double radius) : Operation
{
    public override void Apply(PipelineState state)
    {
        // Unsharp mask: source + amount × (source − blurred).
        using var blur = SKImageFilter.CreateBlur((float)radius, (float)radius, SKShaderTileMode.Clamp);
        using var sharpen = SKImageFilter.CreateArithmetic(0, 1 + (float)amount, -(float)amount, 0, true, blur, null);
        state.Replace(BlurOperation.Filter(state.Current, sharpen));
    }
}

internal sealed class PixelateOperation(int size) : Operation
{
    public override void Apply(PipelineState state)
    {
        // Exact block averages on the premultiplied pixels.
        var bitmap = state.Writable();
        var span = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        int w = bitmap.Width, h = bitmap.Height;
        unchecked
        {
            var pixels = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span), span.Length);
            for (var by = 0; by < h; by += size)
            {
                for (var bx = 0; bx < w; bx += size)
                {
                    int ex = Math.Min(bx + size, w), ey = Math.Min(by + size, h);
                    long r = 0, g = 0, b = 0, a = 0;
                    for (var y = by; y < ey; y++)
                    {
                        var row = y * stride;
                        for (var x = bx; x < ex; x++)
                        {
                            var i = row + x * 4;
                            r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; a += pixels[i + 3];
                        }
                    }
                    var n = (ex - bx) * (ey - by);
                    byte rr = (byte)((r + n / 2) / n), gg = (byte)((g + n / 2) / n), bb = (byte)((b + n / 2) / n), aa = (byte)((a + n / 2) / n);
                    for (var y = by; y < ey; y++)
                    {
                        var row = y * stride;
                        for (var x = bx; x < ex; x++)
                        {
                            var i = row + x * 4;
                            pixels[i] = rr; pixels[i + 1] = gg; pixels[i + 2] = bb; pixels[i + 3] = aa;
                        }
                    }
                }
            }
        }
        bitmap.NotifyPixelsChanged();
    }
}

internal sealed class RoundCornersOperation(float radius) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var result = SkiaOps.CreateLike(src, src.Width, src.Height);
        using (var canvas = new SKCanvas(result))
        using (var rrect = new SKRoundRect(new SKRect(0, 0, src.Width, src.Height), radius, radius))
        {
            canvas.ClipRoundRect(rrect, SKClipOperation.Intersect, antialias: true);
            DrawImage(canvas, src, 0, 0);
        }
        state.Replace(result);
    }
}

internal sealed class BorderOperation(int thickness, Color color) : Operation
{
    public override void Apply(PipelineState state)
    {
        var bitmap = state.Writable();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
        var t = Math.Min(thickness, Math.Min(bitmap.Width, bitmap.Height) / 2f);
        canvas.ClipRect(new SKRect(t, t, bitmap.Width - t, bitmap.Height - t), SKClipOperation.Difference);
        canvas.DrawRect(new SKRect(0, 0, bitmap.Width, bitmap.Height), paint);
    }
}

internal sealed class PadOperation(int left, int top, int right, int bottom, Color color) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        int w = src.Width + left + right, h = src.Height + top + bottom;
        ResizeOperation.Check(w, h);
        var result = SkiaOps.CreateLike(src, w, h);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            DrawImage(canvas, src, left, top, paint);
        }
        state.Replace(result);
    }
}

internal sealed class WatermarkOperation(Watermark watermark, Anchor anchor, double opacity, int margin) : Operation
{
    public override void Apply(PipelineState state)
    {
        var bitmap = state.Writable();
        using var canvas = new SKCanvas(bitmap);
        var alpha = (byte)Math.Round(opacity * 255);
        switch (watermark)
        {
            case WatermarkImage wi:
            {
                var mark = wi.Image.Bitmap;
                float w = mark.Width, h = mark.Height;
                if (wi.RelativeWidth is { } rel)
                {
                    w = (float)(bitmap.Width * rel);
                    h = w * mark.Height / mark.Width;
                }
                var (x, y) = SkiaOps.Place(anchor, bitmap.Width, bitmap.Height, w, h, margin);
                using var image = SKImage.FromBitmap(mark);
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
                canvas.DrawImage(image, SKRect.Create(x, y, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
                break;
            }
            case WatermarkText wt:
            {
                var font = wt.Font ?? ImageFont.Default(Math.Max(8, bitmap.Width / 20f));
                var color = (wt.Color ?? Color.White);
                using var typeface = font.CreateTypeface();
                using var skFont = new SKFont(typeface, font.Size) { Subpixel = true, Edging = SKFontEdging.Antialias };
                using var paint = new SKPaint { Color = ((SKColor)color).WithAlpha((byte)(color.A * opacity)), IsAntialias = true };
                var width = skFont.MeasureText(wt.Text, paint);
                skFont.GetFontMetrics(out var metrics);
                var height = metrics.Descent - metrics.Ascent;
                var (x, y) = SkiaOps.Place(anchor, bitmap.Width, bitmap.Height, width, height, margin);
                canvas.DrawText(wt.Text, x, y - metrics.Ascent, SKTextAlign.Left, skFont, paint);
                break;
            }
        }
    }
}

internal sealed class DrawTextOperation(string text, ImageFont font, ImagePoint position, Color color, TextAlign align) : Operation
{
    public override void Apply(PipelineState state)
    {
        var bitmap = state.Writable();
        using var canvas = new SKCanvas(bitmap);
        using var typeface = font.CreateTypeface();
        using var skFont = new SKFont(typeface, font.Size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        skFont.GetFontMetrics(out var metrics);
        var skAlign = align switch { TextAlign.Center => SKTextAlign.Center, TextAlign.Right => SKTextAlign.Right, _ => SKTextAlign.Left };
        canvas.DrawText(text, position.X, position.Y - metrics.Ascent, skAlign, skFont, paint);
    }
}

internal sealed class ShapeOperation(Action<SKCanvas, SKPaint> draw, Color color, float strokeWidth) : Operation
{
    public override void Apply(PipelineState state)
    {
        var bitmap = state.Writable();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = strokeWidth > 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
            StrokeWidth = strokeWidth,
        };
        draw(canvas, paint);
    }
}

internal sealed class CompositeOperation(ImageDocument overlay, ImagePoint position, BlendMode mode, double opacity) : Operation
{
    public override void Apply(PipelineState state)
    {
        var bitmap = state.Writable();
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            BlendMode = SkiaOps.ToSkia(mode),
            Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255)),
        };
        DrawImage(canvas, overlay.Bitmap, position.X, position.Y, paint);
    }
}

internal sealed class StripMetadataOperation : Operation
{
    public override void Apply(PipelineState state) => state.StripMetadata = true;
}
