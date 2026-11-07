using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class ResizeOperation(int width, int height, ResizeMode mode, ResampleQuality quality, Color background, Anchor anchor) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        double sw = src.Width, sh = src.Height;
        var (w, h) = (width, height);
        if (w == 0) w = Math.Max(1, (int)Math.Round(sw * h / sh));
        if (h == 0) h = Math.Max(1, (int)Math.Round(sh * w / sw));
        Check(w, h);

        switch (mode)
        {
            case ResizeMode.Stretch:
                state.Replace(SkiaOps.Resize(src, w, h, quality));
                break;
            case ResizeMode.Fit:
            case ResizeMode.Max:
            {
                var scale = Math.Min(w / sw, h / sh);
                if (mode == ResizeMode.Max) scale = Math.Min(scale, 1);
                var (fw, fh) = (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));
                if (fw != src.Width || fh != src.Height) state.Replace(SkiaOps.Resize(src, fw, fh, quality));
                break;
            }
            case ResizeMode.Fill:
            {
                var scale = Math.Max(w / sw, h / sh);
                float cw = (float)Math.Min(sw, w / scale), ch = (float)Math.Min(sh, h / scale);
                var (x, y) = SkiaOps.Place(anchor, (float)sw, (float)sh, cw, ch);
                state.Replace(SkiaOps.Resize(src, w, h, quality, SKRect.Create(x, y, cw, ch)));
                break;
            }
            case ResizeMode.Pad:
            {
                var scale = Math.Min(w / sw, h / sh);
                var (fw, fh) = (Math.Max(1, (int)Math.Round(sw * scale)), Math.Max(1, (int)Math.Round(sh * scale)));
                using var fitted = SkiaOps.Resize(src, fw, fh, quality);
                var result = SkiaOps.CreateLike(src, w, h);
                using (var canvas = new SKCanvas(result))
                {
                    canvas.Clear(background);
                    var (x, y) = SkiaOps.Place(anchor, w, h, fw, fh);
                    DrawImage(canvas, fitted, MathF.Round(x), MathF.Round(y));
                }
                state.Replace(result);
                break;
            }
        }
    }

    internal static void Check(int w, int h)
    {
        if (w > SkiaOps.MaxDimension || h > SkiaOps.MaxDimension || (long)w * h > SkiaOps.MaxOutputPixels)
            throw Invalid($"The output size {w}×{h} is beyond the supported maximum ({SkiaOps.MaxDimension} per side, {SkiaOps.MaxOutputPixels:N0} pixels).");
    }
}
