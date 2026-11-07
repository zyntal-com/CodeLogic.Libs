using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
