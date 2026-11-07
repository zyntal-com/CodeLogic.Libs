using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class SharpenOperation(double amount, double radius) : Operation
{
    public override void Apply(PipelineState state)
    {
        // Unsharp mask: source + amount × (source − blurred).
        using var blur = SKImageFilter.CreateBlur((float)radius, (float)radius, SKShaderTileMode.Clamp);
        using var sharpen = SKImageFilter.CreateArithmetic(0, 1 + (float)amount, -(float)amount, 0, true, blur, null);
        state.Replace(BlurOperation.Filter(state.Current, sharpen));
    }
}
