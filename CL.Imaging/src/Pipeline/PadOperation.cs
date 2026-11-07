using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

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
