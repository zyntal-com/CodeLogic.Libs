using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class AutoOrientOperation : Operation
{
    public override void Apply(PipelineState state)
    {
        if (state.Orientation != ImageOrientation.Normal)
            state.Replace(SkiaOps.Orient(state.Current, state.Orientation));
        state.Orientation = ImageOrientation.Normal;
        state.OrientationApplied = true;
    }
}
