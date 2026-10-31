using CL.Imaging;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

/// <summary>
/// Ported from CL.Common's ImagingTests: uploads must be refused before decoding when they would take huge amounts of
/// memory, phone photos must turn upright, and the aspect ratio must be kept.
/// </summary>
public sealed class UploadTests
{
    [Fact]
    public void Process_upload_scales_into_the_box_as_webp()
    {
        using var result = ImageTools.ProcessUpload(new MemoryStream(Solid(1200, 600, SKEncodedImageFormat.Png)),
            new UploadOptions { MaxWidth = 512, MaxHeight = 512 }).Value!;
        Assert.Equal((512, 256), (result.Width, result.Height));
        Assert.Equal(ImageFormat.Webp, result.Format);
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(result.Span.Slice(8, 4)));
    }

    [Fact]
    public void Process_upload_never_scales_up()
    {
        using var result = ImageTools.ProcessUpload(new MemoryStream(Solid(300, 200, SKEncodedImageFormat.Png)),
            new UploadOptions { MaxWidth = 2000, MaxHeight = 2000, Format = ImageFormat.Png }).Value!;
        Assert.Equal((300, 200), (result.Width, result.Height));
    }

    [Fact]
    public async Task Huge_images_are_refused_before_decoding()
    {
        // 10,000 × 5,000 = 50 megapixels: a small PNG that would decode to 200 MB.
        var png = Solid(10_000, 5_000, SKEncodedImageFormat.Png);
        Assert.Equal("imaging.too_large", ImageTools.ProcessUpload(new MemoryStream(png)).Error?.Code);
        Assert.Equal("imaging.too_large", (await ImageTools.ProcessUploadAsync(new MemoryStream(png))).Error?.Code);
        Assert.Equal("imaging.too_large", ImageDocument.Load(png).Error?.Code);
        using var allowed = ImageTools.ProcessUpload(new MemoryStream(png), new UploadOptions { MaxPixels = 60_000_000, MaxWidth = 100 }).Value!;
        Assert.Equal(100, allowed.Width);
    }

    [Fact]
    public void Uploads_over_the_byte_limit_are_refused()
    {
        var png = Solid(200, 200, SKEncodedImageFormat.Png);
        Assert.Equal("imaging.too_large", ImageTools.ProcessUpload(new MemoryStream(png), new UploadOptions { MaxBytes = 100 }).Error?.Code);
        // A non-seekable stream is cut off while reading.
        Assert.Equal("imaging.too_large", ImageTools.ProcessUpload(new NonSeekableStream(png), new UploadOptions { MaxBytes = 100 }).Error?.Code);
    }

    [Fact]
    public void Exif_orientation_turns_photos_upright()
    {
        // Orientation 6: stored 300×200, shown rotated 90° clockwise, so upright it is 200×300.
        var jpeg = JpegWithOrientation(300, 200, 6);
        using var upright = ImageTools.ProcessUpload(new MemoryStream(jpeg), new UploadOptions { Format = ImageFormat.Png }).Value!;
        Assert.Equal((200, 300), (upright.Width, upright.Height));

        using var asStored = ImageTools.ProcessUpload(new MemoryStream(jpeg), new UploadOptions { Format = ImageFormat.Png, ApplyOrientation = false }).Value!;
        Assert.Equal((300, 200), (asStored.Width, asStored.Height));
    }

    [Theory]
    [InlineData(1, "TL")]
    [InlineData(2, "TR")]
    [InlineData(3, "BR")]
    [InlineData(4, "BL")]
    [InlineData(5, "TL")]
    [InlineData(6, "TR")]
    [InlineData(7, "BR")]
    [InlineData(8, "BL")]
    public void Every_exif_orientation_lands_the_stored_corner_where_it_is_shown(ushort orientation, string corner)
    {
        // The stored top-left corner is red; EXIF says where that corner is shown.
        using var result = ImageTools.ProcessUpload(new MemoryStream(JpegWithOrientation(300, 200, orientation)), new UploadOptions { Format = ImageFormat.Png }).Value!;
        using var bitmap = SKBitmap.Decode(result.ToArray());
        var swapped = orientation >= 5;
        Assert.Equal(swapped ? (200, 300) : (300, 200), (bitmap.Width, bitmap.Height));
        var x = corner[1] == 'L' ? 10 : bitmap.Width - 11;
        var y = corner[0] == 'T' ? 10 : bitmap.Height - 11;
        var pixel = bitmap.GetPixel(x, y);
        Assert.True(pixel.Red > 180 && pixel.Blue < 80, $"expected red at {corner}, got {pixel}");
    }

    [Fact]
    public void Uploads_drop_gps_and_all_exif()
    {
        var tiff = new TiffBuilder().Ascii(0x010F, "Canon").Gps().Ascii(1, "N").Rationals(2, (55, 1), (40, 1), (0, 1)).Ascii(3, "E").Rationals(4, (12, 1), (34, 1), (0, 1)).Build();
        var jpeg = InsertJpegApp1(Solid(64, 64, SKEncodedImageFormat.Jpeg), tiff);
        Assert.NotNull(ImageMetadataReader.Read(jpeg).Value!.Gps);
        foreach (var format in new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Webp })
        {
            using var processed = ImageTools.ProcessUpload(new MemoryStream(jpeg), new UploadOptions { Format = format }).Value!;
            var metadata = ImageMetadataReader.Read(processed.Span).Value!;
            Assert.False(metadata.HasExif);
            Assert.Null(metadata.Gps);
        }
    }

    [Fact]
    public void Garbage_is_refused()
    {
        var garbage = "not an image"u8.ToArray();
        Assert.Equal("imaging.decode_failed", ImageTools.ProcessUpload(new MemoryStream(garbage)).Error?.Code);
        Assert.Equal("imaging.decode_failed", ImageDocument.Load(garbage).Error?.Code);
        Assert.Equal("imaging.decode_failed", ImageInfo.Probe(new MemoryStream(garbage)).Error?.Code);
        Assert.Equal("imaging.decode_failed", ImageDocument.Load(ReadOnlySpan<byte>.Empty).Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Quality_must_be_in_range(int quality) =>
        Assert.Equal("imaging.invalid_argument",
            ImageTools.ProcessUpload(new MemoryStream(Solid(10, 10, SKEncodedImageFormat.Png)), new UploadOptions { Quality = quality }).Error?.Code);

    [Fact]
    public void Fit_within_never_scales_up()
    {
        Assert.Equal((512, 256), ImageTools.FitWithin(1024, 512, 512, 512));
        Assert.Equal((100, 50), ImageTools.FitWithin(100, 50, 0, 0));
        Assert.Equal((50, 25), ImageTools.FitWithin(100, 50, 0, 25));
    }

    internal sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
