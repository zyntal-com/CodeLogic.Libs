using SkiaSharp;

namespace CL.Imaging;

/// <summary>MIME types, file extensions, capability checks and signature sniffing for <see cref="ImageFormat"/>.</summary>
public static class ImageFormatExtensions
{
    /// <summary>The number of leading bytes <see cref="Detect"/> looks at.</summary>
    public const int SignatureLength = 32;

    /// <summary>The MIME type, e.g. <c>image/webp</c>; <c>application/octet-stream</c> for <see cref="ImageFormat.Unknown"/>.</summary>
    /// <param name="format">The format.</param>
    public static string MimeType(this ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.Webp => "image/webp",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.Ico => "image/x-icon",
        ImageFormat.Heif => "image/heif",
        ImageFormat.Avif => "image/avif",
        ImageFormat.Unknown => "application/octet-stream",
        _ => "application/octet-stream",
    };

    /// <summary>The usual file extension with the dot, e.g. <c>.jpg</c>; empty for <see cref="ImageFormat.Unknown"/>.</summary>
    /// <param name="format">The format.</param>
    public static string FileExtension(this ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Webp => ".webp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Ico => ".ico",
        ImageFormat.Heif => ".heic",
        ImageFormat.Avif => ".avif",
        ImageFormat.Unknown => "",
        _ => "",
    };

    /// <summary>Whether CL.Imaging can encode to the format (JPEG, PNG, WebP).</summary>
    /// <param name="format">The format.</param>
    public static bool CanEncode(this ImageFormat format) => format is ImageFormat.Jpeg or ImageFormat.Png or ImageFormat.Webp;

    /// <summary>Whether the format can carry an alpha channel when encoded.</summary>
    /// <param name="format">The format.</param>
    public static bool SupportsAlpha(this ImageFormat format) => format is not (ImageFormat.Jpeg or ImageFormat.Unknown);

    /// <summary>Detects the format from the file signature (the first <see cref="SignatureLength"/> bytes are enough).</summary>
    /// <param name="header">The leading bytes of the file.</param>
    /// <returns>The format, or <see cref="ImageFormat.Unknown"/>.</returns>
    public static ImageFormat Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ImageFormat.Jpeg;
        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ImageFormat.Png;
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) return ImageFormat.Gif;
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageFormat.Webp;
        if (header.Length >= 14 && header.StartsWith("BM"u8)) return ImageFormat.Bmp;
        if (header.Length >= 6 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0 && (header[4] | header[5]) != 0)
            return ImageFormat.Ico;
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var boxSize = (int)Math.Min(header.Length, (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]));
            ImageFormat found = ImageFormat.Unknown;
            // Major brand at 8, then minor version, then compatible brands from 16.
            for (var offset = 8; offset + 4 <= boxSize; offset += offset == 8 ? 8 : 4)
            {
                var brand = header.Slice(offset, 4);
                if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return ImageFormat.Avif;
                if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
                    || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8)
                    || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                    found = ImageFormat.Heif;
            }
            return found;
        }
        return ImageFormat.Unknown;
    }

    internal static ImageFormat FromSkia(SKEncodedImageFormat format) => format switch
    {
        SKEncodedImageFormat.Jpeg => ImageFormat.Jpeg,
        SKEncodedImageFormat.Png => ImageFormat.Png,
        SKEncodedImageFormat.Webp => ImageFormat.Webp,
        SKEncodedImageFormat.Gif => ImageFormat.Gif,
        SKEncodedImageFormat.Bmp => ImageFormat.Bmp,
        SKEncodedImageFormat.Ico => ImageFormat.Ico,
        SKEncodedImageFormat.Heif => ImageFormat.Heif,
        SKEncodedImageFormat.Avif => ImageFormat.Avif,
        _ => ImageFormat.Unknown,
    };
}
