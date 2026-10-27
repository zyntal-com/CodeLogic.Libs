using CL.Common.Imaging;
using SkiaSharp;
using Xunit;

namespace Common.Tests;

/// <summary>
/// CLU_Imaging is used on uploads: it must refuse images that would take huge amounts of memory before decoding
/// them, turn phone photos upright, and keep the aspect ratio when asked to.
/// </summary>
public sealed class ImagingTests
{
    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, SKColor? color = null, bool redCorner = false)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.OrangeRed);
        if (redCorner)
        {
            bitmap.Erase(SKColors.Blue);
            using var canvas = new SKCanvas(bitmap);
            canvas.DrawRect(0, 0, 60, 60, new SKPaint { Color = SKColors.Red });
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A JPEG with an EXIF APP1 segment carrying <paramref name="orientation"/> (big-endian TIFF, one IFD entry).</summary>
    private static byte[] JpegWithOrientation(int width, int height, ushort orientation)
    {
        var jpeg = Encode(width, height, SKEncodedImageFormat.Jpeg, redCorner: true);
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // header, IFD at 8
            0x00, 0x01,                                               // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01,           // Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,  // value
            0x00, 0x00, 0x00, 0x00                                    // no next IFD
        ];
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var length = payload.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    [Fact]
    public void Process_image_scales_into_the_box_as_webp()
    {
        var result = CLU_Imaging.ProcessImage(new MemoryStream(Encode(1200, 600, SKEncodedImageFormat.Png)),
            new ImageProcessOptions { MaxWidth = 512, MaxHeight = 512 });
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal((512, 256), (result.Value!.Width, result.Value.Height));
        Assert.Equal(ImageFormat.Webp, result.Value.Format);
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(result.Value.Data, 8, 4));
    }

    [Fact]
    public void Process_image_never_scales_up()
    {
        var result = CLU_Imaging.ProcessImage(new MemoryStream(Encode(300, 200, SKEncodedImageFormat.Png)),
            new ImageProcessOptions { MaxWidth = 2000, MaxHeight = 2000, Format = ImageFormat.Png });
        Assert.Equal((300, 200), (result.Value!.Width, result.Value.Height));
    }

    [Fact]
    public void Huge_images_are_refused_before_decoding()
    {
        // 10,000 × 5,000 = 50 megapixels: a small PNG that would decode to 200 MB.
        var png = Encode(10_000, 5_000, SKEncodedImageFormat.Png);
        var processed = CLU_Imaging.ProcessImage(new MemoryStream(png));
        Assert.Equal("imaging.too_large", processed.Error?.Code);
        Assert.Equal("imaging.too_large", CLU_Imaging.ConvertImage(new MemoryStream(png), ImageFormat.Webp).Error?.Code);
        Assert.Equal("imaging.too_large", CLU_Imaging.ResizeImage(new MemoryStream(png), 100, 100).Error?.Code);
        Assert.True(CLU_Imaging.ProcessImage(new MemoryStream(png), new ImageProcessOptions { MaxPixels = 60_000_000, MaxWidth = 100 }).IsSuccess);
    }

    [Fact]
    public void Exif_orientation_turns_photos_upright()
    {
        // Orientation 6: stored 300×200, shown rotated 90° clockwise, so upright it is 200×300.
        var jpeg = JpegWithOrientation(300, 200, 6);
        var upright = CLU_Imaging.ProcessImage(new MemoryStream(jpeg), new ImageProcessOptions { Format = ImageFormat.Png });
        Assert.Equal((200, 300), (upright.Value!.Width, upright.Value.Height));

        var asStored = CLU_Imaging.ProcessImage(new MemoryStream(jpeg), new ImageProcessOptions { Format = ImageFormat.Png, ApplyOrientation = false });
        Assert.Equal((300, 200), (asStored.Value!.Width, asStored.Value.Height));

        using var converted = CLU_Imaging.ConvertImage(new MemoryStream(jpeg), ImageFormat.Png).Value!;
        using var decoded = SKBitmap.Decode(converted);
        Assert.Equal((200, 300), (decoded.Width, decoded.Height));
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
        var result = CLU_Imaging.ProcessImage(new MemoryStream(JpegWithOrientation(300, 200, orientation)), new ImageProcessOptions { Format = ImageFormat.Png });
        using var bitmap = SKBitmap.Decode(result.Value!.Data);
        var swapped = orientation >= 5;
        Assert.Equal(swapped ? (200, 300) : (300, 200), (bitmap.Width, bitmap.Height));
        var x = corner[1] == 'L' ? 10 : bitmap.Width - 11;
        var y = corner[0] == 'T' ? 10 : bitmap.Height - 11;
        var pixel = bitmap.GetPixel(x, y);
        Assert.True(pixel.Red > 180 && pixel.Blue < 80, $"expected red at {corner}, got {pixel}");
    }

    [Fact]
    public void Garbage_is_refused()
    {
        var garbage = "not an image"u8.ToArray();
        Assert.Equal("imaging.decode_failed", CLU_Imaging.ProcessImage(new MemoryStream(garbage)).Error?.Code);
        Assert.Equal("imaging.decode_failed", CLU_Imaging.ConvertImage(new MemoryStream(garbage), ImageFormat.Png).Error?.Code);
        Assert.False(CLU_Imaging.IsValidAspectRatio(new MemoryStream(garbage)));
    }

    [Fact]
    public void Resize_without_crop_keeps_the_aspect_ratio()
    {
        using var fitted = CLU_Imaging.ResizeImage(new MemoryStream(Encode(1000, 500, SKEncodedImageFormat.Png)), 200, 200, allowCrop: false).Value!;
        using var bitmap = SKBitmap.Decode(fitted);
        Assert.Equal((200, 100), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Resize_with_crop_fills_the_exact_size()
    {
        using var cropped = CLU_Imaging.ResizeImage(new MemoryStream(Encode(1000, 500, SKEncodedImageFormat.Png)), 200, 200).Value!;
        using var bitmap = SKBitmap.Decode(cropped);
        Assert.Equal((200, 200), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Resize_can_encode_other_formats()
    {
        using var webp = CLU_Imaging.ResizeImage(new MemoryStream(Encode(400, 400, SKEncodedImageFormat.Png)), 100, 100, allowCrop: true, ImageFormat.Webp, 80).Value!;
        var bytes = webp.ToArray();
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
    }

    [Fact]
    public void Size_checks_read_the_header()
    {
        var png = Encode(10_000, 5_000, SKEncodedImageFormat.Png);
        Assert.False(CLU_Imaging.IsValidSize(new MemoryStream(png), 4000, 4000));
        Assert.True(CLU_Imaging.IsValidSize(new MemoryStream(png), 10_000, 5_000));
        Assert.True(CLU_Imaging.IsValidAspectRatio(new MemoryStream(png)));
    }

    [Fact]
    public void Dimensions_of_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-imaging-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Encode(321, 123, SKEncodedImageFormat.Png));
        try
        {
            Assert.Equal((321, 123), CLU_Imaging.GetImageDimensions(path).Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Quality_must_be_in_range(int quality) =>
        Assert.Equal("imaging.invalid_options",
            CLU_Imaging.ProcessImage(new MemoryStream(Encode(10, 10, SKEncodedImageFormat.Png)), new ImageProcessOptions { Quality = quality }).Error?.Code);
}
