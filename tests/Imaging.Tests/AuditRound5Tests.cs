using CL.Imaging;
using SkiaSharp;
using Xunit;
using static Imaging.Tests.TestImages;

namespace Imaging.Tests;

public sealed class EncodedImageLifetimeTests
{
    [Fact]
    public void A_stream_kept_past_the_image_disposal_still_reads_the_original_bytes()
    {
        using var doc = SolidDoc(24, 16, Color.Red);
        var encoded = doc.Encode(ImageFormat.Png).Value!;
        var expected = encoded.ToArray();
        var stream = encoded.AsStream();

        encoded.Dispose();
        // Churn the pool: anything returned to it is handed out and overwritten here.
        for (var i = 0; i < 64; i++)
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(expected.Length);
            rented.AsSpan().Fill(0xAB);
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal(24, ImageInfo.Probe(copy.ToArray()).Value!.Width);
        stream.Dispose();
    }

    [Fact]
    public void Span_and_the_other_accessors_throw_after_dispose_and_dispose_is_idempotent()
    {
        using var doc = SolidDoc(8, 8, Color.Blue);
        var encoded = doc.Encode(ImageFormat.Png).Value!;
        var stream = encoded.AsStream();

        encoded.Dispose();
        encoded.Dispose();

        Assert.Throws<ObjectDisposedException>(() => encoded.Span.Length);
        Assert.Throws<ObjectDisposedException>(() => encoded.Memory);
        Assert.Throws<ObjectDisposedException>(() => encoded.ToArray());
        Assert.Throws<ObjectDisposedException>(() => encoded.AsStream());
        Assert.Throws<ObjectDisposedException>(() => encoded.ToBase64());
        Assert.True(stream.CanRead);   // the stream's own reference keeps it readable
        stream.Dispose();
        stream.Dispose();
    }

    [Fact]
    public void Streams_disposed_before_the_image_do_not_release_the_image_bytes()
    {
        using var doc = SolidDoc(8, 8, Color.Green);
        using var encoded = doc.Encode(ImageFormat.Png).Value!;
        var expected = encoded.ToArray();

        encoded.AsStream().Dispose();
        encoded.AsStream().Dispose();

        Assert.Equal(expected, encoded.ToArray());
        Assert.Equal(8, ImageInfo.Probe(encoded.Span).Value!.Width);
    }
}

public sealed class ProbeLimitTests
{
    [Fact]
    public void A_header_claiming_a_side_over_the_limit_is_refused_without_allocating_for_pixels()
    {
        var png = Solid(32_768, 1, SKEncodedImageFormat.Png);            // one pixel wider than CL.Imaging will ever hold
        Assert.True(ImageInfo.Probe(new MemoryStream(Solid(4, 4, SKEncodedImageFormat.Png))).IsSuccess);   // warm-up

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ImageInfo.Probe(new UploadTests.NonSeekableStream(png));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("imaging.too_large", result.Error?.Code);
        Assert.True(allocated < 1024 * 1024, $"probing allocated {allocated:N0} bytes");
        Assert.Equal("imaging.too_large", ImageInfo.Probe(png).Error?.Code);
        Assert.Equal("imaging.too_large", ImageDocument.Load(png).Error?.Code);
    }

    [Fact]
    public void A_header_within_the_limit_is_still_probed()
    {
        var png = Solid(32_767, 1, SKEncodedImageFormat.Png);
        Assert.Equal(32_767, ImageInfo.Probe(new UploadTests.NonSeekableStream(png)).Value!.Width);
    }
}
