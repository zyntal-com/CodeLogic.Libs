using CL.Imaging.Internal;

namespace CL.Imaging;

/// <summary>Options for encoding.</summary>
public sealed record ImageEncodeOptions
{
    /// <summary>Output format (JPEG, PNG or WebP). Null uses <see cref="ImagingDefaults"/>.</summary>
    public ImageFormat? Format { get; init; }

    /// <summary>Quality 1–100 for JPEG and lossy WebP; for lossless WebP it is the compression effort. Null uses <see cref="ImagingDefaults"/>.</summary>
    public int? Quality { get; init; }

    /// <summary>Encode WebP losslessly. Ignored for other formats (PNG is always lossless, JPEG never).</summary>
    public bool Lossless { get; init; }

    /// <summary>
    /// Write the source ICC colour profile into the output (byte for byte) and keep the pixels in that colour space.
    /// When false (the default) pixels are converted to sRGB and no profile is written.
    /// </summary>
    public bool KeepIccProfile { get; init; }

    /// <summary>
    /// Write the source EXIF block into the output (JPEG APP1, PNG <c>eXIf</c>, WebP <c>EXIF</c>); its orientation tag is
    /// reset to 1 when the pipeline turned the pixels upright, and the IFD1 thumbnail and the MakerNote are removed (both
    /// describe the source pixels and can hold a preview or a vendor GPS copy). Null uses the inverse of
    /// <see cref="ImagingSettings.StripMetadata"/>. Ignored after <see cref="ImagePipeline.StripMetadata"/>.
    /// </summary>
    public bool? KeepMetadata { get; init; }

    /// <summary>
    /// With <see cref="KeepMetadata"/>: remove the GPS position from the EXIF that is written back (the GPS values are
    /// zeroed and the GPS IFD emptied; together with the thumbnail and MakerNote removal no coordinates remain). Default false: kept EXIF keeps its GPS. Without kept EXIF there is no GPS in the output anyway.
    /// </summary>
    public bool RemoveGps { get; init; }

    /// <summary>Colour transparent pixels are flattened onto when the format has no alpha (JPEG). Default: white.</summary>
    public Color Background { get; init; } = Color.White;
}

/// <summary>
/// Encoded image bytes in a pooled buffer, with their format and size. Dispose releases the buffer; don't use
/// <see cref="Span"/> or <see cref="Memory"/> after that (copy with <see cref="ToArray"/> to keep the bytes). A stream
/// from <see cref="AsStream"/> holds its own reference to the buffer, so it stays valid until it is disposed itself,
/// even after the image is.
/// </summary>
public sealed class EncodedImage : IDisposable
{
    private readonly PooledBuffer _buffer;
    private int _disposed;

    internal EncodedImage(PooledBuffer buffer, ImageFormat format, int width, int height)
    {
        _buffer = buffer;
        Format = format;
        Width = width;
        Height = height;
    }

    private PooledBuffer Buffer => _disposed == 0 ? _buffer : throw new ObjectDisposedException(nameof(EncodedImage));

    /// <summary>The format of the bytes.</summary>
    public ImageFormat Format { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Number of encoded bytes.</summary>
    public int Length => _buffer.Length;

    /// <summary>The MIME type, e.g. <c>image/webp</c>.</summary>
    public string MimeType => Format.MimeType();

    /// <summary>The encoded bytes (valid until disposed).</summary>
    public ReadOnlySpan<byte> Span => Buffer.Span;

    /// <summary>The encoded bytes (valid until disposed).</summary>
    public ReadOnlyMemory<byte> Memory => Buffer.Memory;

    /// <summary>A copy of the bytes that outlives this object.</summary>
    public byte[] ToArray() => Buffer.Span.ToArray();

    /// <summary>
    /// A read-only stream over the bytes. It keeps the buffer alive on its own: dispose the stream when done, and the
    /// bytes stay valid for it even after this object is disposed.
    /// </summary>
    public Stream AsStream() => PooledBufferStream.Open(Buffer);

    /// <summary>The bytes as Base64.</summary>
    public string ToBase64() => Convert.ToBase64String(Buffer.Span);

    /// <summary>A <c>data:</c> URI, e.g. <c>data:image/webp;base64,…</c>.</summary>
    public string ToDataUri() => $"data:{MimeType};base64,{ToBase64()}";

    /// <summary>Writes the bytes to a stream.</summary>
    /// <param name="destination">The target stream.</param>
    /// <param name="ct">Cancellation.</param>
    public ValueTask WriteToAsync(Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.WriteAsync(Buffer.Memory, ct);
    }

    /// <summary>Writes the bytes to a file, creating its directory.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var memory = Buffer.Memory;
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await file.WriteAsync(memory, ct).ConfigureAwait(false);
    }

    /// <summary>Releases this object's reference to the buffer; the array returns to the pool once every stream from <see cref="AsStream"/> is disposed too.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _buffer.Dispose();
    }
}
