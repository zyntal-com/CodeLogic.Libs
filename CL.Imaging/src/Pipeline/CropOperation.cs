using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
