using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
