using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

internal sealed class RotateOperation(double degrees, Color background) : Operation
{
    public override void Apply(PipelineState state)
    {
        var src = state.Current;
        var d = degrees % 360;
        if (d < 0) d += 360;
        if (d == 0) return;
        var quarter = d % 90 == 0;
        var rad = d * Math.PI / 180;
        var (cos, sin) = (Math.Abs(Math.Cos(rad)), Math.Abs(Math.Sin(rad)));
        var w = quarter ? (d == 180 ? src.Width : src.Height) : (int)Math.Ceiling(src.Width * cos + src.Height * sin - 1e-6);
        var h = quarter ? (d == 180 ? src.Height : src.Width) : (int)Math.Ceiling(src.Width * sin + src.Height * cos - 1e-6);
        ResizeOperation.Check(w, h);
        var result = SkiaOps.CreateLike(src, w, h);
        using (var canvas = new SKCanvas(result))
        {
            if (quarter)
            {
                // Whole-pixel transforms, so odd sizes map pixel to pixel.
                switch (d)
                {
                    case 90: canvas.Translate(w, 0); break;
                    case 180: canvas.Translate(w, h); break;
                    default: canvas.Translate(0, h); break;
                }
                canvas.RotateDegrees((float)d);
            }
            else
            {
                canvas.Clear(background);
                canvas.Translate(w / 2f, h / 2f);
                canvas.RotateDegrees((float)d);
                canvas.Translate(-src.Width / 2f, -src.Height / 2f);
            }
            using var image = SKImage.FromBitmap(src);
            using var paint = new SKPaint { IsAntialias = !quarter };
            canvas.DrawImage(image, 0, 0, quarter ? SKSamplingOptions.Default : new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        state.Replace(result);
    }
}
