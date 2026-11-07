using CL.Imaging.Analysis;
using CL.Imaging.Errors;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>A per-pixel colour change expressed as a Skia colour filter. Consecutive colour operations run in one pass.</summary>
internal abstract class ColorOperation : Operation
{
    public abstract SKColorFilter CreateFilter();

    public override void Apply(PipelineState state) => ApplyAll(state, [this]);

    /// <summary>Composes the filters and draws the image through them once.</summary>
    public static void ApplyAll(PipelineState state, IReadOnlyList<ColorOperation> operations)
    {
        SKColorFilter? combined = null;
        try
        {
            foreach (var op in operations)
            {
                var next = op.CreateFilter();
                if (combined is null) { combined = next; continue; }
                var composed = SKColorFilter.CreateCompose(next, combined);   // inner (earlier) first
                combined.Dispose();
                next.Dispose();
                combined = composed;
            }
            var source = state.Current;
            var result = SkiaOps.CreateLike(source, source.Width, source.Height);
            using (var canvas = new SKCanvas(result))
            using (var paint = new SKPaint { ColorFilter = combined, BlendMode = SKBlendMode.Src })
                DrawImage(canvas, source, 0, 0, paint);
            state.Replace(result);
        }
        finally
        {
            combined?.Dispose();
        }
    }

    // Rec. 709 luma weights, as used by the W3C filter-effects matrices.
    protected const float Lr = 0.2126f, Lg = 0.7152f, Lb = 0.0722f;

    protected static float[] Identity() => [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    protected static float[] Lerp(float[] target, float amount)
    {
        var id = Identity();
        var m = new float[20];
        for (var i = 0; i < 20; i++) m[i] = id[i] + (target[i] - id[i]) * amount;
        return m;
    }

    protected static float[] SaturationMatrix(float s) =>
    [
        Lr + (1 - Lr) * s, Lg - Lg * s, Lb - Lb * s, 0, 0,
        Lr - Lr * s, Lg + (1 - Lg) * s, Lb - Lb * s, 0, 0,
        Lr - Lr * s, Lg - Lg * s, Lb + (1 - Lb) * s, 0, 0,
        0, 0, 0, 1, 0,
    ];
}
