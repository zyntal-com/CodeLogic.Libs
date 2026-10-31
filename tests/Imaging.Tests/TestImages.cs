using System.Buffers.Binary;
using CL.Imaging;
using SkiaSharp;
using Xunit;

namespace Imaging.Tests;

/// <summary>Images generated in code: no binary fixtures.</summary>
internal static class TestImages
{
    public static byte[] Solid(int width, int height, SKEncodedImageFormat format, SKColor? color = null, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.OrangeRed);
        return Encode(bitmap, format, quality);
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Blue image with a red 60×60 square in the stored top-left corner.</summary>
    public static byte[] RedCorner(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Red })
            canvas.DrawRect(0, 0, 60, 60, paint);
        return Encode(bitmap, format);
    }

    /// <summary>Four quadrants: red (TL), green (TR), blue (BL), white (BR).</summary>
    public static ImageDocument Quadrants(int width = 100, int height = 80)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            int hw = width / 2, hh = height / 2;
            paint.Color = SKColors.Red; canvas.DrawRect(0, 0, hw, hh, paint);
            paint.Color = new SKColor(0, 255, 0); canvas.DrawRect(hw, 0, width - hw, hh, paint);
            paint.Color = SKColors.Blue; canvas.DrawRect(0, hh, hw, height - hh, paint);
            paint.Color = SKColors.White; canvas.DrawRect(hw, hh, width - hw, height - hh, paint);
        }
        return Doc(bitmap);
    }

    public static ImageDocument SolidDoc(int width, int height, Color color) => ImageDocument.Create(width, height, color).Value!;

    public static ImageDocument Doc(SKBitmap bitmap) => ImageDocument.FromSkiaBitmap(bitmap).Value!;

    /// <summary>A smooth photo-like scene: gradient sky, a circle "sun" and some shapes.</summary>
    public static ImageDocument Scene(int width = 256, int height = 192, int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height),
                [new SKColor(40, 90, 200), new SKColor(250, 200, 120)], SKShaderTileMode.Clamp);
            using var sky = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, sky);
            var random = new Random(seed);
            using var paint = new SKPaint { IsAntialias = true };
            for (var i = 0; i < 12; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawCircle(random.Next(width), random.Next(height), random.Next(8, 40), paint);
            }
        }
        return Doc(bitmap);
    }

    public static byte[] JpegWithOrientation(int width, int height, ushort orientation) =>
        InsertJpegApp1(RedCorner(width, height, SKEncodedImageFormat.Jpeg), new TiffBuilder(bigEndian: true).Short(0x0112, orientation).Build());

    public static byte[] InsertJpegApp1(byte[] jpeg, byte[] tiff)
    {
        byte[] payload = [.. "Exif\0\0"u8, .. tiff];
        var length = payload.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload];
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    public static (byte R, byte G, byte B, byte A) Pixel(ImageDocument doc, int x, int y)
    {
        var c = doc.GetPixel(x, y);
        return (c.R, c.G, c.B, c.A);
    }

    public static void AssertColor(Color expected, Color actual, int tolerance = 3, string? where = null)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
                    && Math.Abs(expected.B - actual.B) <= tolerance && Math.Abs(expected.A - actual.A) <= tolerance,
            $"expected {expected} but got {actual}{(where is null ? "" : " at " + where)}");
    }
}

/// <summary>Builds a minimal TIFF/EXIF block: IFD0 with optional Exif and GPS sub-IFDs.</summary>
internal sealed class TiffBuilder(bool bigEndian = false)
{
    private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _ifd0 = [];
    private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _exif = [];
    private readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _gps = [];
    private List<(ushort Tag, ushort Type, uint Count, byte[] Data)> _target = null!;

    public TiffBuilder Ifd0() { _target = _ifd0; return this; }
    public TiffBuilder Exif() { _target = _exif; return this; }
    public TiffBuilder Gps() { _target = _gps; return this; }

    private List<(ushort Tag, ushort Type, uint Count, byte[] Data)> Target => _target ?? _ifd0;

    public TiffBuilder Short(ushort tag, ushort value)
    {
        var d = new byte[2];
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(d, value); else BinaryPrimitives.WriteUInt16LittleEndian(d, value);
        Target.Add((tag, 3, 1, d));
        return this;
    }

    public TiffBuilder Ascii(ushort tag, string value)
    {
        byte[] d = [.. System.Text.Encoding.ASCII.GetBytes(value), 0];
        Target.Add((tag, 2, (uint)d.Length, d));
        return this;
    }

    public TiffBuilder Rationals(ushort tag, params (uint Num, uint Den)[] values)
    {
        var d = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            U32(d.AsSpan(i * 8), values[i].Num);
            U32(d.AsSpan(i * 8 + 4), values[i].Den);
        }
        Target.Add((tag, 5, (uint)values.Length, d));
        return this;
    }

    public TiffBuilder Byte(ushort tag, byte value)
    {
        Target.Add((tag, 1, 1, [value]));
        return this;
    }

    public byte[] Build()
    {
        // Layout: header, IFD0, Exif IFD, GPS IFD, then all out-of-line values.
        var ifd0 = _ifd0.ToList();
        if (_exif.Count > 0) ifd0.Add((0x8769, 4, 1, new byte[4]));
        if (_gps.Count > 0) ifd0.Add((0x8825, 4, 1, new byte[4]));
        ifd0.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        var ifds = new List<List<(ushort Tag, ushort Type, uint Count, byte[] Data)>> { ifd0 };
        if (_exif.Count > 0) ifds.Add(_exif);
        if (_gps.Count > 0) ifds.Add(_gps);

        var offsets = new List<int>();
        var pos = 8;
        foreach (var ifd in ifds) { offsets.Add(pos); pos += 2 + ifd.Count * 12 + 4; }
        var dataStart = pos;
        var total = dataStart + ifds.Sum(ifd => ifd.Sum(e => e.Data.Length > 4 ? e.Data.Length + (e.Data.Length & 1) : 0));
        var buffer = new byte[total];
        buffer[0] = buffer[1] = (byte)(bigEndian ? 'M' : 'I');
        U16(buffer.AsSpan(2), 42);
        U32(buffer.AsSpan(4), 8);

        var dataPos = dataStart;
        for (var n = 0; n < ifds.Count; n++)
        {
            var ifd = ifds[n];
            var at = offsets[n];
            U16(buffer.AsSpan(at), (ushort)ifd.Count);
            for (var i = 0; i < ifd.Count; i++)
            {
                var (tag, type, count, data) = ifd[i];
                if (tag == 0x8769) U32(data, (uint)offsets[1]);
                if (tag == 0x8825) U32(data, (uint)offsets[^1]);
                var e = at + 2 + i * 12;
                U16(buffer.AsSpan(e), tag);
                U16(buffer.AsSpan(e + 2), type);
                U32(buffer.AsSpan(e + 4), count);
                if (data.Length <= 4) data.CopyTo(buffer, e + 8);
                else
                {
                    U32(buffer.AsSpan(e + 8), (uint)dataPos);
                    data.CopyTo(buffer, dataPos);
                    dataPos += data.Length + (data.Length & 1);
                }
            }
        }
        return buffer;
    }

    private void U16(Span<byte> s, ushort v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(s, v); else BinaryPrimitives.WriteUInt16LittleEndian(s, v); }
    private void U32(Span<byte> s, uint v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(s, v); else BinaryPrimitives.WriteUInt32LittleEndian(s, v); }
}
