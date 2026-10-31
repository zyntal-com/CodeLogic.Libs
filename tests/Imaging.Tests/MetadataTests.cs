using CL.Imaging;
using CL.Imaging.Metadata;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class MetadataTests
{
    private static byte[] CameraExif(bool bigEndian = false) => new TiffBuilder(bigEndian)
        .Ifd0().Ascii(0x010F, "Canon").Ascii(0x0110, "EOS R5").Short(0x0112, 6).Ascii(0x0131, "Darktable").Ascii(0x8298, "(c) Me")
        .Exif()
            .Rationals(0x829A, (1, 250)).Rationals(0x829D, (28, 10)).Short(0x8827, 400)
            .Ascii(0x9003, "2024:06:01 14:30:15").Ascii(0x9011, "+02:00")
            .Rationals(0x920A, (50, 1)).Short(0xA405, 75).Short(0x9209, 1)
            .Ascii(0xA433, "Canon").Ascii(0xA434, "RF 24-70mm F2.8")
        .Gps()
            .Ascii(1, "S").Rationals(2, (33, 1), (51, 1), (3600, 100))
            .Ascii(3, "W").Rationals(4, (151, 1), (12, 1), (0, 1))
            .Byte(5, 0).Rationals(6, (125, 2))
        .Build();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_fields_are_read_in_both_byte_orders(bool bigEndian)
    {
        var jpeg = InsertJpegApp1(Solid(64, 48, SKEncodedImageFormat.Jpeg), CameraExif(bigEndian));
        var m = ImageMetadataReader.Read(jpeg).Value!;
        Assert.True(m.HasExif);
        Assert.Equal(ImageFormat.Jpeg, m.Format);
        Assert.Equal((64, 48), (m.Width, m.Height));
        Assert.Equal("Canon", m.CameraMake);
        Assert.Equal("EOS R5", m.CameraModel);
        Assert.Equal("RF 24-70mm F2.8", m.LensModel);
        Assert.Equal("Canon", m.LensMake);
        Assert.Equal("Darktable", m.Software);
        Assert.NotNull(m.Copyright);
        Assert.Equal(ImageOrientation.Rotate90, m.Orientation);
        Assert.Equal(new DateTime(2024, 6, 1, 14, 30, 15), m.DateTaken);
        Assert.Equal(TimeSpan.FromHours(2), m.DateTakenOffset);
        Assert.Equal(400, m.Iso);
        Assert.Equal(1 / 250.0, m.ExposureTime!.Value, 6);
        Assert.Equal("1/250", m.ExposureTimeText);
        Assert.Equal(2.8, m.FNumber!.Value, 6);
        Assert.Equal(50, m.FocalLength!.Value, 6);
        Assert.Equal(75, m.FocalLength35mm);
        Assert.True(m.FlashFired);
        Assert.NotNull(m.Gps);
        Assert.Equal(-(33 + 51 / 60.0 + 36 / 3600.0), m.Gps!.Latitude, 6);
        Assert.Equal(-(151 + 12 / 60.0), m.Gps.Longitude, 6);
        Assert.Equal(62.5, m.Gps.Altitude!.Value, 6);
    }

    [Fact]
    public void Documents_carry_metadata_and_images_without_exif_have_defaults()
    {
        using var doc = ImageDocument.Load(InsertJpegApp1(Solid(64, 48, SKEncodedImageFormat.Jpeg), CameraExif())).Value!;
        Assert.Equal("Canon", doc.Metadata.CameraMake);
        Assert.False(doc.ExifData.IsEmpty);

        var plain = ImageMetadataReader.Read(Solid(10, 10, SKEncodedImageFormat.Png)).Value!;
        Assert.False(plain.HasExif);
        Assert.Equal(ImageOrientation.Normal, plain.Orientation);
        Assert.Null(plain.CameraMake);
    }

    [Fact]
    public void Malformed_exif_never_fails_the_image()
    {
        byte[] broken = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0xFF, 0xFF, 1, 2, 3];   // claims 65535 entries
        using var doc = ImageDocument.Load(InsertJpegApp1(Solid(10, 10, SKEncodedImageFormat.Jpeg), broken)).Value!;
        Assert.Equal(10, doc.Width);
        // Offsets pointing outside the block or looping back are ignored.
        var looping = new TiffBuilder().Ascii(0x010F, "X").Build();
        looping[^4] = 8;   // "next IFD" is not followed, but make sure odd data does not crash
        Assert.True(ImageMetadataReader.Read(InsertJpegApp1(Solid(10, 10, SKEncodedImageFormat.Jpeg), looping)).IsSuccess);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Webp)]
    public void Exif_is_kept_on_request_and_its_orientation_reset_after_auto_orient(ImageFormat format)
    {
        using var doc = ImageDocument.Load(InsertJpegApp1(Solid(64, 48, SKEncodedImageFormat.Jpeg), CameraExif())).Value!;

        using (var stripped = doc.Edit().AutoOrient().Encode(format).Value!)
            Assert.False(ImageMetadataReader.Read(stripped.Span).Value!.HasExif);    // default: strip

        using var kept = doc.Edit().AutoOrient().Encode(new ImageEncodeOptions { Format = format, KeepMetadata = true }).Value!;
        var m = ImageMetadataReader.Read(kept.Span).Value!;
        Assert.True(m.HasExif);
        Assert.Equal("EOS R5", m.CameraModel);
        Assert.Equal(ImageOrientation.Normal, m.Orientation);
        Assert.Equal((48, 64), (m.Width, m.Height));
        using var reloaded = ImageDocument.Load(kept.Span).Value!;
        Assert.Equal((48, 64), (reloaded.Width, reloaded.Height));

        // Without AutoOrient the stored pixels keep their orientation tag.
        using var asStored = doc.Edit().Encode(new ImageEncodeOptions { Format = format, KeepMetadata = true }).Value!;
        Assert.Equal(ImageOrientation.Rotate90, ImageMetadataReader.Read(asStored.Span).Value!.Orientation);

        // StripMetadata beats KeepMetadata.
        using var forced = doc.Edit().StripMetadata().Encode(new ImageEncodeOptions { Format = format, KeepMetadata = true }).Value!;
        Assert.False(ImageMetadataReader.Read(forced.Span).Value!.HasExif);
    }

    [Fact]
    public void Defaults_decide_whether_exif_is_kept()
    {
        using var doc = ImageDocument.Load(InsertJpegApp1(Solid(16, 16, SKEncodedImageFormat.Jpeg), CameraExif())).Value!;
        var before = ImagingDefaults.Current;
        try
        {
            ImagingDefaults.Current = before with { StripMetadata = false };
            using var kept = doc.Encode(ImageFormat.Jpeg).Value!;
            Assert.True(ImageMetadataReader.Read(kept.Span).Value!.HasExif);
        }
        finally
        {
            ImagingDefaults.Current = before;
        }
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, ImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg, ImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp, ImageFormat.Webp)]
    public void Icc_profile_is_kept_byte_for_byte_on_request(SKEncodedImageFormat sourceFormat, ImageFormat outputFormat)
    {
        var p3 = TaggedImage(sourceFormat);
        using var doc = ImageDocument.Load(p3).Value!;
        Assert.True(doc.Metadata.HasIccProfile);
        var profile = doc.IccProfile.ToArray();
        Assert.NotEmpty(profile);

        using var kept = doc.Encode(new ImageEncodeOptions { Format = outputFormat, KeepIccProfile = true, Quality = 95 }).Value!;
        var keptMeta = ImageMetadataReader.Read(kept.Span).Value!;
        Assert.True(keptMeta.HasIccProfile);
        using (var reloaded = ImageDocument.Load(kept.Span).Value!)
            Assert.Equal(profile, reloaded.IccProfile.ToArray());

        using var dropped = doc.Encode(new ImageEncodeOptions { Format = outputFormat, Quality = 95 }).Value!;
        Assert.False(ImageMetadataReader.Read(dropped.Span).Value!.HasIccProfile);
    }

    [Fact]
    public void Dropping_a_profile_converts_the_pixels_to_srgb()
    {
        // The test image is sRGB (0,200,0) stored as Display P3 values; loading keeps the P3 values, encoding without
        // the profile converts them back to sRGB.
        using var doc = ImageDocument.Load(TaggedImage(SKEncodedImageFormat.Png, new SKColor(0, 200, 0))).Value!;
        var raw = doc.GetPixel(10, 10);
        Assert.NotEqual(new Color(0, 200, 0), raw);
        using var srgb = doc.Encode(ImageFormat.Png).Value!;
        using var reloaded = ImageDocument.Load(srgb.Span).Value!;
        Assert.True(reloaded.IccProfile.IsEmpty);
        AssertColor(new Color(0, 200, 0), reloaded.GetPixel(10, 10), 2);
    }

    [Fact]
    public void Exif_is_read_from_png_and_webp_containers()
    {
        using var doc = ImageDocument.Load(InsertJpegApp1(Solid(16, 16, SKEncodedImageFormat.Jpeg), CameraExif())).Value!;
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Webp })
        {
            using var encoded = doc.Encode(new ImageEncodeOptions { Format = format, KeepMetadata = true, KeepIccProfile = true }).Value!;
            Assert.Equal("Canon", ImageMetadataReader.Read(encoded.Span).Value!.CameraMake);
            Assert.Equal(16, ImageInfo.Probe(encoded.Span).Value!.Width);
        }
    }

    private static byte[] TaggedImage(SKEncodedImageFormat format, SKColor? color = null)
    {
        using var p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul, p3));
        bitmap.Erase(color ?? new SKColor(30, 160, 90));
        using var pixmap = bitmap.PeekPixels();
        using var data = format switch
        {
            SKEncodedImageFormat.Jpeg => pixmap.Encode(new SKJpegEncoderOptions(95)),
            SKEncodedImageFormat.Webp => pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75)),
            _ => pixmap.Encode(SKPngEncoderOptions.Default),
        };
        return data!.ToArray();
    }
}
