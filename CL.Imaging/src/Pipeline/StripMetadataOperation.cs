using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class StripMetadataOperation : Operation
{
    public override void Apply(PipelineState state) => state.StripMetadata = true;
}
