using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Common.Imaging;

/// <summary>Options for <see cref="CLU_Imaging.ProcessImage"/>.</summary>
public sealed class ImageProcessOptions
{
    /// <summary>Output format. Default: <see cref="ImageFormat.Webp"/>.</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Webp;

    /// <summary>Encoder quality, 1–100 (lossy formats). Default: 85.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>Largest output width; the image is scaled down (never up) to fit. 0 = no limit.</summary>
    public int MaxWidth { get; init; }

    /// <summary>Largest output height; the image is scaled down (never up) to fit. 0 = no limit.</summary>
    public int MaxHeight { get; init; }

    /// <summary>
    /// Largest source image, in pixels, that is decoded at all; checked from the header before decoding, so a
    /// small file that claims a huge size is refused instead of allocating gigabytes. Default:
    /// <see cref="CLU_Imaging.DefaultMaxPixels"/>.
    /// </summary>
    public long MaxPixels { get; init; } = CLU_Imaging.DefaultMaxPixels;

    /// <summary>Turn the image upright from its EXIF orientation (phone photos). Default: true.</summary>
    public bool ApplyOrientation { get; init; } = true;
}

/// <summary>An encoded image and its size in pixels.</summary>
/// <param name="Data">The encoded bytes.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Format">The format of <paramref name="Data"/>.</param>
public sealed record ProcessedImage(byte[] Data, int Width, int Height, ImageFormat Format);

public static partial class CLU_Imaging
{
    /// <summary>Default <see cref="ImageProcessOptions.MaxPixels"/>: 40 megapixels (about 160 MB once decoded).</summary>
    public const long DefaultMaxPixels = 40_000_000;

    /// <summary>
    /// Decodes an image safely and re-encodes it: refuses images over <see cref="ImageProcessOptions.MaxPixels"/>
    /// before decoding, applies the EXIF orientation, scales into <see cref="ImageProcessOptions.MaxWidth"/> ×
    /// <see cref="ImageProcessOptions.MaxHeight"/> and encodes to <see cref="ImageProcessOptions.Format"/>.
    /// Re-encoding keeps only pixels, so metadata such as GPS positions is dropped. Animated images keep their
    /// first frame. Meant for user uploads.
    /// </summary>
    /// <param name="imageStream">A readable stream with the source image. Must not be null.</param>
    /// <param name="options">Options; null uses the defaults (WebP, quality 85, no size box).</param>
    /// <returns>
    /// The encoded image, or a failure: <c>imaging.decode_failed</c> (not an image),
    /// <c>imaging.too_large</c> (over the pixel limit), <c>imaging.encode_failed</c>, <c>imaging.process_failed</c>.
    /// </returns>
    public static Result<ProcessedImage> ProcessImage(Stream imageStream, ImageProcessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        options ??= new ImageProcessOptions();
        if (options.Quality is < 1 or > 100)
            return Result<ProcessedImage>.Failure(Error.Validation("imaging.invalid_options", "Quality must be between 1 and 100."));
        try
        {
            var decoded = Decode(imageStream, options.MaxPixels, options.ApplyOrientation);
            if (decoded.IsFailure)
                return Result<ProcessedImage>.Failure(decoded.Error!);
            using var source = decoded.Value!;
            var (width, height) = FitWithin(source.Width, source.Height, options.MaxWidth, options.MaxHeight);
            using var scaled = width == source.Width && height == source.Height
                ? null
                : source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            var output = scaled ?? source;
            using var image = SKImage.FromBitmap(output);
            using var data = image.Encode(ToSkiaFormat(options.Format), options.Quality);
            return data is null
                ? Result<ProcessedImage>.Failure(Error.Internal("imaging.encode_failed", "Failed to encode image to target format."))
                : Result<ProcessedImage>.Success(new ProcessedImage(data.ToArray(), output.Width, output.Height, options.Format));
        }
        catch (Exception ex)
        {
            return Result<ProcessedImage>.Failure(Error.FromException(ex, "imaging.process_failed"));
        }
    }

    /// <summary>The size of a <paramref name="width"/>×<paramref name="height"/> image scaled down (never up) to fit the box (0 = no limit on that side).</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var scale = 1d;
        if (maxWidth > 0) scale = Math.Min(scale, (double)maxWidth / width);
        if (maxHeight > 0) scale = Math.Min(scale, (double)maxHeight / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Reads the stored size from the header without decoding the pixels; null when it is not an image.</summary>
    private static (int Width, int Height, SKEncodedOrigin Origin)? ReadHeader(Stream imageStream)
    {
        using var codec = SKCodec.Create(imageStream);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
    }

    /// <summary>Decodes after checking the pixel count from the header; optionally turns the bitmap upright.</summary>
    private static Result<SKBitmap> Decode(Stream imageStream, long maxPixels, bool applyOrientation)
    {
        using var data = SKData.Create(imageStream);
        using var codec = data is null ? null : SKCodec.Create(data);
        if (codec is null)
            return Result<SKBitmap>.Failure(Error.Validation("imaging.decode_failed", "Failed to decode source image."));
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            return Result<SKBitmap>.Failure(Error.Validation("imaging.decode_failed", "Failed to decode source image."));
        if (maxPixels > 0 && (long)info.Width * info.Height > maxPixels)
            return Result<SKBitmap>.Failure(Error.Validation("imaging.too_large",
                $"The image is {info.Width}×{info.Height} pixels; the limit is {maxPixels:N0} pixels."));

        var bitmap = SKBitmap.Decode(codec, info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul));
        if (bitmap is null)
            return Result<SKBitmap>.Failure(Error.Validation("imaging.decode_failed", "Failed to decode source image."));
        if (!applyOrientation || codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return Result<SKBitmap>.Success(bitmap);
        using (bitmap)
            return Result<SKBitmap>.Success(Orient(bitmap, codec.EncodedOrigin));
    }

    /// <summary>A copy of <paramref name="source"/> turned upright for its EXIF <paramref name="origin"/>.</summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return source.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, result.Width / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, result.Width / 2f, result.Height / 2f); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, result.Height / 2f); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, source.Height / 2f); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(result.Width, 0); canvas.RotateDegrees(90); canvas.Scale(-1, 1, source.Width / 2f, 0); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, result.Height); canvas.RotateDegrees(270); break;
        }
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return result;
    }
}
