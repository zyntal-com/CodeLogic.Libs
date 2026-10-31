using CL.Imaging.Errors;
using CL.Imaging.Metadata;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging.Internal;

/// <summary>Decodes encoded bytes into an <see cref="ImageDocument"/> after checking the header against the limits.</summary>
internal static class Decoder
{
    public static Result<ImageDocument> Decode(ReadOnlySpan<byte> data, long maxPixels, bool readMetadata, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (data.IsEmpty) return ImagingErrors.DecodeFailed("The image data is empty.");
        var sniffed = ImageFormatExtensions.Detect(data[..Math.Min(data.Length, ImageFormatExtensions.SignatureLength)]);

        using var skData = SKData.CreateCopy(data);
        using var codec = skData is null ? null : SKCodec.Create(skData);
        if (codec is null) return Unreadable(sniffed);

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return ImagingErrors.DecodeFailed("The image has no pixels.");
        if (maxPixels > 0 && (long)info.Width * info.Height > maxPixels)
            return ImagingErrors.TooLarge($"The image is {info.Width}×{info.Height} pixels; the limit is {maxPixels:N0} pixels.");
        if (info.Width > SkiaOps.MaxDimension || info.Height > SkiaOps.MaxDimension || (long)info.Width * info.Height > SkiaOps.MaxOutputPixels)
            return ImagingErrors.TooLarge($"The image is {info.Width}×{info.Height} pixels, beyond what CL.Imaging can hold.");

        var format = ImageFormatExtensions.FromSkia(codec.EncodedFormat);
        if (format == ImageFormat.Unknown) format = sniffed;

        // Keep the pixels in the source colour space (untagged when sRGB): nothing is converted until encoding,
        // so the original ICC profile can be written back unchanged when asked.
        var colorSpace = info.ColorSpace is { IsSrgb: false } cs ? cs : null;
        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);

        ct.ThrowIfCancellationRequested();
        var bitmap = new SKBitmap();
        try
        {
            if (!bitmap.TryAllocPixels(target))
                return ImagingErrors.TooLarge($"Could not allocate memory for a {info.Width}×{info.Height} image.");
            var result = codec.GetPixels(target, bitmap.GetPixels());
            if (result != SKCodecResult.Success)
                return ImagingErrors.DecodeFailed(result == SKCodecResult.IncompleteInput
                    ? "The image data is truncated."
                    : "The image data could not be decoded.", result.ToString());

            var blocks = readMetadata ? ContainerReader.Extract(data, format) : default;
            var metadata = BuildMetadata(format, info.Width, info.Height, blocks, codec.EncodedOrigin);
            var document = new ImageDocument(bitmap, format, metadata.Orientation, metadata, blocks.Exif, blocks.Icc);
            bitmap = null!;
            return document;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    public static ImageMetadata BuildMetadata(ImageFormat format, int width, int height, MetadataBlocks blocks, SKEncodedOrigin origin)
    {
        var metadata = new ImageMetadata { Format = format, Width = width, Height = height, HasIccProfile = blocks.Icc is { Length: > 0 } };
        if (ExifReader.Parse(blocks.Exif) is { } exif) metadata = exif.Apply(metadata);
        // Skia knows the orientation for containers this reader does not parse (e.g. HEIF).
        if (metadata.Orientation == ImageOrientation.Normal && origin is not (SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default))
            metadata = metadata with { Orientation = (ImageOrientation)(int)origin };
        return metadata;
    }

    public static Error Unreadable(ImageFormat sniffed) => sniffed switch
    {
        ImageFormat.Heif or ImageFormat.Avif => ImagingErrors.UnsupportedFormat(
            $"{sniffed} images cannot be decoded on this platform (its Skia build has no {sniffed} codec)."),
        ImageFormat.Unknown => ImagingErrors.DecodeFailed("The data is not a recognised image format."),
        _ => ImagingErrors.DecodeFailed($"The {sniffed} data could not be decoded (corrupt or truncated)."),
    };
}
