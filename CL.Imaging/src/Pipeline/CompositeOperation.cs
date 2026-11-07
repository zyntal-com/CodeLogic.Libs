using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
