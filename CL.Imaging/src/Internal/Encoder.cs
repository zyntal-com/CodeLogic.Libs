using CL.Imaging.Errors;
using CL.Imaging.Metadata;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>Encodes bitmaps to JPEG, PNG or WebP into pooled buffers, writing EXIF/ICC back when asked.</summary>
internal static class Encoder
{
    public static Error? Validate(ImageEncodeOptions options, out ImageFormat format, out int quality)
    {
        var defaults = ImagingDefaults.Current;
        format = options.Format ?? defaults.Format;
        quality = options.Quality ?? defaults.Quality;
        if (!format.CanEncode())
            return ImagingErrors.UnsupportedFormat($"Encoding to {format} is not supported; use JPEG, PNG or WebP.");
        if (quality is < 1 or > 100)
            return ImagingErrors.InvalidArgument("Quality must be between 1 and 100.");
        return null;
    }

    /// <param name="bitmap">The pixels (not modified).</param>
    /// <param name="options">Encoder options.</param>
    /// <param name="exif">The source EXIF block, or null.</param>
    /// <param name="icc">The source ICC profile, or null.</param>
    /// <param name="resetOrientation">Whether the pixels were turned upright (the EXIF orientation is rewritten to 1).</param>
    /// <param name="strip">Whether metadata was stripped in the pipeline (overrides the options).</param>
    public static Result<EncodedImage> Encode(SKBitmap bitmap, ImageEncodeOptions options, byte[]? exif, byte[]? icc, bool resetOrientation, bool strip)
    {
        if (Validate(options, out var format, out var quality) is { } invalid) return invalid;

        var keepIcc = !strip && options.KeepIccProfile;
        var keepExif = !strip && (options.KeepMetadata ?? !ImagingDefaults.Current.StripMetadata) && exif is not null;
        var writeIcc = keepIcc && icc is { Length: > 0 };

        SKBitmap? converted = null;
        try
        {
            var source = bitmap;
            var tagged = bitmap.ColorSpace is { IsSrgb: false };

            // Not keeping the profile: convert the pixels to sRGB so they look right without it.
            if (tagged && !keepIcc)
            {
                converted = Convert(source, SKColorSpace.CreateSrgb(), format == ImageFormat.Jpeg ? options.Background : null);
                source = converted;
            }
            else if (format == ImageFormat.Jpeg && !IsOpaque(source))
            {
                converted = Convert(source, source.ColorSpace, options.Background);
                source = converted;
            }

            // When the original profile is written by hand, Skia must not write its own: encode untagged pixels.
            // When keeping a colour space that has no profile bytes (e.g. PNG gAMA/cHRM), Skia writes one for it.
            var encodeInfo = writeIcc || !(keepIcc && tagged)
                ? source.Info.WithColorSpace(null)
                : source.Info;

            using var pixmap = new SKPixmap(encodeInfo, source.GetPixels(), source.RowBytes);
            using var output = new PooledMemoryStream(EstimateSize(source, format));
            bool ok;
            using (var skStream = new SKManagedWStream(output))
            {
                ok = format switch
                {
                    ImageFormat.Jpeg => pixmap.Encode(skStream, new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore)),
                    ImageFormat.Png => pixmap.Encode(skStream, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
                    ImageFormat.Webp => pixmap.Encode(skStream, new SKWebpEncoderOptions(
                        options.Lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, quality)),
                    _ => false,
                };
                skStream.Flush();
            }
            if (!ok || output.Length == 0)
                return ImagingErrors.EncodeFailed($"The {format} encoder produced no output.");

            if (!keepExif && !writeIcc)
                return new EncodedImage(output.Detach(), format, source.Width, source.Height);

            byte[]? exifToWrite = null;
            if (keepExif)
            {
                exifToWrite = (byte[])exif!.Clone();
                if (resetOrientation) ExifReader.TrySetOrientation(exifToWrite, ImageOrientation.Normal);
            }
            using var withMetadata = new PooledMemoryStream(checked((int)output.Length + (exifToWrite?.Length ?? 0) + (writeIcc ? icc!.Length + 1024 : 0) + 64));
            MetadataWriter.Write(output.WrittenSpan, format, exifToWrite, writeIcc ? icc : null, !IsOpaque(source), withMetadata);
            return new EncodedImage(withMetadata.Detach(), format, source.Width, source.Height);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>A copy in <paramref name="colorSpace"/>, optionally flattened onto an opaque background.</summary>
    private static SKBitmap Convert(SKBitmap source, SKColorSpace? colorSpace, Color? background)
    {
        var result = SkiaOps.Create(source.Width, source.Height, colorSpace);
        using var canvas = new SKCanvas(result);
        if (background is { } bg) canvas.Clear(bg.WithAlpha(255));
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return result;
    }

    /// <summary>Whether every pixel is opaque (scans the alpha channel).</summary>
    public static bool IsOpaque(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return true;
        var span = bitmap.GetPixelSpan();
        for (var i = 3; i < span.Length; i += 4)
            if (span[i] != 255) return false;
        return true;
    }

    private static int EstimateSize(SKBitmap bitmap, ImageFormat format)
    {
        var raw = (long)bitmap.Width * bitmap.Height * 4;
        var estimate = format == ImageFormat.Png ? raw / 2 : raw / 8;
        return (int)Math.Clamp(estimate, 4096, 64L * 1024 * 1024);
    }
}
