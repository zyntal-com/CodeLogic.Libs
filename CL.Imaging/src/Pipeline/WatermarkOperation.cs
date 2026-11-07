using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
