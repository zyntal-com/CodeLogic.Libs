using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
