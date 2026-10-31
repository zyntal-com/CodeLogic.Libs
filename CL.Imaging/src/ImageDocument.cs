using CL.Imaging.Errors;
using CL.Imaging.Internal;
using CL.Imaging.Metadata;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging;

/// <summary>Options for loading an image.</summary>
public sealed record ImageLoadOptions
{
    /// <summary>
    /// Largest image, in pixels, that is decoded; checked from the header before any pixel memory is allocated, so a
    /// small file that claims a huge size is refused (<c>imaging.too_large</c>). Null uses
    /// <see cref="ImagingDefaults"/>; 0 means no limit.
    /// </summary>
    public long? MaxPixels { get; init; }

    /// <summary>Largest encoded input, in bytes, that is read (<c>imaging.too_large</c>). Null uses <see cref="ImagingDefaults"/>; 0 means no limit.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Whether to read EXIF and the ICC profile. Default: true.</summary>
    public bool ReadMetadata { get; init; } = true;

    internal long EffectiveMaxPixels => MaxPixels ?? ImagingDefaults.Current.MaxPixels;
    internal long EffectiveMaxBytes => MaxBytes ?? ImagingDefaults.Current.MaxBytes;
}

/// <summary>
/// A decoded image held in memory (8-bit RGBA), with the metadata it was loaded with. Edit it with <see cref="Edit"/>:
/// the pipeline never changes the document it starts from. Dispose it to release the pixel memory.
/// </summary>
/// <remarks>
/// The pixels are kept as stored: an EXIF orientation is not applied on load (see <see cref="Orientation"/> and
/// <see cref="ImagePipeline.AutoOrient"/>), and pixels stay in the source colour space so the original ICC profile can
/// be written back on encode (<see cref="ImageEncodeOptions.KeepIccProfile"/>); otherwise they are converted to sRGB
/// when encoded.
/// </remarks>
public sealed class ImageDocument : IDisposable
{
    private SKBitmap? _bitmap;
    private readonly byte[]? _exif;
    private readonly byte[]? _icc;

    internal ImageDocument(SKBitmap bitmap, ImageFormat sourceFormat, ImageOrientation orientation, ImageMetadata metadata, byte[]? exif, byte[]? icc)
    {
        _bitmap = bitmap;
        SourceFormat = sourceFormat;
        Orientation = orientation;
        Metadata = metadata;
        _exif = exif;
        _icc = icc;
    }

    /// <summary>Width of the pixels in memory.</summary>
    public int Width => Bitmap.Width;

    /// <summary>Height of the pixels in memory.</summary>
    public int Height => Bitmap.Height;

    /// <summary>Width once <see cref="Orientation"/> is applied.</summary>
    public int DisplayWidth => Swaps(Orientation) ? Height : Width;

    /// <summary>Height once <see cref="Orientation"/> is applied.</summary>
    public int DisplayHeight => Swaps(Orientation) ? Width : Height;

    /// <summary>The format the image was decoded from (<see cref="ImageFormat.Unknown"/> for created images).</summary>
    public ImageFormat SourceFormat { get; }

    /// <summary>The EXIF orientation still to apply to the pixels; <see cref="ImageOrientation.Normal"/> once upright.</summary>
    public ImageOrientation Orientation { get; }

    /// <summary>The metadata read on load.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>The embedded ICC profile bytes, or empty.</summary>
    public ReadOnlyMemory<byte> IccProfile => _icc;

    /// <summary>The raw EXIF block (a TIFF structure), or empty.</summary>
    public ReadOnlyMemory<byte> ExifData => _exif;

    internal byte[]? Exif => _exif;
    internal byte[]? Icc => _icc;
    internal SKBitmap Bitmap => _bitmap ?? throw new ObjectDisposedException(nameof(ImageDocument));

    // ── Loading ────────────────────────────────────────────────────────────────

    /// <summary>Decodes an image from a stream (read to the end, up to <see cref="ImageLoadOptions.MaxBytes"/>).</summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <param name="options">Limits; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The document, or <c>imaging.decode_failed</c>, <c>imaging.unsupported_format</c>, <c>imaging.too_large</c>.</returns>
    public static Result<ImageDocument> Load(Stream stream, ImageLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ImageLoadOptions();
        try
        {
            var read = StreamBuffer.ReadAll(stream, options.EffectiveMaxBytes);
            if (read.IsFailure) return read.Error!;
            using var buffer = read.Value!;
            return Decoder.Decode(buffer.Span, options.EffectiveMaxPixels, options.ReadMetadata, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Reads a stream asynchronously and decodes it.</summary>
    /// <param name="stream">A readable stream positioned at the image.</param>
    /// <param name="options">Limits; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="ct">Cancels the read (and the decode if it has not started): <c>imaging.cancelled</c>.</param>
    /// <returns>The document or an imaging error.</returns>
    public static async Task<Result<ImageDocument>> LoadAsync(Stream stream, ImageLoadOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ImageLoadOptions();
        try
        {
            var read = await StreamBuffer.ReadAllAsync(stream, options.EffectiveMaxBytes, ct).ConfigureAwait(false);
            if (read.IsFailure) return read.Error!;
            using var buffer = read.Value!;
            return Decoder.Decode(buffer.Span, options.EffectiveMaxPixels, options.ReadMetadata, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Decodes an image from bytes.</summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="options">Limits; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The document or an imaging error.</returns>
    public static Result<ImageDocument> Load(ReadOnlySpan<byte> data, ImageLoadOptions? options = null)
    {
        options ??= new ImageLoadOptions();
        var maxBytes = options.EffectiveMaxBytes;
        if (maxBytes > 0 && data.Length > maxBytes)
            return ImagingErrors.TooLarge($"The image data is larger than the limit of {maxBytes:N0} bytes.");
        try
        {
            return Decoder.Decode(data, options.EffectiveMaxPixels, options.ReadMetadata, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Decodes an image file.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Limits; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="ct">Cancellation: <c>imaging.cancelled</c>.</param>
    /// <returns>The document, <c>imaging.file_not_found</c>, or an imaging error.</returns>
    public static async Task<Result<ImageDocument>> LoadFileAsync(string path, ImageLoadOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return ImagingErrors.FileNotFound(path);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return await LoadAsync(stream, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Decodes an image file synchronously.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Limits; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The document, <c>imaging.file_not_found</c>, or an imaging error.</returns>
    public static Result<ImageDocument> LoadFile(string path, ImageLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return ImagingErrors.FileNotFound(path);
        try
        {
            using var stream = File.OpenRead(path);
            return Load(stream, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    // ── Creating ───────────────────────────────────────────────────────────────

    /// <summary>Creates a blank image filled with <paramref name="background"/>.</summary>
    /// <param name="width">Width in pixels, 1–32767.</param>
    /// <param name="height">Height in pixels, 1–32767.</param>
    /// <param name="background">Fill colour (e.g. <see cref="Color.Transparent"/>).</param>
    /// <returns>The document or <c>imaging.invalid_argument</c>.</returns>
    public static Result<ImageDocument> Create(int width, int height, Color background)
    {
        if (width is <= 0 or > SkiaOps.MaxDimension || height is <= 0 or > SkiaOps.MaxDimension || (long)width * height > SkiaOps.MaxOutputPixels)
            return ImagingErrors.InvalidArgument($"Width and height must be 1–{SkiaOps.MaxDimension} with at most {SkiaOps.MaxOutputPixels:N0} pixels.");
        try
        {
            var bitmap = SkiaOps.Create(width, height, null);
            bitmap.Erase(background);
            return FromOwnedBitmap(bitmap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    /// <summary>Creates a document from a copy of a SkiaSharp bitmap (any colour type; converted to RGBA).</summary>
    /// <param name="bitmap">The bitmap; it stays owned by the caller.</param>
    /// <returns>The document or an imaging error.</returns>
    public static Result<ImageDocument> FromSkiaBitmap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width <= 0 || bitmap.Height <= 0) return ImagingErrors.InvalidArgument("The bitmap is empty.");
        try
        {
            var copy = SkiaOps.Create(bitmap.Width, bitmap.Height, bitmap.ColorSpace is { IsSrgb: false } cs ? cs : null);
            using (var src = bitmap.PeekPixels())
            using (var dst = copy.PeekPixels())
            {
                if (src is null || !src.ReadPixels(dst))
                {
                    copy.Dispose();
                    return ImagingErrors.InvalidArgument("The bitmap's pixels cannot be read.");
                }
            }
            return FromOwnedBitmap(copy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
    }

    internal static ImageDocument FromOwnedBitmap(SKBitmap bitmap, ImageDocument? template = null, ImageOrientation? orientation = null, bool stripMetadata = false)
    {
        var o = orientation ?? template?.Orientation ?? ImageOrientation.Normal;
        var metadata = (template?.Metadata ?? new ImageMetadata()) with { Width = bitmap.Width, Height = bitmap.Height, Orientation = o };
        if (stripMetadata) metadata = new ImageMetadata { Format = metadata.Format, Width = bitmap.Width, Height = bitmap.Height };
        return new ImageDocument(bitmap, template?.SourceFormat ?? ImageFormat.Unknown, o, metadata,
            stripMetadata ? null : template?.Exif, stripMetadata ? null : template?.Icc);
    }

    // ── Using ──────────────────────────────────────────────────────────────────

    /// <summary>Starts an editing pipeline on this document. Nothing runs until <see cref="ImagePipeline.Apply"/> or <see cref="ImagePipeline.Encode(ImageEncodeOptions, CancellationToken)"/>.</summary>
    public ImagePipeline Edit() => new(this);

    /// <summary>Encodes the document as it is (no edits).</summary>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The encoded image or an imaging error.</returns>
    public Result<EncodedImage> Encode(ImageFormat? format = null, int? quality = null) => Edit().Encode(format, quality);

    /// <summary>Encodes the document as it is (no edits).</summary>
    /// <param name="options">Encoder options.</param>
    /// <returns>The encoded image or an imaging error.</returns>
    public Result<EncodedImage> Encode(ImageEncodeOptions options) => Edit().Encode(options);

    /// <summary>The colour of one pixel (unpremultiplied).</summary>
    /// <param name="x">Column, 0 ≤ x &lt; <see cref="Width"/>.</param>
    /// <param name="y">Row, 0 ≤ y &lt; <see cref="Height"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The point is outside the image.</exception>
    public Color GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Bitmap.GetPixel(x, y);
    }

    /// <summary>A deep copy (pixels and metadata).</summary>
    public ImageDocument Clone() => new(Bitmap.Copy(), SourceFormat, Orientation, Metadata, _exif, _icc);

    /// <summary>A copy of the pixels as a SkiaSharp bitmap, for interop; the caller disposes it.</summary>
    public SKBitmap ToSkiaBitmap() => Bitmap.Copy();

    /// <summary>Releases the pixel memory.</summary>
    public void Dispose() => Interlocked.Exchange(ref _bitmap, null)?.Dispose();

    private static bool Swaps(ImageOrientation o) =>
        o is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;
}
