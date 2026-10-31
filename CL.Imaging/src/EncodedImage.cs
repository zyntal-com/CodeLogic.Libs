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
    /// reset to 1 when the pipeline turned the pixels upright. Null uses the inverse of
    /// <see cref="ImagingSettings.StripMetadata"/>. Ignored after <see cref="ImagePipeline.StripMetadata"/>.
    /// </summary>
    public bool? KeepMetadata { get; init; }

    /// <summary>
    /// With <see cref="KeepMetadata"/>: remove the GPS position from the EXIF that is written back (all other tags are
    /// kept). Default false: kept EXIF keeps its GPS. Without kept EXIF there is no GPS in the output anyway.
    /// </summary>
    public bool RemoveGps { get; init; }

    /// <summary>Colour transparent pixels are flattened onto when the format has no alpha (JPEG). Default: white.</summary>
    public Color Background { get; init; } = Color.White;
}

/// <summary>
/// Encoded image bytes in a pooled buffer, with their format and size. Dispose returns the buffer to the pool;
/// don't use <see cref="Span"/> or <see cref="Memory"/> after that (copy with <see cref="ToArray"/> to keep the bytes).
/// </summary>
public sealed class EncodedImage : IDisposable
{
    private readonly PooledBuffer _buffer;

    internal EncodedImage(PooledBuffer buffer, ImageFormat format, int width, int height)
    {
        _buffer = buffer;
        Format = format;
        Width = width;
        Height = height;
    }

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
    public ReadOnlySpan<byte> Span => _buffer.Span;

    /// <summary>The encoded bytes (valid until disposed).</summary>
    public ReadOnlyMemory<byte> Memory => _buffer.Memory;

    /// <summary>A copy of the bytes that outlives this object.</summary>
    public byte[] ToArray() => _buffer.Span.ToArray();

    /// <summary>A read-only stream over the bytes (valid until this object is disposed).</summary>
    public Stream AsStream() => new MemoryStream(_buffer.Array, 0, _buffer.Length, writable: false);

    /// <summary>The bytes as Base64.</summary>
    public string ToBase64() => Convert.ToBase64String(_buffer.Span);

    /// <summary>A <c>data:</c> URI, e.g. <c>data:image/webp;base64,…</c>.</summary>
    public string ToDataUri() => $"data:{MimeType};base64,{ToBase64()}";

    /// <summary>Writes the bytes to a stream.</summary>
    /// <param name="destination">The target stream.</param>
    /// <param name="ct">Cancellation.</param>
    public ValueTask WriteToAsync(Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.WriteAsync(_buffer.Memory, ct);
    }

    /// <summary>Writes the bytes to a file, creating its directory.</summary>
    /// <param name="path">The target path.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await file.WriteAsync(_buffer.Memory, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose() => _buffer.Dispose();
}
