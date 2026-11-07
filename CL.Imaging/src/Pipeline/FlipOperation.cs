using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
