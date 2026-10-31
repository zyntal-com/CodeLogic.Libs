using System.Collections;
using CL.Imaging.Errors;
using CodeLogic.Core.Results;

namespace CL.Imaging;

/// <summary>Options for <see cref="ImageTools.ProcessUpload(Stream, UploadOptions?)"/>.</summary>
public sealed record UploadOptions
{
    /// <summary>Output format; null uses <see cref="ImagingDefaults"/> (WebP unless configured).</summary>
    public ImageFormat? Format { get; init; }

    /// <summary>Quality 1–100; null uses <see cref="ImagingDefaults"/>.</summary>
    public int? Quality { get; init; }

    /// <summary>Largest output width; the image is scaled down (never up) to fit. 0 = no limit.</summary>
    public int MaxWidth { get; init; }

    /// <summary>Largest output height; the image is scaled down (never up) to fit. 0 = no limit.</summary>
    public int MaxHeight { get; init; }

    /// <summary>Largest source image in pixels (checked from the header). Null uses <see cref="ImagingDefaults"/>; 0 = no limit.</summary>
    public long? MaxPixels { get; init; }

    /// <summary>Largest upload in bytes. Null uses <see cref="ImagingDefaults"/>; 0 = no limit.</summary>
    public long? MaxBytes { get; init; }

    /// <summary>Turn the image upright from its EXIF orientation (phone photos). Default: true.</summary>
    public bool ApplyOrientation { get; init; } = true;
}

/// <summary>Responsive variants of one image, smallest first. Dispose to return every buffer to the pool.</summary>
public sealed class ImageVariantSet : IReadOnlyList<EncodedImage>, IDisposable
{
    private readonly List<EncodedImage> _variants;

    internal ImageVariantSet(List<EncodedImage> variants) => _variants = variants;

    /// <inheritdoc/>
    public EncodedImage this[int index] => _variants[index];

    /// <inheritdoc/>
    public int Count => _variants.Count;

    /// <summary>Builds an HTML <c>srcset</c> value, e.g. <c>/img/a-320.webp 320w, /img/a-640.webp 640w</c>.</summary>
    /// <param name="url">The URL each variant will be served at.</param>
    public string ToSrcSet(Func<EncodedImage, string> url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return string.Join(", ", _variants.Select(v => $"{url(v)} {v.Width}w"));
    }

    /// <inheritdoc/>
    public IEnumerator<EncodedImage> GetEnumerator() => _variants.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Returns every buffer to the pool.</summary>
    public void Dispose()
    {
        foreach (var v in _variants) v.Dispose();
        _variants.Clear();
    }
}

/// <summary>One-call helpers for common web tasks: safe upload processing, responsive variants, thumbnails, data URIs.</summary>
public static class ImageTools
{
    /// <summary>
    /// Processes an untrusted upload safely: refuses it over the byte or pixel limit before decoding, turns it upright,
    /// scales it down into <see cref="UploadOptions.MaxWidth"/> × <see cref="UploadOptions.MaxHeight"/> and re-encodes it.
    /// Only pixels are kept, so EXIF (including GPS positions) and other metadata are dropped. Animated images keep
    /// their first frame.
    /// </summary>
    /// <param name="stream">The upload.</param>
    /// <param name="options">Options; null uses the defaults.</param>
    /// <returns>
    /// The encoded image, or <c>imaging.decode_failed</c>, <c>imaging.unsupported_format</c>, <c>imaging.too_large</c>,
    /// <c>imaging.invalid_argument</c>, <c>imaging.encode_failed</c>.
    /// </returns>
    public static Result<EncodedImage> ProcessUpload(Stream stream, UploadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new UploadOptions();
        if (Validate(options) is { } invalid) return invalid;
        var loaded = ImageDocument.Load(stream, LoadOptions(options));
        if (loaded.IsFailure) return loaded.Error!;
        using var document = loaded.Value!;
        return Process(document, options, CancellationToken.None);
    }

    /// <summary>Asynchronous <see cref="ProcessUpload(Stream, UploadOptions?)"/>: the upload is read asynchronously.</summary>
    /// <param name="stream">The upload.</param>
    /// <param name="options">Options; null uses the defaults.</param>
    /// <param name="ct">Cancellation: <c>imaging.cancelled</c>.</param>
    /// <returns>The encoded image or an imaging error.</returns>
    public static async Task<Result<EncodedImage>> ProcessUploadAsync(Stream stream, UploadOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new UploadOptions();
        if (Validate(options) is { } invalid) return invalid;
        var loaded = await ImageDocument.LoadAsync(stream, LoadOptions(options), ct).ConfigureAwait(false);
        if (loaded.IsFailure) return loaded.Error!;
        using var document = loaded.Value!;
        return Process(document, options, ct);
    }

    private static Result<EncodedImage> Process(ImageDocument document, UploadOptions options, CancellationToken ct)
    {
        var pipeline = document.Edit();
        if (options.ApplyOrientation) pipeline.AutoOrient();
        if (options.MaxWidth > 0 || options.MaxHeight > 0) pipeline.Resize(options.MaxWidth, options.MaxHeight, ResizeMode.Max);
        return pipeline.StripMetadata().Encode(new ImageEncodeOptions { Format = options.Format, Quality = options.Quality }, ct);
    }

    private static Error? Validate(UploadOptions options) =>
        options.MaxWidth < 0 || options.MaxHeight < 0 ? ImagingErrors.InvalidArgument("MaxWidth and MaxHeight must be 0 or more.")
        : options.Quality is < 1 or > 100 ? ImagingErrors.InvalidArgument("Quality must be between 1 and 100.")
        : null;

    private static ImageLoadOptions LoadOptions(UploadOptions options) => new() { MaxPixels = options.MaxPixels, MaxBytes = options.MaxBytes };

    /// <summary>
    /// Encodes responsive variants at the given widths from one decoded image: upright, metadata stripped, never
    /// scaled up. Widths at or above the image's own width are replaced by one variant at the image's width, so the
    /// largest requested size is still covered by the best available.
    /// </summary>
    /// <param name="image">The decoded image.</param>
    /// <param name="widths">Target widths, e.g. <c>[320, 640, 1280]</c>.</param>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="ct">Checked between variants: <c>imaging.cancelled</c>.</param>
    /// <returns>The variants, smallest first, or the first error.</returns>
    public static Result<ImageVariantSet> GenerateVariants(ImageDocument image, IEnumerable<int> widths, ImageFormat? format = null,
        int? quality = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(widths);
        var list = widths.Distinct().Order().ToList();
        if (list.Count == 0 || list[0] <= 0) return ImagingErrors.InvalidArgument("Widths must be a non-empty list of positive numbers.");

        var upright = image.Edit().AutoOrient().Apply(ct);
        if (upright.IsFailure) return upright.Error!;
        using var source = upright.Value!;
        var fitting = list.Where(w => w < source.Width).ToList();
        if (list.Exists(w => w >= source.Width)) fitting.Add(source.Width);

        var variants = new List<EncodedImage>(fitting.Count);
        try
        {
            foreach (var width in fitting)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                var pipeline = source.Edit().StripMetadata();
                if (width != source.Width) pipeline.Resize(width, 0, ResizeMode.Fit);
                var encoded = pipeline.Encode(format, quality, ct);
                if (encoded.IsFailure) throw new VariantFailedException(encoded.Error!);
                variants.Add(encoded.Value!);
            }
            return new ImageVariantSet(variants);
        }
        catch (Exception ex) when (ex is VariantFailedException or OperationCanceledException)
        {
            foreach (var v in variants) v.Dispose();
            return ex is VariantFailedException vf ? vf.Error : ImagingErrors.Cancelled();
        }
    }

    /// <summary>Decodes a stream once and encodes responsive variants (see <see cref="GenerateVariants(ImageDocument, IEnumerable{int}, ImageFormat?, int?, CancellationToken)"/>).</summary>
    /// <param name="stream">The source image.</param>
    /// <param name="widths">Target widths.</param>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="loadOptions">Decode limits.</param>
    /// <param name="ct">Cancellation: <c>imaging.cancelled</c>.</param>
    /// <returns>The variants or an imaging error.</returns>
    public static async Task<Result<ImageVariantSet>> GenerateVariantsAsync(Stream stream, IEnumerable<int> widths, ImageFormat? format = null,
        int? quality = null, ImageLoadOptions? loadOptions = null, CancellationToken ct = default)
    {
        var loaded = await ImageDocument.LoadAsync(stream, loadOptions, ct).ConfigureAwait(false);
        if (loaded.IsFailure) return loaded.Error!;
        using var document = loaded.Value!;
        return GenerateVariants(document, widths, format, quality, ct);
    }

    /// <summary>A thumbnail: upright, metadata stripped, scaled with <paramref name="mode"/> (cover by default).</summary>
    /// <param name="image">The decoded image.</param>
    /// <param name="width">Thumbnail width.</param>
    /// <param name="height">Thumbnail height.</param>
    /// <param name="mode">How to fit; <see cref="ResizeMode.Fill"/> crops to exactly the size.</param>
    /// <param name="smartCrop">With <see cref="ResizeMode.Fill"/>: keep the most detailed region instead of the centre.</param>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The encoded thumbnail or an imaging error.</returns>
    public static Result<EncodedImage> Thumbnail(ImageDocument image, int width, int height, ResizeMode mode = ResizeMode.Fill,
        bool smartCrop = false, ImageFormat? format = null, int? quality = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var pipeline = image.Edit().AutoOrient();
        if (smartCrop && mode == ResizeMode.Fill) pipeline.SmartCrop(width, height);
        else pipeline.Resize(width, height, mode);
        return pipeline.StripMetadata().Encode(format, quality);
    }

    /// <summary>Decodes a stream and makes a thumbnail (see <see cref="Thumbnail(ImageDocument, int, int, ResizeMode, bool, ImageFormat?, int?)"/>).</summary>
    /// <param name="stream">The source image.</param>
    /// <param name="width">Thumbnail width.</param>
    /// <param name="height">Thumbnail height.</param>
    /// <param name="mode">How to fit.</param>
    /// <param name="smartCrop">Keep the most detailed region (with <see cref="ResizeMode.Fill"/>).</param>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="ct">Cancellation: <c>imaging.cancelled</c>.</param>
    /// <returns>The encoded thumbnail or an imaging error.</returns>
    public static async Task<Result<EncodedImage>> ThumbnailAsync(Stream stream, int width, int height, ResizeMode mode = ResizeMode.Fill,
        bool smartCrop = false, ImageFormat? format = null, int? quality = null, CancellationToken ct = default)
    {
        var loaded = await ImageDocument.LoadAsync(stream, null, ct).ConfigureAwait(false);
        if (loaded.IsFailure) return loaded.Error!;
        using var document = loaded.Value!;
        return Thumbnail(document, width, height, mode, smartCrop, format, quality);
    }

    /// <summary>Encodes the image and returns it as a <c>data:</c> URI.</summary>
    /// <param name="image">The image.</param>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <returns>The data URI or an imaging error.</returns>
    public static Result<string> ToDataUri(ImageDocument image, ImageFormat? format = null, int? quality = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var encoded = image.Edit().StripMetadata().Encode(format, quality);
        if (encoded.IsFailure) return encoded.Error!;
        using var e = encoded.Value!;
        return e.ToDataUri();
    }

    /// <summary>Wraps already-encoded bytes in a <c>data:</c> URI, detecting the MIME type from the signature.</summary>
    /// <param name="encoded">The encoded image.</param>
    /// <returns>The data URI, or <c>imaging.decode_failed</c> when the bytes are not a recognised image.</returns>
    public static Result<string> ToDataUri(ReadOnlySpan<byte> encoded)
    {
        var format = ImageFormatExtensions.Detect(encoded[..Math.Min(encoded.Length, ImageFormatExtensions.SignatureLength)]);
        if (format == ImageFormat.Unknown) return ImagingErrors.DecodeFailed("The data is not a recognised image format.");
        return $"data:{format.MimeType()};base64,{Convert.ToBase64String(encoded)}";
    }

    /// <summary>The size of a <paramref name="width"/>×<paramref name="height"/> image scaled down (never up) to fit the box (0 = no limit on that side).</summary>
    /// <param name="width">Image width, greater than 0.</param>
    /// <param name="height">Image height, greater than 0.</param>
    /// <param name="maxWidth">Box width; 0 = no limit.</param>
    /// <param name="maxHeight">Box height; 0 = no limit.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public static (int Width, int Height) FitWithin(int width, int height, int maxWidth, int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var scale = 1d;
        if (maxWidth > 0) scale = Math.Min(scale, (double)maxWidth / width);
        if (maxHeight > 0) scale = Math.Min(scale, (double)maxHeight / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private sealed class VariantFailedException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }
}
