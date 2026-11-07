using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>A recorded pipeline step. Arguments are validated when recorded; <see cref="Apply"/> may still fail for the actual image.</summary>
internal abstract class Operation
{
    public abstract void Apply(PipelineState state);

    protected static OperationFailedException Invalid(string message) => new(ImagingErrors.InvalidArgument(message));

    protected static void DrawImage(SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, x, y, SKSamplingOptions.Default, paint);
    }
}
