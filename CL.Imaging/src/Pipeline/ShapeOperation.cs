using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
