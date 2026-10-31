using CL.Imaging.Errors;
using CL.Imaging.Internal;
using CL.Imaging.Metadata;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging;

/// <summary>What an image's header says, read without decoding any pixels.</summary>
/// <param name="Format">The format.</param>
/// <param name="Width">Stored width in pixels (before orientation).</param>
/// <param name="Height">Stored height in pixels (before orientation).</param>
/// <param name="Orientation">The EXIF orientation.</param>
/// <param name="HasAlphaChannel">Whether the format and header allow transparency (not whether any pixel is transparent).</param>
/// <param name="FrameCount">Number of frames (more than 1 for animations; only the first is ever decoded).</param>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height, ImageOrientation Orientation, bool HasAlphaChannel, int FrameCount)
{
    /// <summary>Width once the orientation is applied.</summary>
    public int DisplayWidth => Swaps ? Height : Width;

    /// <summary>Height once the orientation is applied.</summary>
    public int DisplayHeight => Swaps ? Width : Height;

    /// <summary>Width × height.</summary>
    public long PixelCount => (long)Width * Height;

    /// <summary>The MIME type of <see cref="Format"/>.</summary>
    public string MimeType => Format.MimeType();

    private bool Swaps => Orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;

    /// <summary>
    /// Reads format, size and orientation from the header. The stream is read only as far as the codec needs; a
    /// seekable stream is left where it was.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <returns>The header information, or <c>imaging.decode_failed</c> / <c>imaging.unsupported_format</c>.</returns>
    public static Result<ImageInfo> Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var start = stream.CanSeek ? stream.Position : -1;
            Span<byte> header = stackalloc byte[ImageFormatExtensions.SignatureLength];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var sniffed = ImageFormatExtensions.Detect(header[..read]);
            Stream source;
            if (start >= 0)
            {
                stream.Position = start;
                source = stream;
            }
            else
            {
                // Codecs rewind to the start after sniffing: replay the bytes already read and buffer the front.
                source = new SKFrontBufferedStream(new PrefixedStream(header[..read].ToArray(), stream), SKCodec.MinBufferedBytesNeeded, disposeUnderlyingStream: false);
            }
            try
            {
                // SKCodec owns the SKStream it is given; this wrapper leaves the caller's stream open.
                using var codec = SKCodec.Create(new SKManagedStream(source, disposeManagedStream: false));
                return codec is null ? Decoder.Unreadable(sniffed) : FromCodec(codec, sniffed);
            }
            finally
            {
                if (start >= 0 && stream.CanSeek) stream.Position = start;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Reads format, size and orientation from encoded bytes without decoding pixels.</summary>
    /// <param name="data">The encoded image (the beginning is enough for most formats).</param>
    /// <returns>The header information or an imaging error.</returns>
    public static Result<ImageInfo> Probe(ReadOnlySpan<byte> data)
    {
        try
        {
            var sniffed = ImageFormatExtensions.Detect(data[..Math.Min(data.Length, ImageFormatExtensions.SignatureLength)]);
            using var skData = SKData.CreateCopy(data);
            using var codec = skData is null ? null : SKCodec.Create(skData);
            return codec is null ? Decoder.Unreadable(sniffed) : FromCodec(codec, sniffed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Probes an image file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The header information, <c>imaging.file_not_found</c>, or an imaging error.</returns>
    public static Result<ImageInfo> ProbeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return ImagingErrors.FileNotFound(path);
        try
        {
            using var stream = File.OpenRead(path);
            return Probe(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    private static Result<ImageInfo> FromCodec(SKCodec codec, ImageFormat sniffed)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return ImagingErrors.DecodeFailed("The image has no pixels.");
        var format = ImageFormatExtensions.FromSkia(codec.EncodedFormat);
        if (format == ImageFormat.Unknown) format = sniffed;
        var origin = codec.EncodedOrigin;
        var orientation = origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default ? ImageOrientation.Normal : (ImageOrientation)(int)origin;
        return new ImageInfo(format, info.Width, info.Height, orientation, info.AlphaType != SKAlphaType.Opaque, Math.Max(1, codec.FrameCount));
    }
}

/// <summary>Replays bytes already read from a non-seekable stream before the rest of it.</summary>
internal sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return n;
        }
        return inner.Read(buffer);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Reads <see cref="ImageMetadata"/> without decoding pixels.</summary>
public static class ImageMetadataReader
{
    /// <summary>Reads metadata from encoded bytes.</summary>
    /// <param name="data">The whole encoded image (EXIF may sit at the end of PNG and WebP files).</param>
    /// <returns>The metadata, or <c>imaging.decode_failed</c> / <c>imaging.unsupported_format</c>.</returns>
    public static Result<ImageMetadata> Read(ReadOnlySpan<byte> data)
    {
        var probed = ImageInfo.Probe(data);
        if (probed.IsFailure) return probed.Error!;
        var info = probed.Value!;
        var blocks = ContainerReader.Extract(data, info.Format);
        var metadata = Decoder.BuildMetadata(info.Format, info.Width, info.Height, blocks,
            info.Orientation == ImageOrientation.Normal ? SKEncodedOrigin.TopLeft : (SKEncodedOrigin)(int)info.Orientation);
        return metadata;
    }

    /// <summary>Reads metadata from a stream (read to the end, up to <paramref name="maxBytes"/>).</summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <param name="maxBytes">Byte limit; null uses <see cref="ImagingDefaults"/>, 0 means none.</param>
    /// <param name="ct">Cancellation: <c>imaging.cancelled</c>.</param>
    /// <returns>The metadata or an imaging error.</returns>
    public static async Task<Result<ImageMetadata>> ReadAsync(Stream stream, long? maxBytes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var read = await StreamBuffer.ReadAllAsync(stream, maxBytes ?? ImagingDefaults.Current.MaxBytes, ct).ConfigureAwait(false);
            if (read.IsFailure) return read.Error!;
            using var buffer = read.Value!;
            return Read(buffer.Span);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }
}
