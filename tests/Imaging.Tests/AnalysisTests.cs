using CL.Imaging;
using CL.Imaging.Analysis;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class AnalysisTests
{
    [Fact]
    public void Average_and_dominant_colour()
    {
        using var quadrants = Quadrants(100, 100);
        AssertColor(new Color(128, 128, 128), ImageAnalysis.AverageColor(quadrants), 2);

        using var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(SKColors.Blue);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Yellow })
            canvas.DrawRect(0, 0, 100, 30, paint);
        using var mostlyBlue = Doc(bitmap);
        AssertColor(Color.Blue, ImageAnalysis.DominantColor(mostlyBlue), 4);
    }

    [Fact]
    public void Palette_finds_the_main_colours_with_their_shares()
    {
        using var quadrants = Quadrants(100, 100);
        var palette = ImageAnalysis.Palette(quadrants, 4);
        Assert.Equal(4, palette.Count);
        Assert.Equal(1.0, palette.Sum(p => p.Share), 6);
        foreach (var expected in new[] { Color.Red, Color.Green, Color.Blue, Color.White })
            Assert.Contains(palette, p => Math.Abs(p.Color.R - expected.R) + Math.Abs(p.Color.G - expected.G) + Math.Abs(p.Color.B - expected.B) < 30
                                          && Math.Abs(p.Share - 0.25) < 0.05);
        // Deterministic.
        Assert.Equal(palette, ImageAnalysis.Palette(quadrants, 4));
        // A flat image has one colour, however many are asked for.
        using var flat = SolidDoc(20, 20, Color.Red);
        Assert.Single(ImageAnalysis.Palette(flat, 8));
    }

    [Fact]
    public void Transparency_checks()
    {
        using var opaque = SolidDoc(10, 10, Color.Red);
        Assert.False(ImageAnalysis.HasAlpha(opaque));
        Assert.False(ImageAnalysis.IsMostlyTransparent(opaque));

        using var empty = SolidDoc(10, 10, Color.Transparent);
        Assert.True(ImageAnalysis.HasAlpha(empty));
        Assert.True(ImageAnalysis.IsMostlyTransparent(empty));
        Assert.Empty(ImageAnalysis.Palette(empty, 3));
        Assert.Equal(Color.Transparent, ImageAnalysis.AverageColor(empty));

        using var rounded = SolidDoc(100, 100, Color.Red).Edit().RoundCorners(10).Apply().Value!;
        Assert.True(ImageAnalysis.HasAlpha(rounded));
        Assert.False(ImageAnalysis.IsMostlyTransparent(rounded));
    }

    [Fact]
    public void BlurHash_round_trips_to_a_placeholder_of_the_same_colours()
    {
        using var quadrants = Quadrants(64, 64);
        var hash = BlurHash.Encode(quadrants, 4, 4).Value!;
        Assert.Equal(4 + 2 * 16, hash.Length);
        Assert.True(BlurHash.IsValid(hash));
        Assert.Equal(hash, BlurHash.Encode(quadrants, 4, 4).Value);             // deterministic

        using var placeholder = BlurHash.Decode(hash, 32, 32).Value!;
        Assert.Equal((32, 32), (placeholder.Width, placeholder.Height));
        var tl = placeholder.GetPixel(3, 3);
        var br = placeholder.GetPixel(28, 28);
        Assert.True(tl.R > tl.G + 60 && tl.R > tl.B + 60, $"top-left should be reddish, got {tl}");
        Assert.True(br.R > 170 && br.G > 170 && br.B > 170, $"bottom-right should be light, got {br}");

        // Decoding is deterministic, and a placeholder can itself be hashed.
        using var second = BlurHash.Decode(hash, 32, 32).Value!;
        Assert.Equal(placeholder.GetPixel(17, 9), second.GetPixel(17, 9));
        Assert.True(BlurHash.IsValid(BlurHash.Encode(placeholder, 4, 4).Value));
    }

    [Fact]
    public void BlurHash_known_vector_decodes()
    {
        // Reference hash from the BlurHash project README.
        using var image = BlurHash.Decode("LEHV6nWB2yk8pyo0adR*.7kCMdnj", 32, 32).Value!;
        Assert.Equal(32, image.Width);
        Assert.Equal("imaging.invalid_argument", BlurHash.Decode("not-a-hash", 32, 32).Error?.Code);
        Assert.Equal("imaging.invalid_argument", BlurHash.Decode("LEHV6nWB2yk8pyo0adR*.7kCMdnj", 0, 32).Error?.Code);
        Assert.Equal("imaging.invalid_argument", BlurHash.Encode(image, 0, 3).Error?.Code);
    }

    [Fact]
    public void BlurHash_rejects_out_of_spec_component_counts_and_oversized_output()
    {
        // Size flags 81 and 82 ('}' and '~') would mean ten component rows; the spec allows 1-9 per axis.
        foreach (var flag in "}~")
        {
            var rows = flag == '}' ? (81 % 9 + 1) * 10 : (82 % 9 + 1) * 10;
            var hash = flag + new string('0', 3 + 2 * rows);
            Assert.False(BlurHash.IsValid(hash));
            Assert.Equal("imaging.invalid_argument", BlurHash.Decode(hash, 8, 8).Error?.Code);
        }
        var maximal = "|" + new string('0', 3 + 2 * 81);                         // 80: 9 x 9 components, valid
        Assert.True(BlurHash.IsValid(maximal));

        const string vector = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";
        Assert.Equal("imaging.invalid_argument", BlurHash.Decode(vector, 2048, 2048).Error?.Code);
        Assert.Equal("imaging.invalid_argument", BlurHash.Decode(vector, 2048, 1024).Error?.Code);
        using var large = BlurHash.Decode(maximal, 2048, 512).Value!;              // exactly the pixel cap
        Assert.Equal(BlurHash.MaxDecodePixels, large.Width * large.Height);
    }

    [Fact]
    public void BlurHash_decode_matches_the_reference_algorithm()
    {
        const string hash = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";
        const int width = 20, height = 12;
        using var image = BlurHash.Decode(hash, width, height).Value!;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                AssertColor(ReferencePixel(hash, x, y, width, height), image.GetPixel(x, y), 1, $"({x},{y})");

        // The reference implementation's per-pixel double loop.
        static Color ReferencePixel(string hash, int x, int y, int width, int height)
        {
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
            int D(int start, int length) { var v = 0; for (var k = start; k < start + length; k++) v = v * 83 + alphabet.IndexOf(hash[k]); return v; }
            double Lin(int v) { var f = v / 255.0; return f <= 0.04045 ? f / 12.92 : Math.Pow((f + 0.055) / 1.055, 2.4); }
            int Srgb(double v) { v = Math.Clamp(v, 0, 1); return v <= 0.0031308 ? (int)(v * 12.92 * 255 + 0.5) : (int)((1.055 * Math.Pow(v, 1 / 2.4) - 0.055) * 255 + 0.5); }
            double SignPow(double v, double e) => Math.CopySign(Math.Pow(Math.Abs(v), e), v);
            var flag = D(0, 1);
            var (nx, ny) = (flag % 9 + 1, flag / 9 + 1);
            var max = (D(1, 1) + 1) / 166.0;
            double r = 0, g = 0, b = 0;
            for (var j = 0; j < ny; j++)
                for (var i = 0; i < nx; i++)
                {
                    double cr, cg, cb;
                    if (i == 0 && j == 0) { var dc = D(2, 4); (cr, cg, cb) = (Lin(dc >> 16), Lin((dc >> 8) & 255), Lin(dc & 255)); }
                    else
                    {
                        var v = D(4 + (j * nx + i) * 2, 2);
                        cr = SignPow((v / 361 - 9) / 9.0, 2) * max;
                        cg = SignPow((v / 19 % 19 - 9) / 9.0, 2) * max;
                        cb = SignPow((v % 19 - 9) / 9.0, 2) * max;
                    }
                    var basis = Math.Cos(Math.PI * x * i / width) * Math.Cos(Math.PI * y * j / height);
                    r += cr * basis; g += cg * basis; b += cb * basis;
                }
            return new Color((byte)Srgb(r), (byte)Srgb(g), (byte)Srgb(b));
        }
    }

    [Fact]
    public void Reading_a_stream_without_a_limit_does_not_rent_what_it_claims_up_front()
    {
        using var liar = new HugeLengthStream([1, 2, 3, 4, 5]);
        Assert.True(CL.Imaging.Internal.StreamBuffer.InitialSize(liar, 0) <= CL.Imaging.Internal.StreamBuffer.MaxInitialRent);
        Assert.Equal(1001, CL.Imaging.Internal.StreamBuffer.InitialSize(new MemoryStream(new byte[1000]), 0));
        Assert.Equal(256, CL.Imaging.Internal.StreamBuffer.InitialSize(new MemoryStream(new byte[1000]), 10));
        using var read = CL.Imaging.Internal.StreamBuffer.ReadAll(liar, 0).Value!;
        Assert.Equal(5, read.Length);
    }

    /// <summary>A seekable stream that reports a terabyte but holds a few bytes.</summary>
    private sealed class HugeLengthStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1L << 40;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
            _position += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Perceptual_hashes_spot_near_duplicates()
    {
        using var original = Scene(320, 240, seed: 7);
        using var resized = original.Edit().Resize(160, 120).Apply().Value!;
        using var recompressed = ImageDocument.Load(original.Encode(ImageFormat.Jpeg, 40).Value!.Span).Value!;
        using var brighter = original.Edit().Brightness(0.05).Apply().Value!;
        using var different = Scene(320, 240, seed: 99);
        using var flipped = original.Edit().FlipVertical().Apply().Value!;

        var p = PerceptualHash.PHash(original);
        Assert.True(PerceptualHash.IsNearDuplicate(p, PerceptualHash.PHash(resized)), "resized");
        Assert.True(PerceptualHash.IsNearDuplicate(p, PerceptualHash.PHash(recompressed)), "recompressed");
        Assert.True(PerceptualHash.IsNearDuplicate(p, PerceptualHash.PHash(brighter)), "brighter");
        Assert.False(PerceptualHash.IsNearDuplicate(p, PerceptualHash.PHash(different)), "different scene");
        Assert.False(PerceptualHash.IsNearDuplicate(p, PerceptualHash.PHash(flipped)), "flipped");

        var d = PerceptualHash.DHash(original);
        Assert.True(PerceptualHash.HammingDistance(d, PerceptualHash.DHash(resized)) <= 6);
        Assert.True(PerceptualHash.HammingDistance(d, PerceptualHash.DHash(different)) > 12);
        Assert.Equal(1.0, PerceptualHash.Similarity(d, d));
        Assert.Equal(0, PerceptualHash.HammingDistance(p, PerceptualHash.PHash(original)));
    }

    [Fact]
    public void Compare_reports_similarity_and_a_diff_image()
    {
        using var a = SolidDoc(100, 100, Color.White);
        using var b = a.Edit().DrawRectangle(new PixelRect(0, 0, 10, 10), Color.Black).Apply().Value!;
        using var same = ImageCompare.Difference(a, a).Value!;
        Assert.Equal(1.0, same.Similarity);
        Assert.Equal(0, same.DifferentPixels);
        Assert.Null(same.DiffImage);

        using var diff = ImageCompare.Difference(a, b, new ImageCompareOptions { CreateDiffImage = true }).Value!;
        Assert.Equal(100, diff.DifferentPixels);
        Assert.Equal(0.01, diff.DifferentRatio, 6);
        Assert.Equal(1 - 0.01 * 0.75, diff.Similarity, 6);                       // RGB differ fully, alpha does not
        AssertColor(Color.Red, diff.DiffImage!.GetPixel(5, 5), 0);
        AssertColor(Color.White, diff.DiffImage.GetPixel(50, 50), 0);

        using var small = a.Edit().Resize(50, 50).Apply().Value!;
        Assert.Equal(1.0, ImageCompare.Difference(a, small).Value!.Similarity, 3);
        Assert.Equal("imaging.invalid_argument", ImageCompare.Difference(a, small, new() { ResizeToMatch = false }).Error?.Code);
    }
}
