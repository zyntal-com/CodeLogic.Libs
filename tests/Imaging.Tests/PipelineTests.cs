using CL.Imaging;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class PipelineTests
{
    private static ImageDocument Run(ImageDocument source, Func<ImagePipeline, ImagePipeline> edit)
    {
        var result = edit(source.Edit()).Apply();
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    // ── Geometry ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ResizeMode.Fit, 200, 200, 200, 160)]
    [InlineData(ResizeMode.Fit, 50, 0, 50, 40)]
    [InlineData(ResizeMode.Fit, 0, 20, 25, 20)]
    [InlineData(ResizeMode.Max, 200, 200, 100, 80)]
    [InlineData(ResizeMode.Max, 50, 50, 50, 40)]
    [InlineData(ResizeMode.Stretch, 30, 90, 30, 90)]
    [InlineData(ResizeMode.Fill, 40, 40, 40, 40)]
    [InlineData(ResizeMode.Pad, 60, 60, 60, 60)]
    public void Resize_modes_produce_the_documented_sizes(ResizeMode mode, int w, int h, int ew, int eh)
    {
        using var source = Quadrants(100, 80);
        using var result = Run(source, p => p.Resize(w, h, mode));
        Assert.Equal((ew, eh), (result.Width, result.Height));
    }

    [Fact]
    public void Resize_fill_crops_at_the_anchor_and_pad_fills_with_background()
    {
        using var source = Quadrants(100, 50);
        // Cover a square: the left half (red over blue) is kept with Anchor.Left.
        using var left = Run(source, p => p.Resize(20, 20, ResizeMode.Fill, anchor: Anchor.Left));
        AssertColor(Color.Red, left.GetPixel(10, 3), 10);
        AssertColor(Color.Blue, left.GetPixel(10, 16), 10);

        using var padded = Run(source, p => p.Resize(100, 100, ResizeMode.Pad, background: Color.Black));
        AssertColor(Color.Black, padded.GetPixel(50, 5), 0);
        AssertColor(Color.Red, padded.GetPixel(10, 30), 10);
        AssertColor(Color.Black, padded.GetPixel(50, 95), 0);
    }

    [Fact]
    public void Large_reductions_stay_smooth()
    {
        using var source = Scene(2000, 1500);
        using var small = Run(source, p => p.Resize(100, 75, ResizeMode.Fit, ResampleQuality.High));
        using var reference = Run(source, p => p.Resize(100, 75, ResizeMode.Fit, ResampleQuality.Medium));
        using var cmp = CL.Imaging.Analysis.ImageCompare.Difference(small, reference).Value!;
        Assert.True(cmp.Similarity > 0.97, $"similarity {cmp.Similarity}");
    }

    [Fact]
    public void Crop_keeps_the_rectangle_and_rejects_out_of_bounds()
    {
        using var source = Quadrants(100, 80);
        using var cropped = Run(source, p => p.Crop(new PixelRect(50, 0, 50, 40)));
        Assert.Equal((50, 40), (cropped.Width, cropped.Height));
        AssertColor(Color.Green, cropped.GetPixel(25, 20), 0);

        Assert.Equal("imaging.invalid_argument", source.Edit().Crop(new PixelRect(60, 0, 50, 40)).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", source.Edit().Crop(new PixelRect(0, 0, 0, 10)).Apply().Error?.Code);
    }

    [Theory]
    [InlineData(90, 80, 100, "B")]     // red TL → TR, blue BL → TL
    [InlineData(180, 100, 80, "W")]   // white BR → TL
    [InlineData(270, 80, 100, "G")]   // green TR → TL
    [InlineData(-90, 80, 100, "G")]
    [InlineData(450, 80, 100, "B")]
    public void Rotate_right_angles_exactly(double degrees, int w, int h, string topLeft)
    {
        using var source = Quadrants(100, 80);
        using var rotated = Run(source, p => p.Rotate(degrees));
        Assert.Equal((w, h), (rotated.Width, rotated.Height));
        var expected = topLeft switch { "B" => Color.Blue, "W" => Color.White, _ => Color.Green };
        AssertColor(expected, rotated.GetPixel(5, 5), 0);
    }

    [Fact]
    public void Rotate_right_angles_map_odd_sizes_pixel_to_pixel()
    {
        using var source = SolidDoc(5, 3, Color.White).Edit().DrawRectangle(new PixelRect(0, 0, 1, 1), Color.Red).Apply().Value!;
        using var r90 = Run(source, p => p.Rotate(90));
        Assert.Equal((3, 5), (r90.Width, r90.Height));
        AssertColor(Color.Red, r90.GetPixel(2, 0), 0);
        AssertColor(Color.White, r90.GetPixel(1, 0), 0);
        using var r180 = Run(source, p => p.Rotate(180));
        AssertColor(Color.Red, r180.GetPixel(4, 2), 0);
        using var r270 = Run(source, p => p.Rotate(270));
        AssertColor(Color.Red, r270.GetPixel(0, 4), 0);
    }

    [Fact]
    public void Rotate_any_angle_grows_the_canvas_and_fills_corners()
    {
        using var source = SolidDoc(100, 100, Color.Red);
        using var rotated = Run(source, p => p.Rotate(45, Color.Blue));
        Assert.InRange(rotated.Width, 141, 142);
        Assert.InRange(rotated.Height, 141, 142);
        AssertColor(Color.Blue, rotated.GetPixel(2, 2), 0);
        AssertColor(Color.Red, rotated.GetPixel(rotated.Width / 2, rotated.Height / 2), 0);

        using var transparent = Run(source, p => p.Rotate(17.5));
        Assert.Equal(0, transparent.GetPixel(0, 0).A);
    }

    [Fact]
    public void Flips_mirror_the_quadrants()
    {
        using var source = Quadrants(100, 80);
        using var h = Run(source, p => p.FlipHorizontal());
        AssertColor(Color.Green, h.GetPixel(5, 5), 0);
        using var v = Run(source, p => p.FlipVertical());
        AssertColor(Color.Blue, v.GetPixel(5, 5), 0);
        Assert.Equal((100, 80), (v.Width, v.Height));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Auto_orient_handles_all_eight_orientations(ushort orientation)
    {
        using var doc = ImageDocument.Load(JpegWithOrientation(300, 200, orientation)).Value!;
        Assert.Equal((ImageOrientation)orientation, doc.Orientation);
        Assert.Equal((300, 200), (doc.Width, doc.Height));
        using var upright = Run(doc, p => p.AutoOrient());
        Assert.Equal(ImageOrientation.Normal, upright.Orientation);
        Assert.Equal((doc.DisplayWidth, doc.DisplayHeight), (upright.Width, upright.Height));
        // Applying it twice changes nothing more.
        using var again = Run(upright, p => p.AutoOrient());
        Assert.Equal((upright.Width, upright.Height), (again.Width, again.Height));
    }

    [Fact]
    public void Smart_crop_picks_the_busy_region()
    {
        // A flat grey image with a detailed patch on the right.
        using var bitmap = new SKBitmap(400, 200);
        bitmap.Erase(new SKColor(128, 128, 128));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            for (var y = 40; y < 160; y += 8)
                for (var x = 280; x < 380; x += 8)
                {
                    paint.Color = ((x + y) / 8 % 2 == 0) ? SKColors.Black : SKColors.Yellow;
                    canvas.DrawRect(x, y, 8, 8, paint);
                }
        }
        using var source = Doc(bitmap);
        var region = CL.Imaging.Analysis.SmartCrop.FindRegion(source, 1, 1);
        Assert.Equal((200, 200), (region.Width, region.Height));
        Assert.InRange(region.X, 180, 200);   // covers x 280–380

        using var cropped = Run(source, p => p.SmartCrop(100, 100));
        Assert.Equal((100, 100), (cropped.Width, cropped.Height));
        var busy = 0;
        for (var x = 0; x < 100; x += 5)
            if (cropped.GetPixel(x, 50) is var c && (Math.Abs(c.R - 128) > 40 || Math.Abs(c.B - 128) > 40)) busy++;
        Assert.True(busy >= 8, $"{busy} of 20 samples are in the detailed patch");
    }

    // ── Colour (tolerances allow for 8-bit rounding) ───────────────────────────

    [Fact]
    public void Brightness_contrast_exposure_and_gamma()
    {
        using var gray = SolidDoc(10, 10, new Color(100, 100, 100));
        AssertColor(new Color(151, 151, 151), Run(gray, p => p.Brightness(0.2)).GetPixel(5, 5));
        AssertColor(new Color(49, 49, 49), Run(gray, p => p.Brightness(-0.2)).GetPixel(5, 5));
        AssertColor(new Color(72, 72, 72), Run(gray, p => p.Contrast(1.0)).GetPixel(5, 5));    // 2×(100−127.5)+127.5
        AssertColor(new Color(128, 128, 128), Run(gray, p => p.Contrast(-1.0)).GetPixel(5, 5));
        AssertColor(new Color(200, 200, 200), Run(gray, p => p.Exposure(1)).GetPixel(5, 5));
        AssertColor(new Color(50, 50, 50), Run(gray, p => p.Exposure(-1)).GetPixel(5, 5));
        // 255 × (100/255)^(1/2.2) ≈ 167
        AssertColor(new Color(167, 167, 167), Run(gray, p => p.Gamma(2.2)).GetPixel(5, 5));
    }

    [Fact]
    public void Saturation_grayscale_sepia_invert_hue_and_tint()
    {
        using var red = SolidDoc(10, 10, Color.Red);
        var gray = Run(red, p => p.Grayscale()).GetPixel(5, 5);
        AssertColor(new Color(54, 54, 54), gray);                               // 0.2126 × 255
        AssertColor(Run(red, p => p.Saturation(-1)).GetPixel(5, 5), gray, 1);
        AssertColor(Color.Red, Run(red, p => p.Saturation(0)).GetPixel(5, 5), 1);
        AssertColor(new Color(100, 89, 69), Run(red, p => p.Sepia()).GetPixel(5, 5));   // 0.393/0.349/0.272 × 255
        AssertColor(new Color(0, 255, 255), Run(red, p => p.Invert()).GetPixel(5, 5), 0);
        var hue = Run(red, p => p.Hue(120)).GetPixel(5, 5);
        Assert.True(hue.G > hue.R && hue.G > hue.B, $"hue-rotated red should be greenish, got {hue}");
        using var white = SolidDoc(10, 10, Color.White);
        AssertColor(new Color(0, 0, 255), Run(white, p => p.Tint(Color.Blue, 1)).GetPixel(5, 5));
        AssertColor(new Color(128, 128, 255), Run(white, p => p.Tint(Color.Blue, 0.5)).GetPixel(5, 5));
    }

    [Fact]
    public void Colour_operations_keep_alpha_and_compose()
    {
        using var half = SolidDoc(10, 10, new Color(200, 100, 50, 128));
        var inverted = Run(half, p => p.Invert()).GetPixel(5, 5);
        Assert.InRange(inverted.A, 127, 129);
        AssertColor(new Color(55, 155, 205, 128), inverted, 3);
        // Invert twice is the identity (fused into one pass).
        using var source = Scene(64, 48);
        using var twice = Run(source, p => p.Invert().Invert());
        using var cmp = CL.Imaging.Analysis.ImageCompare.Difference(source, twice, new() { Tolerance = 1 }).Value!;
        Assert.Equal(0, cmp.DifferentPixels);
    }

    // ── Filters ────────────────────────────────────────────────────────────────

    [Fact]
    public void Blur_softens_edges_and_keeps_flat_areas()
    {
        using var source = Quadrants(100, 80);
        using var blurred = Run(source, p => p.GaussianBlur(4));
        Assert.Equal((100, 80), (blurred.Width, blurred.Height));
        AssertColor(Color.Red, blurred.GetPixel(2, 2), 2);                      // clamped edges: no dark border
        var edge = blurred.GetPixel(50, 10);                                    // red | green boundary
        Assert.InRange(edge.R, 60, 200);
        Assert.InRange(edge.G, 60, 200);
    }

    [Fact]
    public void Sharpen_increases_edge_contrast()
    {
        using var bitmap = new SKBitmap(40, 10);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = new SKColor(100, 100, 100) })
        {
            canvas.Clear(new SKColor(100, 100, 100));
            paint.Color = new SKColor(160, 160, 160);
            canvas.DrawRect(20, 0, 20, 10, paint);
        }
        using var source = Doc(bitmap);
        using var sharp = Run(source, p => p.Sharpen(1.0, 1.5));
        Assert.True(sharp.GetPixel(18, 5).R < 100, "dark side of the edge gets darker");
        Assert.True(sharp.GetPixel(21, 5).R > 160, "light side of the edge gets lighter");
        AssertColor(new Color(100, 100, 100), sharp.GetPixel(2, 5), 2);
    }

    [Fact]
    public void Pixelate_averages_blocks()
    {
        using var source = Quadrants(16, 16);
        using var pixelated = Run(source, p => p.Pixelate(16));
        var c = pixelated.GetPixel(0, 0);
        Assert.Equal(pixelated.GetPixel(15, 15), c);
        AssertColor(new Color(128, 128, 128), c, 2);                            // (R+G+B+W)/4 per channel
        using var blocks = Run(source, p => p.Pixelate(8));
        AssertColor(Color.Red, blocks.GetPixel(0, 0), 0);
        AssertColor(Color.White, blocks.GetPixel(15, 15), 0);
    }

    // ── Frames and drawing ─────────────────────────────────────────────────────

    [Fact]
    public void Round_corners_border_and_pad()
    {
        using var source = SolidDoc(100, 60, Color.Red);
        using var round = Run(source, p => p.RoundCorners(20));
        Assert.Equal(0, round.GetPixel(0, 0).A);
        AssertColor(Color.Red, round.GetPixel(50, 30), 0);

        using var border = Run(source, p => p.Border(4, Color.Blue));
        Assert.Equal((100, 60), (border.Width, border.Height));
        AssertColor(Color.Blue, border.GetPixel(1, 30), 0);
        AssertColor(Color.Blue, border.GetPixel(98, 58), 0);
        AssertColor(Color.Red, border.GetPixel(5, 30), 0);

        using var padded = Run(source, p => p.Pad(20, Color.White));
        Assert.Equal((140, 100), (padded.Width, padded.Height));
        AssertColor(Color.White, padded.GetPixel(5, 5), 0);
        AssertColor(Color.Red, padded.GetPixel(70, 50), 0);
        using var uneven = Run(source, p => p.Pad(1, 2, 3, 4));
        Assert.Equal((104, 66), (uneven.Width, uneven.Height));
    }

    [Fact]
    public void Shapes_are_drawn_where_asked()
    {
        using var source = SolidDoc(100, 100, Color.White);
        using var drawn = Run(source, p => p
            .DrawRectangle(new PixelRect(10, 10, 20, 20), Color.Red)
            .DrawRectangle(new PixelRect(50, 10, 40, 40), Color.Blue, strokeWidth: 2)
            .DrawEllipse(new PixelRect(10, 60, 30, 30), Color.Green)
            .DrawLine(new ImagePoint(0, 99.5f), new ImagePoint(100, 99.5f), Color.Black, 1));
        AssertColor(Color.Red, drawn.GetPixel(20, 20), 0);
        AssertColor(Color.Blue, drawn.GetPixel(50, 30), 30);
        AssertColor(Color.White, drawn.GetPixel(70, 30), 0);                    // outline only
        AssertColor(Color.Green, drawn.GetPixel(25, 75), 0);
        AssertColor(Color.White, drawn.GetPixel(11, 61), 30);                   // outside the ellipse
        AssertColor(Color.Black, drawn.GetPixel(50, 99), 30);
        // The source is untouched.
        AssertColor(Color.White, source.GetPixel(20, 20), 0);
    }

    [Fact]
    public void Text_is_drawn_when_the_platform_has_a_font()
    {
        using var source = SolidDoc(200, 60, Color.White);
        using var drawn = Run(source, p => p.DrawText("Hello", ImageFont.Default(32), new ImagePoint(10, 10), Color.Black));
        if (!PlatformHasFont()) return;
        Assert.True(CountDark(drawn) > 50, "text pixels were drawn");
    }

    [Fact]
    public void Watermarks_land_at_the_anchor()
    {
        using var source = SolidDoc(200, 100, Color.White);
        using var logo = SolidDoc(40, 20, Color.Red);
        using var marked = Run(source, p => p.Watermark(new WatermarkImage(logo, null), Anchor.BottomRight, opacity: 1, margin: 10));
        AssertColor(Color.Red, marked.GetPixel(170, 80), 2);                     // inside 150–190 × 70–90
        AssertColor(Color.White, marked.GetPixel(20, 20), 0);

        using var faint = Run(source, p => p.Watermark(new WatermarkImage(logo, 0.5), Anchor.TopLeft, opacity: 0.5, margin: 0));
        AssertColor(new Color(255, 128, 128), faint.GetPixel(50, 10), 3);       // 100 px wide, 50% opacity
        AssertColor(Color.White, faint.GetPixel(150, 10), 0);

        using var text = Run(source, p => p.Watermark(new WatermarkText("© CodeLogic", ImageFont.Default(20), Color.Black), Anchor.TopLeft, 1, 4));
        if (PlatformHasFont()) Assert.True(CountDark(text) > 20);
    }

    [Fact]
    public void Composite_blends_with_opacity_and_mode()
    {
        using var source = SolidDoc(50, 50, new Color(200, 200, 200));
        using var overlay = SolidDoc(20, 20, new Color(100, 100, 100));
        using var normal = Run(source, p => p.Composite(overlay, new ImagePoint(10, 10), BlendMode.Normal, 0.5));
        AssertColor(new Color(150, 150, 150), normal.GetPixel(20, 20), 2);
        AssertColor(new Color(200, 200, 200), normal.GetPixel(40, 40), 0);
        using var multiply = Run(source, p => p.Composite(overlay, new ImagePoint(0, 0), BlendMode.Multiply));
        AssertColor(new Color(78, 78, 78), multiply.GetPixel(5, 5), 2);          // 200 × 100 / 255
    }

    // ── Pipeline behaviour ─────────────────────────────────────────────────────

    [Fact]
    public void Invalid_arguments_surface_from_encode_and_apply_without_throwing()
    {
        using var doc = SolidDoc(10, 10, Color.Red);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Brightness(2).Encode(ImageFormat.Png).Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Resize(0, 0).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Resize(10, 0, ResizeMode.Fill).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().GaussianBlur(0).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Pixelate(1).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Rotate(double.NaN).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Pad(-1, 0, 0, 0).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().DrawText("", ImageFont.Default(), default, Color.Black).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Watermark(new WatermarkImage(doc, 2)).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Resize(20_000, 20_000, ResizeMode.Stretch).Apply().Error?.Code);
        Assert.Equal("imaging.invalid_argument", doc.Edit().Encode(ImageFormat.Png, 0).Error?.Code);
        // The first error wins; later steps are ignored.
        var message = doc.Edit().Contrast(5).Saturation(9).Apply().Error!.Message;
        Assert.StartsWith("Contrast", message);
    }

    [Fact]
    public void Cancellation_stops_the_pipeline()
    {
        using var doc = Scene(64, 64);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal("imaging.cancelled", doc.Edit().GaussianBlur(2).Apply(cts.Token).Error?.Code);
        Assert.Equal("imaging.cancelled", doc.Edit().Encode(ImageFormat.Png, ct: cts.Token).Error?.Code);
    }

    [Fact]
    public void The_source_document_is_never_changed_and_pipelines_can_rerun()
    {
        using var doc = Quadrants(40, 40);
        var pipeline = doc.Edit().Pixelate(40).Border(2, Color.Black).Invert();
        using (var a = pipeline.Apply().Value!)
        using (var b = pipeline.Apply().Value!)
            Assert.Equal(a.GetPixel(20, 20), b.GetPixel(20, 20));
        AssertColor(Color.Red, doc.GetPixel(5, 5), 0);
        Assert.Equal(3, pipeline.Count);
    }

    [Fact]
    public void Disposed_documents_cannot_be_used_but_results_are_reported_not_thrown_for_overlays()
    {
        using var doc = SolidDoc(10, 10, Color.Red);
        var overlay = SolidDoc(5, 5, Color.Blue);
        overlay.Dispose();
        Assert.True(doc.Edit().Composite(overlay, default).Apply().IsFailure);
        Assert.Throws<ObjectDisposedException>(() => overlay.Width);
    }

    private static bool PlatformHasFont()
    {
        using var typeface = ImageFont.Default().CreateTypefaceForTests();
        return typeface.GlyphCount > 0;
    }

    private static int CountDark(ImageDocument doc)
    {
        var n = 0;
        for (var y = 0; y < doc.Height; y++)
            for (var x = 0; x < doc.Width; x++)
                if (doc.GetPixel(x, y).R < 100) n++;
        return n;
    }
}

internal static class FontTestExtensions
{
    public static SKTypeface CreateTypefaceForTests(this ImageFont font) => font.CreateTypeface();
}
