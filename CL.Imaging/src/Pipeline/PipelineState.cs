using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>The bitmap a pipeline run is working on. The source document's bitmap is never written to.</summary>
internal sealed class PipelineState : IDisposable
{
    private bool _owned;

    public PipelineState(ImageDocument source)
    {
        Source = source;
        Current = source.Bitmap;
        Orientation = source.Orientation;
    }

    public ImageDocument Source { get; }
    public SKBitmap Current { get; private set; }
    public ImageOrientation Orientation { get; set; }
    public bool OrientationApplied { get; set; }
    public bool StripMetadata { get; set; }

    /// <summary>Replaces the current bitmap with a new one produced by an operation.</summary>
    public void Replace(SKBitmap next)
    {
        if (ReferenceEquals(next, Current)) return;
        if (_owned) Current.Dispose();
        Current = next;
        _owned = true;
    }

    /// <summary>The current bitmap, copied first if it still belongs to the source document.</summary>
    public SKBitmap Writable()
    {
        if (_owned) return Current;
        var copy = SkiaOps.CreateLike(Current, Current.Width, Current.Height);
        using (var src = Current.PeekPixels())
        using (var dst = copy.PeekPixels())
            src.ReadPixels(dst);
        Current = copy;
        _owned = true;
        return Current;
    }

    /// <summary>Takes the result bitmap out (a copy if no operation produced one).</summary>
    public SKBitmap Detach()
    {
        var result = Writable();
        _owned = false;
        return result;
    }

    public void Dispose()
    {
        if (_owned) Current.Dispose();
        _owned = false;
    }
}

/// <summary>Thrown by an operation whose arguments turn out invalid for the image it runs on; becomes the run's error.</summary>
internal sealed class OperationFailedException(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}
