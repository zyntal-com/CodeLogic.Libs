using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
