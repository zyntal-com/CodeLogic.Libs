using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class PixelateOperation(int size) : Operation
{
    public override void Apply(PipelineState state)
    {
        // Exact block averages on the premultiplied pixels.
        var bitmap = state.Writable();
        var span = bitmap.GetPixelSpan();
        var stride = bitmap.RowBytes;
        int w = bitmap.Width, h = bitmap.Height;
        unchecked
        {
            var pixels = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span), span.Length);
            for (var by = 0; by < h; by += size)
            {
                for (var bx = 0; bx < w; bx += size)
                {
                    int ex = Math.Min(bx + size, w), ey = Math.Min(by + size, h);
                    long r = 0, g = 0, b = 0, a = 0;
                    for (var y = by; y < ey; y++)
                    {
                        var row = y * stride;
                        for (var x = bx; x < ex; x++)
                        {
                            var i = row + x * 4;
                            r += pixels[i]; g += pixels[i + 1]; b += pixels[i + 2]; a += pixels[i + 3];
                        }
                    }
                    var n = (ex - bx) * (ey - by);
                    byte rr = (byte)((r + n / 2) / n), gg = (byte)((g + n / 2) / n), bb = (byte)((b + n / 2) / n), aa = (byte)((a + n / 2) / n);
                    for (var y = by; y < ey; y++)
                    {
                        var row = y * stride;
                        for (var x = bx; x < ex; x++)
                        {
                            var i = row + x * 4;
                            pixels[i] = rr; pixels[i + 1] = gg; pixels[i + 2] = bb; pixels[i + 3] = aa;
                        }
                    }
                }
            }
        }
        bitmap.NotifyPixelsChanged();
    }
}
