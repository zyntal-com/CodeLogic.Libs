using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>Bitmap helpers shared by the pipeline, the tools and the analysis code. Every SK object is disposed.</summary>
internal static class SkiaOps
{
    /// <summary>The largest width or height any operation may produce.</summary>
    public const int MaxDimension = 32_767;

    /// <summary>The largest pixel count any operation may produce (16384²).</summary>
    public const long MaxOutputPixels = 16_384L * 16_384;

    /// <summary>A new, transparent RGBA (premultiplied) bitmap in <paramref name="colorSpace"/>.</summary>
    public static SKBitmap Create(int width, int height, SKColorSpace? colorSpace)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxOutputPixels)
            throw new AllocationFailedException(width, height);
        var bitmap = new SKBitmap();
        if (!bitmap.TryAllocPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace)))
        {
            bitmap.Dispose();
            throw new AllocationFailedException(width, height);
        }
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    /// <summary>A new bitmap of the given size in the same colour space as <paramref name="like"/>.</summary>
    public static SKBitmap CreateLike(SKBitmap like, int width, int height) => Create(width, height, like.ColorSpace);

    public static SKSamplingOptions Sampling(ResampleQuality quality) => quality switch
    {
        ResampleQuality.Nearest => new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
        ResampleQuality.Medium => new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
        ResampleQuality.High => new SKSamplingOptions(SKCubicResampler.Mitchell),
        _ => new SKSamplingOptions(SKCubicResampler.Mitchell),
    };

    /// <summary>
    /// Scales <paramref name="source"/> (or its <paramref name="sourceRect"/>) to <paramref name="width"/>×<paramref name="height"/>.
    /// With <see cref="ResampleQuality.High"/>, reductions below one half are first halved with bilinear filtering,
    /// which keeps the bicubic pass from aliasing.
    /// </summary>
    public static SKBitmap Resize(SKBitmap source, int width, int height, ResampleQuality quality, SKRect? sourceRect = null)
    {
        var rect = sourceRect ?? new SKRect(0, 0, source.Width, source.Height);
        SKBitmap? intermediate = null;
        try
        {
            if (quality == ResampleQuality.High)
            {
                // Halve while still more than twice the target size.
                var current = source;
                var currentRect = rect;
                while (currentRect.Width / 2 >= width && currentRect.Height / 2 >= height && currentRect.Width >= 4 && currentRect.Height >= 4)
                {
                    var hw = Math.Max(1, (int)Math.Round(currentRect.Width / 2));
                    var hh = Math.Max(1, (int)Math.Round(currentRect.Height / 2));
                    var halved = Draw(current, currentRect, hw, hh, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    intermediate?.Dispose();
                    intermediate = halved;
                    current = halved;
                    currentRect = new SKRect(0, 0, hw, hh);
                }
                return Draw(current, currentRect, width, height, Sampling(quality));
            }
            return Draw(source, rect, width, height, Sampling(quality));
        }
        finally
        {
            intermediate?.Dispose();
        }
    }

    private static SKBitmap Draw(SKBitmap source, SKRect sourceRect, int width, int height, SKSamplingOptions sampling)
    {
        var result = CreateLike(source, width, height);
        using var canvas = new SKCanvas(result);
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, sourceRect, new SKRect(0, 0, width, height), sampling, paint);
        return result;
    }

    /// <summary>A copy of <paramref name="source"/> turned upright for its EXIF <paramref name="orientation"/>.</summary>
    public static SKBitmap Orient(SKBitmap source, ImageOrientation orientation)
    {
        var swap = orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;
        var result = CreateLike(source, swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(result);
        switch (orientation)
        {
            case ImageOrientation.Normal: break;
            case ImageOrientation.FlipHorizontal: canvas.Scale(-1, 1, result.Width / 2f, 0); break;
            case ImageOrientation.Rotate180: canvas.RotateDegrees(180, result.Width / 2f, result.Height / 2f); break;
            case ImageOrientation.FlipVertical: canvas.Scale(1, -1, 0, result.Height / 2f); break;
            case ImageOrientation.Transpose: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, source.Height / 2f); break;
            case ImageOrientation.Rotate90: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); break;
            case ImageOrientation.Transverse: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); canvas.Scale(-1, 1, source.Width / 2f, 0); break;
            case ImageOrientation.Rotate270: canvas.Translate(0, result.Height); canvas.RotateDegrees(270); break;
        }
        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default, paint);
        return result;
    }

    /// <summary>The top-left offset that places an inner box inside an outer box at <paramref name="anchor"/>, <paramref name="margin"/> from the edges.</summary>
    public static (float X, float Y) Place(Anchor anchor, float outerWidth, float outerHeight, float innerWidth, float innerHeight, float margin = 0)
    {
        var x = anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => margin,
            Anchor.Top or Anchor.Center or Anchor.Bottom => (outerWidth - innerWidth) / 2,
            _ => outerWidth - innerWidth - margin,
        };
        var y = anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => margin,
            Anchor.Left or Anchor.Center or Anchor.Right => (outerHeight - innerHeight) / 2,
            _ => outerHeight - innerHeight - margin,
        };
        return (x, y);
    }

    /// <summary>
    /// A small unpremultiplied RGBA copy no larger than <paramref name="maxSide"/> on either side (or the original size),
    /// for analysis. Pixels are returned as bytes, 4 per pixel.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Sample(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return ReadPixels(source, width, height);
    }

    /// <summary>Unpremultiplied RGBA bytes of <paramref name="source"/> scaled to exactly <paramref name="width"/>×<paramref name="height"/>.</summary>
    public static (byte[] Pixels, int Width, int Height) ReadPixels(SKBitmap source, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul, source.ColorSpace);
        using var target = new SKBitmap(info);
        using var scaled = width == source.Width && height == source.Height ? null : Resize(source, width, height, ResampleQuality.Medium);
        using (var src = (scaled ?? source).PeekPixels())
        using (var dst = target.PeekPixels())
            src.ReadPixels(dst);
        var pixels = target.GetPixelSpan().ToArray();
        return (pixels, width, height);
    }

    public static SKBlendMode ToSkia(BlendMode mode) => mode switch
    {
        BlendMode.Normal => SKBlendMode.SrcOver,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        BlendMode.Plus => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };
}

/// <summary>A bitmap could not be allocated (too large for the limits or for memory); mapped to <c>imaging.too_large</c>.</summary>
internal sealed class AllocationFailedException(int width, int height)
    : Exception($"Could not allocate a {width}×{height} bitmap; it is beyond the supported size or the available memory.");
