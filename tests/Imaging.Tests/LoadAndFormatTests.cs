using CL.Imaging;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class LoadAndFormatTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, ImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png, ImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp, ImageFormat.Webp)]
    public void Decodes_common_formats(SKEncodedImageFormat skFormat, ImageFormat expected)
    {
        using var doc = ImageDocument.Load(Solid(40, 30, skFormat, SKColors.Green)).Value!;
        Assert.Equal((40, 30), (doc.Width, doc.Height));
        Assert.Equal(expected, doc.SourceFormat);
        AssertColor(new Color(0, 128, 0), doc.GetPixel(20, 15), tolerance: 6);
    }

    [Fact]
    public void Decodes_bmp_gif_and_ico()
    {
        // Minimal hand-written files: 2×1 24-bit BMP, 1×1 GIF, and an ICO wrapping a PNG.
        byte[] bmp =
        [
            (byte)'B', (byte)'M', 62, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 255, 255, 0, 0, 0, 0,   // BGR red, BGR blue, row padding
        ];
        using (var doc = ImageDocument.Load(bmp).Value!)
        {
            Assert.Equal(ImageFormat.Bmp, doc.SourceFormat);
            AssertColor(Color.Red, doc.GetPixel(0, 0));
            AssertColor(Color.Blue, doc.GetPixel(1, 0));
        }

        byte[] gif =
        [
            .. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0, 0, 255, 0, 0, 0, 0, 0,          // header, 2-colour table: red, black
            0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0, 0x3B,         // image, LZW data, trailer
        ];
        using (var doc = ImageDocument.Load(gif).Value!)
        {
            Assert.Equal(ImageFormat.Gif, doc.SourceFormat);
            AssertColor(Color.Red, doc.GetPixel(0, 0));
        }

        var png = Solid(16, 16, SKEncodedImageFormat.Png, SKColors.Blue);
        byte[] ico =
        [
            0, 0, 1, 0, 1, 0,
            16, 16, 0, 0, 1, 0, 32, 0, .. BitConverter.GetBytes(png.Length), 22, 0, 0, 0,
            .. png,
        ];
        using (var doc = ImageDocument.Load(ico).Value!)
        {
            Assert.Equal(ImageFormat.Ico, doc.SourceFormat);
            Assert.Equal((16, 16), (doc.Width, doc.Height));
            AssertColor(Color.Blue, doc.GetPixel(8, 8));
        }
    }

    [Fact]
    public void Heif_and_avif_signatures_are_recognised_and_reported_honestly()
    {
        byte[] heic = [0, 0, 0, 24, .. "ftyp"u8, .. "heic"u8, 0, 0, 0, 0, .. "mif1"u8, .. "heic"u8, 0, 0, 0, 0];
        byte[] avif = [0, 0, 0, 24, .. "ftyp"u8, .. "avif"u8, 0, 0, 0, 0, .. "mif1"u8, .. "avif"u8, 0, 0, 0, 0];
        Assert.Equal(ImageFormat.Heif, ImageFormatExtensions.Detect(heic));
        Assert.Equal(ImageFormat.Avif, ImageFormatExtensions.Detect(avif));
        // Truncated containers: whatever the platform supports, they never decode, and the error names the format.
        foreach (var data in new[] { heic, avif })
        {
            var error = ImageDocument.Load(data).Error!;
            Assert.Contains(error.Code, new[] { "imaging.unsupported_format", "imaging.decode_failed" });
        }
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Webp)]
    public void Round_trips_through_every_encoder(ImageFormat format)
    {
        using var source = Quadrants(64, 48);
        using var encoded = source.Encode(format, 95).Value!;
        Assert.Equal(format, encoded.Format);
        Assert.Equal(format, ImageFormatExtensions.Detect(encoded.Span));
        using var decoded = ImageDocument.Load(encoded.Span).Value!;
        Assert.Equal((64, 48), (decoded.Width, decoded.Height));
        var tolerance = format == ImageFormat.Png ? 0 : 12;
        AssertColor(Color.Red, decoded.GetPixel(8, 8), tolerance);
        AssertColor(Color.Green, decoded.GetPixel(56, 8), tolerance);
        AssertColor(Color.Blue, decoded.GetPixel(8, 40), tolerance);
        AssertColor(Color.White, decoded.GetPixel(56, 40), tolerance);
    }

    [Fact]
    public void Lossless_webp_is_exact_and_keeps_alpha()
    {
        using var source = Scene(80, 60);
        using var withAlpha = source.Edit().Pad(5).Apply().Value!;   // transparent frame, opaque centre
        using var encoded = withAlpha.Encode(new ImageEncodeOptions { Format = ImageFormat.Webp, Lossless = true }).Value!;
        using var decoded = ImageDocument.Load(encoded.Span).Value!;
        using var comparison = CL.Imaging.Analysis.ImageCompare.Difference(withAlpha, decoded, new() { Tolerance = 0 }).Value!;
        Assert.Equal(0, comparison.DifferentPixels);
        Assert.Equal(0, decoded.GetPixel(0, 0).A);
    }

    [Fact]
    public void Jpeg_flattens_transparency_onto_the_background()
    {
        using var transparent = SolidDoc(20, 20, Color.Transparent);
        using var white = transparent.Encode(ImageFormat.Jpeg, 95).Value!;
        AssertColor(Color.White, ImageDocument.Load(white.Span).Value!.GetPixel(10, 10), 4);
        using var black = transparent.Encode(new ImageEncodeOptions { Format = ImageFormat.Jpeg, Background = Color.Black }).Value!;
        AssertColor(Color.Black, ImageDocument.Load(black.Span).Value!.GetPixel(10, 10), 4);
    }

    [Theory]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Heif)]
    [InlineData(ImageFormat.Unknown)]
    public void Encoding_other_formats_is_unsupported(ImageFormat format)
    {
        using var doc = SolidDoc(4, 4, Color.Red);
        Assert.Equal("imaging.unsupported_format", doc.Encode(format).Error?.Code);
    }

    [Fact]
    public void Probe_reads_the_header_only()
    {
        var png = Solid(10_000, 5_000, SKEncodedImageFormat.Png);
        var info = ImageInfo.Probe(new MemoryStream(png)).Value!;
        Assert.Equal((ImageFormat.Png, 10_000, 5_000), (info.Format, info.Width, info.Height));
        Assert.Equal(50_000_000, info.PixelCount);
        Assert.Equal("image/png", info.MimeType);

        var jpeg = JpegWithOrientation(300, 200, 6);
        var oriented = ImageInfo.Probe(jpeg).Value!;
        Assert.Equal(ImageOrientation.Rotate90, oriented.Orientation);
        Assert.Equal((200, 300), (oriented.DisplayWidth, oriented.DisplayHeight));
        Assert.False(oriented.HasAlphaChannel);

        // Non-seekable streams work too, and seekable streams are left where they were.
        Assert.Equal(300, ImageInfo.Probe(new UploadTests.NonSeekableStream(jpeg)).Value!.Width);
        var stream = new MemoryStream(jpeg);
        ImageInfo.Probe(stream);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Probe_file_and_load_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-imaging-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Solid(321, 123, SKEncodedImageFormat.Png));
        try
        {
            var info = ImageInfo.ProbeFile(path).Value!;
            Assert.Equal((321, 123), (info.Width, info.Height));
            using var doc = ImageDocument.LoadFile(path).Value!;
            Assert.Equal(321, doc.Width);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal("imaging.file_not_found", ImageInfo.ProbeFile(path).Error?.Code);
        Assert.Equal("imaging.file_not_found", ImageDocument.LoadFile(path).Error?.Code);
    }

    [Fact]
    public async Task Load_async_respects_cancellation_and_limits()
    {
        var png = Solid(100, 100, SKEncodedImageFormat.Png);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await ImageDocument.LoadAsync(new MemoryStream(png), ct: cts.Token);
        Assert.Equal("imaging.cancelled", cancelled.Error?.Code);
        Assert.IsType<CodeLogic.Core.Results.UnavailableError>(cancelled.Error);

        Assert.Equal("imaging.too_large", (await ImageDocument.LoadAsync(new MemoryStream(png), new ImageLoadOptions { MaxPixels = 9_999 })).Error?.Code);
        using var ok = (await ImageDocument.LoadAsync(new MemoryStream(png), new ImageLoadOptions { MaxPixels = 10_000 })).Value!;
        Assert.Equal(100, ok.Width);
    }

    [Fact]
    public void Truncated_images_fail_to_decode()
    {
        var png = Solid(200, 200, SKEncodedImageFormat.Png);
        var error = ImageDocument.Load(png.AsSpan(0, png.Length / 2)).Error;
        Assert.Equal("imaging.decode_failed", error?.Code);
    }

    [Fact]
    public void Formats_know_their_mime_types_and_extensions()
    {
        Assert.Equal("image/webp", ImageFormat.Webp.MimeType());
        Assert.Equal(".jpg", ImageFormat.Jpeg.FileExtension());
        Assert.True(ImageFormat.Png.CanEncode());
        Assert.False(ImageFormat.Gif.CanEncode());
        Assert.False(ImageFormat.Jpeg.SupportsAlpha());
    }

    [Fact]
    public void Colors_parse_and_format_as_hex()
    {
        Assert.True(Color.TryParseHex("#ff8000", out var orange));
        Assert.Equal(new Color(255, 128, 0), orange);
        Assert.True(Color.TryParseHex("0F0", out var green));
        Assert.Equal(Color.Green, green);
        Assert.True(Color.TryParseHex("#11223344", out var withAlpha));
        Assert.Equal("#11223344", withAlpha.ToHex());
        Assert.False(Color.TryParseHex("#12345", out _));
    }

    [Fact]
    public void Create_validates_its_size()
    {
        Assert.Equal("imaging.invalid_argument", ImageDocument.Create(0, 10, Color.Red).Error?.Code);
        Assert.Equal("imaging.invalid_argument", ImageDocument.Create(40_000, 10, Color.Red).Error?.Code);
        using var doc = ImageDocument.Create(3, 2, Color.Red).Value!;
        AssertColor(Color.Red, doc.GetPixel(2, 1), 0);
    }
}
