using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class SmartCropOperation(int width, int height, ResampleQuality quality) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var region = SmartCrop.FindRegion(src, width, height);
        state.Replace(SkiaOps.Resize(src, width, height, quality, region.ToSkiaF()));
    }
}
