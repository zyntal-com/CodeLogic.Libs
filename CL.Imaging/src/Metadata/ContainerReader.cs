using System.Buffers.Binary;
using System.IO.Compression;

namespace CL.Imaging.Metadata;

/// <summary>Raw metadata blocks found in an encoded image.</summary>
/// <param name="Exif">The EXIF block as a TIFF structure (starting with <c>II</c> or <c>MM</c>), or null.</param>
/// <param name="Icc">The embedded ICC colour profile, or null.</param>
internal readonly record struct MetadataBlocks(byte[]? Exif, byte[]? Icc);

/// <summary>Finds the EXIF and ICC blocks in JPEG, PNG and WebP containers without decoding pixels.</summary>
internal static class ContainerReader
{
    private const int MaxIccBytes = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> IccHeader => "ICC_PROFILE\0"u8;

    public static MetadataBlocks Extract(ReadOnlySpan<byte> data, ImageFormat format)
    {
        try
        {
            return format switch
            {
                ImageFormat.Jpeg => ReadJpeg(data),
                ImageFormat.Png => ReadPng(data),
                ImageFormat.Webp => ReadWebp(data),
                _ => default,
            };
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            // Malformed metadata never fails the image itself.
            return default;
        }
    }

    private static MetadataBlocks ReadJpeg(ReadOnlySpan<byte> data)
    {
        byte[]? exif = null;
        SortedDictionary<int, byte[]>? iccChunks = null;
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            var marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }                 // fill byte
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { pos += 2; continue; }
            if (marker is 0xDA or 0xD9) break;                       // start of scan / end of image
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > data.Length) break;
            var payload = data.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && exif is null && payload.StartsWith(ExifHeader))
                exif = payload[ExifHeader.Length..].ToArray();
            else if (marker == 0xE2 && payload.Length > IccHeader.Length + 2 && payload.StartsWith(IccHeader))
            {
                iccChunks ??= [];
                iccChunks[payload[IccHeader.Length]] = payload[(IccHeader.Length + 2)..].ToArray();
            }
            pos += 2 + length;
        }

        byte[]? icc = null;
        if (iccChunks is { Count: > 0 })
        {
            icc = new byte[iccChunks.Values.Sum(c => c.Length)];
            var offset = 0;
            foreach (var chunk in iccChunks.Values)
            {
                chunk.CopyTo(icc, offset);
                offset += chunk.Length;
            }
        }
        return new MetadataBlocks(exif, icc);
    }

    private static MetadataBlocks ReadPng(ReadOnlySpan<byte> data)
    {
        byte[]? exif = null, icc = null;
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length) break;
            var type = data.Slice(pos + 4, 4);
            var body = data.Slice(pos + 8, (int)length);
            if (type.SequenceEqual("eXIf"u8)) exif = body.ToArray();
            else if (type.SequenceEqual("iCCP"u8))
            {
                var nul = body.IndexOf((byte)0);
                if (nul is > 0 and < 80 && nul + 2 <= body.Length && body[nul + 1] == 0)
                    icc = Inflate(body[(nul + 2)..]);
            }
            else if (type.SequenceEqual("IEND"u8)) break;
            pos += 12 + (int)length;
        }
        return new MetadataBlocks(exif, icc);
    }

    private static MetadataBlocks ReadWebp(ReadOnlySpan<byte> data)
    {
        byte[]? exif = null, icc = null;
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            if (size > int.MaxValue || pos + 8 + (long)size > data.Length) break;
            var fourcc = data.Slice(pos, 4);
            var body = data.Slice(pos + 8, (int)size);
            if (fourcc.SequenceEqual("EXIF"u8))
                exif = (body.StartsWith(ExifHeader) ? body[ExifHeader.Length..] : body).ToArray();
            else if (fourcc.SequenceEqual("ICCP"u8))
                icc = body.ToArray();
            pos += 8 + (int)size + (int)(size & 1);
        }
        return new MetadataBlocks(exif, icc);
    }

    private static byte[]? Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = zlib.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxIccBytes) return null;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
