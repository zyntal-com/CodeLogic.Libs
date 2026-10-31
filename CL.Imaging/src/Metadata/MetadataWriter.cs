using System.Buffers.Binary;
using System.IO.Compression;

namespace CL.Imaging.Metadata;

/// <summary>
/// Inserts EXIF and ICC blocks into freshly encoded JPEG, PNG and WebP data (Skia's encoders write neither the
/// original EXIF nor the original profile bytes).
/// </summary>
internal static class MetadataWriter
{
    private const int MaxJpegSegmentPayload = 65533;
    private const int IccChunkPayload = 65519;   // 65533 - "ICC_PROFILE\0" (12) - seq (1) - count (1)

    /// <summary>Writes the encoded image with the blocks inserted to <paramref name="output"/>.</summary>
    public static void Write(ReadOnlySpan<byte> encoded, ImageFormat format, byte[]? exif, byte[]? icc, bool hasAlpha, Stream output)
    {
        switch (format)
        {
            case ImageFormat.Jpeg: WriteJpeg(encoded, exif, icc, output); break;
            case ImageFormat.Png: WritePng(encoded, exif, icc, output); break;
            case ImageFormat.Webp: WriteWebp(encoded, exif, icc, hasAlpha, output); break;
            default: output.Write(encoded); break;
        }
    }

    private static void WriteJpeg(ReadOnlySpan<byte> jpeg, byte[]? exif, byte[]? icc, Stream output)
    {
        // After SOI and any JFIF APP0 segment.
        var insertAt = 2;
        if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
            insertAt = 4 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(4, 2));
        output.Write(jpeg[..insertAt]);

        if (exif is not null && exif.Length + 6 <= MaxJpegSegmentPayload)
        {
            WriteSegmentHeader(output, 0xE1, exif.Length + 6);
            output.Write("Exif\0\0"u8);
            output.Write(exif);
        }
        if (icc is { Length: > 0 })
        {
            var chunks = (icc.Length + IccChunkPayload - 1) / IccChunkPayload;
            if (chunks <= 255)
            {
                for (var i = 0; i < chunks; i++)
                {
                    var part = icc.AsSpan(i * IccChunkPayload, Math.Min(IccChunkPayload, icc.Length - i * IccChunkPayload));
                    WriteSegmentHeader(output, 0xE2, part.Length + 14);
                    output.Write("ICC_PROFILE\0"u8);
                    output.WriteByte((byte)(i + 1));
                    output.WriteByte((byte)chunks);
                    output.Write(part);
                }
            }
        }
        output.Write(jpeg[insertAt..]);
    }

    private static void WriteSegmentHeader(Stream output, byte marker, int payloadLength)
    {
        Span<byte> header = [0xFF, marker, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(payloadLength + 2));
        output.Write(header);
    }

    private static void WritePng(ReadOnlySpan<byte> png, byte[]? exif, byte[]? icc, Stream output)
    {
        // Signature (8) + IHDR chunk (4 + 4 + 13 + 4).
        const int afterIhdr = 8 + 25;
        output.Write(png[..afterIhdr]);
        if (icc is { Length: > 0 })
        {
            using var compressed = new MemoryStream();
            compressed.Write("icc\0\0"u8);   // profile name, NUL, compression method 0
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(icc);
            WritePngChunk(output, "iCCP"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        if (exif is not null)
            WritePngChunk(output, "eXIf"u8, exif);
        output.Write(png[afterIhdr..]);
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crc = Internal.Crc32.Update(Internal.Crc32.Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static void WriteWebp(ReadOnlySpan<byte> webp, byte[]? exif, byte[]? icc, bool hasAlpha, Stream output)
    {
        // Collect the chunks Skia wrote, then rebuild as an extended (VP8X) file: VP8X, ICCP, image chunks, EXIF.
        var chunks = new List<(string FourCc, int Offset, int Length)>();
        var pos = 12;
        while (pos + 8 <= webp.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.Slice(pos + 4, 4));
            if (size < 0 || pos + 8 + size > webp.Length) break;
            chunks.Add((System.Text.Encoding.ASCII.GetString(webp.Slice(pos, 4)), pos, 8 + size + (size & 1)));
            pos += 8 + size + (size & 1);
        }

        var (width, height) = WebpCanvasSize(webp, chunks);
        var flags = (byte)0;
        foreach (var c in chunks)
            if (c.FourCc == "VP8X") flags = webp[c.Offset + 8];
        if (hasAlpha || chunks.Exists(c => c.FourCc == "ALPH")) flags |= 0x10;
        if (icc is { Length: > 0 }) flags |= 0x20; else flags &= unchecked((byte)~0x20);
        if (exif is not null) flags |= 0x08; else flags &= unchecked((byte)~0x08);

        using var body = new MemoryStream();
        Span<byte> vp8x = stackalloc byte[18];
        "VP8X"u8.CopyTo(vp8x);
        BinaryPrimitives.WriteUInt32LittleEndian(vp8x[4..], 10);
        vp8x[8] = flags;
        WriteUInt24(vp8x[12..], width - 1);
        WriteUInt24(vp8x[15..], height - 1);
        body.Write(vp8x);
        if (icc is { Length: > 0 }) WriteRiffChunk(body, "ICCP"u8, icc);
        foreach (var c in chunks)
            if (c.FourCc is not ("VP8X" or "ICCP" or "EXIF"))
                body.Write(webp.Slice(c.Offset, c.Length));
        if (exif is not null) WriteRiffChunk(body, "EXIF"u8, exif);

        Span<byte> riff = stackalloc byte[12];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], (uint)(body.Length + 4));
        "WEBP"u8.CopyTo(riff[8..]);
        output.Write(riff);
        output.Write(body.GetBuffer().AsSpan(0, (int)body.Length));
    }

    private static (int Width, int Height) WebpCanvasSize(ReadOnlySpan<byte> webp, List<(string FourCc, int Offset, int Length)> chunks)
    {
        foreach (var c in chunks)
        {
            var data = webp.Slice(c.Offset + 8, c.Length - 8);
            switch (c.FourCc)
            {
                case "VP8X" when data.Length >= 10:
                    return (ReadUInt24(data[4..]) + 1, ReadUInt24(data[7..]) + 1);
                case "VP8 " when data.Length >= 10:
                    return (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF);
                case "VP8L" when data.Length >= 5:
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
        }
        return (1, 1);
    }

    private static void WriteRiffChunk(Stream output, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        fourCc.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
        output.Write(header);
        output.Write(data);
        if ((data.Length & 1) == 1) output.WriteByte(0);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> s) => s[0] | s[1] << 8 | s[2] << 16;

    private static void WriteUInt24(Span<byte> s, int value)
    {
        s[0] = (byte)value;
        s[1] = (byte)(value >> 8);
        s[2] = (byte)(value >> 16);
    }
}
