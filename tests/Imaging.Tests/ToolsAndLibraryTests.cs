using CL.Imaging;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class ToolsAndLibraryTests
{
    [Fact]
    public void Variants_are_made_from_one_decode_and_never_upscaled()
    {
        using var source = ImageDocument.Load(JpegWithOrientation(1000, 600, 6)).Value!;   // upright 600×1000
        using var variants = ImageTools.GenerateVariants(source, [1280, 320, 640, 320], ImageFormat.Webp).Value!;
        Assert.Equal([320, 600], variants.Select(v => v.Width));   // 640 and 1280 are wider than the image (600)
        Assert.Equal(533, variants[0].Height);
        Assert.All(variants, v => Assert.Equal(ImageFormat.Webp, v.Format));
        Assert.Equal("/a-320.webp 320w, /a-600.webp 600w", variants.ToSrcSet(v => $"/a-{v.Width}.webp"));

        using var tiny = SolidDoc(100, 50, Color.Red);
        using var single = ImageTools.GenerateVariants(tiny, [320, 640]).Value!;
        Assert.Equal(100, Assert.Single(single).Width);

        Assert.Equal("imaging.invalid_argument", ImageTools.GenerateVariants(tiny, []).Error?.Code);
        Assert.Equal("imaging.invalid_argument", ImageTools.GenerateVariants(tiny, [0, 10]).Error?.Code);
    }

    [Fact]
    public async Task Variants_from_a_stream_and_cancellation()
    {
        var png = Solid(800, 400, SKEncodedImageFormat.Png);
        using (var variants = (await ImageTools.GenerateVariantsAsync(new MemoryStream(png), [200, 400], ImageFormat.Jpeg)).Value!)
            Assert.Equal([(200, 100), (400, 200)], variants.Select(v => (v.Width, v.Height)));

        using var doc = ImageDocument.Load(png).Value!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal("imaging.cancelled", ImageTools.GenerateVariants(doc, [100, 200], ct: cts.Token).Error?.Code);
        Assert.Equal("imaging.cancelled", (await ImageTools.ProcessUploadAsync(new MemoryStream(png), ct: cts.Token)).Error?.Code);
    }

    [Fact]
    public async Task Thumbnails_cover_the_box()
    {
        using var source = Quadrants(200, 100);
        using var thumb = ImageTools.Thumbnail(source, 64, 64, format: ImageFormat.Png).Value!;
        Assert.Equal((64, 64), (thumb.Width, thumb.Height));
        using var smart = ImageTools.Thumbnail(source, 64, 64, smartCrop: true, format: ImageFormat.Png).Value!;
        Assert.Equal((64, 64), (smart.Width, smart.Height));
        using var fit = ImageTools.Thumbnail(source, 64, 64, ResizeMode.Fit, format: ImageFormat.Png).Value!;
        Assert.Equal((64, 32), (fit.Width, fit.Height));
        using var fromStream = (await ImageTools.ThumbnailAsync(new MemoryStream(Solid(300, 300, SKEncodedImageFormat.Png)), 50, 40)).Value!;
        Assert.Equal((50, 40), (fromStream.Width, fromStream.Height));
    }

    [Fact]
    public void Data_uris()
    {
        using var doc = SolidDoc(4, 4, Color.Red);
        var uri = ImageTools.ToDataUri(doc, ImageFormat.Png).Value!;
        Assert.StartsWith("data:image/png;base64,", uri);
        var bytes = Convert.FromBase64String(uri["data:image/png;base64,".Length..]);
        Assert.Equal(4, ImageInfo.Probe(bytes).Value!.Width);

        Assert.StartsWith("data:image/jpeg;base64,", ImageTools.ToDataUri(Solid(4, 4, SKEncodedImageFormat.Jpeg)).Value);
        Assert.Equal("imaging.decode_failed", ImageTools.ToDataUri("nope"u8).Error?.Code);
    }

    [Fact]
    public async Task Encoded_images_expose_their_bytes_in_several_ways()
    {
        using var doc = SolidDoc(8, 8, Color.Blue);
        using var encoded = doc.Encode(ImageFormat.Png).Value!;
        Assert.Equal("image/png", encoded.MimeType);
        Assert.Equal(encoded.Length, encoded.ToArray().Length);
        Assert.Equal(encoded.ToArray(), Convert.FromBase64String(encoded.ToBase64()));
        using var stream = encoded.AsStream();
        Assert.Equal(encoded.Length, stream.Length);
        var copy = new MemoryStream();
        await encoded.WriteToAsync(copy);
        Assert.Equal(encoded.ToArray(), copy.ToArray());
        var path = Path.Combine(Path.GetTempPath(), $"cl-imaging-{Guid.NewGuid():N}", "out.png");
        try
        {
            await encoded.SaveAsync(path);
            Assert.Equal(encoded.ToArray(), await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Defaults_drive_format_and_quality()
    {
        using var doc = SolidDoc(8, 8, Color.Blue);
        using (var e = doc.Encode().Value!) Assert.Equal(ImageFormat.Webp, e.Format);
        var before = ImagingDefaults.Current;
        try
        {
            ImagingDefaults.Current = before with { Format = ImageFormat.Jpeg };
            using var jpeg = doc.Encode().Value!;
            Assert.Equal(ImageFormat.Jpeg, jpeg.Format);
        }
        finally
        {
            ImagingDefaults.Reset();
        }
        Assert.Same(ImagingSettings.BuiltIn, ImagingDefaults.Current);
    }

    [Fact]
    public void Config_validates_and_maps_to_settings()
    {
        var config = new ImagingConfig();
        Assert.True(config.Validate().IsValid);
        Assert.Equal(ImagingSettings.BuiltIn, config.ToSettings());
        config.DefaultQuality = 0;
        config.DefaultFormat = ImageFormat.Gif;
        config.DefaultMaxPixels = -1;
        var result = config.Validate();
        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public async Task Library_reports_its_manifest_and_health()
    {
        using var library = new ImagingLibrary();
        Assert.Equal("CL.Imaging", library.Manifest.Id);
        var health = await library.HealthCheckAsync();
        Assert.Contains("Webp", health.Message);
    }
}
