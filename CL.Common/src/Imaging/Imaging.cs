using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Common.Imaging;

/// <summary>
/// Specifies the target output format for image conversion operations.
/// </summary>
public enum ImageFormat
{
    /// <summary>JPEG format — lossy compression, suitable for photographs.</summary>
    Jpeg,

    /// <summary>PNG format — lossless compression, suitable for graphics and transparency.</summary>
    Png,

    /// <summary>WebP format — modern format with both lossy and lossless modes.</summary>
    Webp
}

/// <summary>
/// Provides image processing utilities including validation, resizing, conversion, thumbnail creation,
/// and Base64 encoding. All operations are implemented using SkiaSharp.
/// Methods that can fail return <see cref="Result{T}"/> or <see cref="Result"/> instead of throwing.
/// Decoding methods refuse images over <see cref="DefaultMaxPixels"/> (checked from the header) and turn
/// photos upright from their EXIF orientation; size checks read only the header.
/// For user uploads use <see cref="ProcessImage"/>.
/// </summary>
public static partial class CLU_Imaging
{
    /// <summary>
    /// Checks whether the image contained in the given stream does not exceed the specified dimensions.
    /// Only the header is read, so a huge image is not decoded to find out.
    /// </summary>
    /// <param name="imageStream">A readable stream containing image data. Must not be null.</param>
    /// <param name="maxWidth">Maximum allowed width in pixels.</param>
    /// <param name="maxHeight">Maximum allowed height in pixels.</param>
    /// <returns><c>true</c> if the image dimensions are within the allowed bounds; otherwise <c>false</c>.</returns>
    public static bool IsValidSize(Stream imageStream, int maxWidth, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        try
        {
            return ReadHeader(imageStream) is { } header && header.Width > 0 && header.Height > 0
                && header.Width <= maxWidth && header.Height <= maxHeight;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the image in the stream has a valid (non-degenerate) aspect ratio.
    /// An aspect ratio is considered invalid if either dimension is zero. Only the header is read.
    /// </summary>
    /// <param name="imageStream">A readable stream containing image data. Must not be null.</param>
    /// <returns><c>true</c> if the image has non-zero width and height; otherwise <c>false</c>.</returns>
    public static bool IsValidAspectRatio(Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        try
        {
            return ReadHeader(imageStream) is { } header && header.Width > 0 && header.Height > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts the image in the given stream to the specified <see cref="ImageFormat"/> (quality 90), upright
    /// from its EXIF orientation. Images over <see cref="DefaultMaxPixels"/> are refused
    /// (<c>imaging.too_large</c>); use <see cref="ProcessImage"/> to choose the limit, quality or a size box.
    /// </summary>
    /// <param name="imageStream">A readable stream containing the source image. Must not be null.</param>
    /// <param name="format">The target image format.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="MemoryStream"/> with the converted image data on success,
    /// or a failure result with a descriptive error.
    /// </returns>
    public static Result<MemoryStream> ConvertImage(Stream imageStream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        var processed = ProcessImage(imageStream, new ImageProcessOptions { Format = format, Quality = 90 });
        if (processed.IsSuccess)
            return Result<MemoryStream>.Success(ToStream(processed.Value!.Data));
        var error = processed.Error!;
        return Result<MemoryStream>.Failure(error.Code == "imaging.process_failed"
            ? Error.Internal("imaging.convert_failed", error.Message, error.Details)
            : error);
    }

    /// <summary>
    /// Resizes the image in the given stream to the specified dimensions and encodes it as PNG.
    /// </summary>
    /// <param name="imageStream">A readable stream containing the source image. Must not be null.</param>
    /// <param name="targetWidth">The desired output width in pixels.</param>
    /// <param name="targetHeight">The desired output height in pixels.</param>
    /// <param name="allowCrop">
    /// When <c>true</c>, the image is scaled to cover the target and cropped to its exact dimensions.
    /// When <c>false</c>, the image is scaled to fit within the bounds, preserving aspect ratio.
    /// Default: <c>true</c>.
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="MemoryStream"/> with PNG-encoded resized image data on success,
    /// or a failure result with a descriptive error.
    /// </returns>
    public static Result<MemoryStream> ResizeImage(Stream imageStream, int targetWidth, int targetHeight, bool allowCrop = true) =>
        ResizeImage(imageStream, targetWidth, targetHeight, allowCrop, ImageFormat.Png, 100);

    /// <summary>
    /// Resizes the image in the given stream and encodes it to <paramref name="format"/>. The image is turned
    /// upright from its EXIF orientation first; images over <see cref="DefaultMaxPixels"/> are refused.
    /// </summary>
    /// <param name="imageStream">A readable stream containing the source image. Must not be null.</param>
    /// <param name="targetWidth">The desired output width in pixels.</param>
    /// <param name="targetHeight">The desired output height in pixels.</param>
    /// <param name="allowCrop">
    /// When <c>true</c>, the image is scaled to cover the target and cropped to its exact dimensions.
    /// When <c>false</c>, the image is scaled to fit within the bounds, preserving aspect ratio.
    /// </param>
    /// <param name="format">The output format.</param>
    /// <param name="quality">Encoder quality, 1–100.</param>
    /// <returns>The encoded image, or a failure.</returns>
    public static Result<MemoryStream> ResizeImage(Stream imageStream, int targetWidth, int targetHeight, bool allowCrop, ImageFormat format, int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        if (targetWidth <= 0 || targetHeight <= 0)
            return Result<MemoryStream>.Failure(Error.Validation("imaging.invalid_options", "Target width and height must be greater than zero."));
        if (quality is < 1 or > 100)
            return Result<MemoryStream>.Failure(Error.Validation("imaging.invalid_options", "Quality must be between 1 and 100."));
        try
        {
            var decoded = Decode(imageStream, DefaultMaxPixels, applyOrientation: true);
            if (decoded.IsFailure)
                return Result<MemoryStream>.Failure(decoded.Error!);
            using var source = decoded.Value!;
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

            SKBitmap resized;
            if (allowCrop)
            {
                // Scale to cover, then crop the centre.
                var scale = Math.Max((double)targetWidth / source.Width, (double)targetHeight / source.Height);
                var scaledW = Math.Max(targetWidth, (int)Math.Ceiling(source.Width * scale));
                var scaledH = Math.Max(targetHeight, (int)Math.Ceiling(source.Height * scale));
                using var cover = source.Resize(new SKImageInfo(scaledW, scaledH, SKColorType.Rgba8888, SKAlphaType.Premul), sampling);
                resized = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(resized);
                using var coverImage = SKImage.FromBitmap(cover);
                canvas.DrawImage(coverImage, -(scaledW - targetWidth) / 2f, -(scaledH - targetHeight) / 2f, SKSamplingOptions.Default);
            }
            else
            {
                var (fitW, fitH) = FitWithin(source.Width, source.Height, targetWidth, targetHeight);
                resized = source.Resize(new SKImageInfo(fitW, fitH, SKColorType.Rgba8888, SKAlphaType.Premul), sampling);
            }

            using (resized)
            {
                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(ToSkiaFormat(format), quality);
                if (data is null)
                    return Result<MemoryStream>.Failure(Error.Internal("imaging.encode_failed", "Failed to encode image to target format."));
                return Result<MemoryStream>.Success(ToStream(data.ToArray()));
            }
        }
        catch (Exception ex)
        {
            return Result<MemoryStream>.Failure(Error.FromException(ex, "imaging.resize_stream_failed"));
        }
    }

    /// <summary>
    /// Reads an image file from disk and returns its contents as a Base64-encoded string.
    /// </summary>
    /// <param name="imagePath">The full path to the image file. Must not be null or empty.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the Base64-encoded image string on success,
    /// or a failure result if the file is missing or cannot be read.
    /// </returns>
    public static Result<string> ImageToBase64(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        try
        {
            if (!File.Exists(imagePath))
                return Result<string>.Failure(Error.NotFound("imaging.file_not_found", $"Image not found: {imagePath}"));
            var bytes = File.ReadAllBytes(imagePath);
            return Result<string>.Success(Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            return Result<string>.Failure(Error.FromException(ex, "imaging.base64_failed"));
        }
    }

    /// <summary>
    /// Resizes an image from disk and saves it to the specified output path.
    /// </summary>
    /// <param name="inputPath">The full path to the source image file. Must not be null or empty.</param>
    /// <param name="outputPath">The full path where the resized image will be saved. Must not be null or empty.</param>
    /// <param name="width">The desired output width in pixels.</param>
    /// <param name="height">The desired output height in pixels.</param>
    /// <param name="maintainAspectRatio">
    /// When <c>true</c>, the image is scaled to fit within the bounds while preserving the aspect ratio.
    /// When <c>false</c>, the image is scaled to cover <paramref name="width"/> × <paramref name="height"/> and cropped to it.
    /// Default: <c>true</c>.
    /// </param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public static Result ResizeImage(string inputPath, string outputPath, int width, int height, bool maintainAspectRatio = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        try
        {
            if (!File.Exists(inputPath))
                return Result.Failure(Error.NotFound("imaging.file_not_found", $"Source file not found: {inputPath}"));

            using var stream = File.OpenRead(inputPath);
            var resizeResult = ResizeImage(stream, width, height, allowCrop: !maintainAspectRatio);
            if (resizeResult.IsFailure) return Result.Failure(resizeResult.Error!);

            using var ms = resizeResult.Value!;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, ms.ToArray());
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(Error.FromException(ex, "imaging.resize_file_failed"));
        }
    }

    /// <summary>
    /// Converts an image file to the specified format and saves it to the output path.
    /// </summary>
    /// <param name="inputPath">The full path to the source image file. Must not be null or empty.</param>
    /// <param name="outputPath">The full path where the converted image will be saved. Must not be null or empty.</param>
    /// <param name="format">The target image format.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public static Result ConvertImageFormat(string inputPath, string outputPath, ImageFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        try
        {
            if (!File.Exists(inputPath))
                return Result.Failure(Error.NotFound("imaging.file_not_found", $"Source file not found: {inputPath}"));

            using var stream = File.OpenRead(inputPath);
            var convertResult = ConvertImage(stream, format);
            if (convertResult.IsFailure) return Result.Failure(convertResult.Error!);

            using var ms = convertResult.Value!;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, ms.ToArray());
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(Error.FromException(ex, "imaging.convert_file_failed"));
        }
    }

    /// <summary>
    /// Creates a square thumbnail from the specified image and saves it to the output path.
    /// The image is cropped to a square centered on the image before scaling.
    /// </summary>
    /// <param name="inputPath">The full path to the source image file. Must not be null or empty.</param>
    /// <param name="outputPath">The full path where the thumbnail will be saved. Must not be null or empty.</param>
    /// <param name="thumbnailSize">The width and height (in pixels) of the square thumbnail. Default: 100.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public static Result CreateThumbnail(string inputPath, string outputPath, int thumbnailSize = 100)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        try
        {
            if (!File.Exists(inputPath))
                return Result.Failure(Error.NotFound("imaging.file_not_found", $"Source file not found: {inputPath}"));

            using var stream = File.OpenRead(inputPath);
            var resizeResult = ResizeImage(stream, thumbnailSize, thumbnailSize, allowCrop: true);
            if (resizeResult.IsFailure) return Result.Failure(resizeResult.Error!);

            using var ms = resizeResult.Value!;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outputPath, ms.ToArray());
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(Error.FromException(ex, "imaging.thumbnail_failed"));
        }
    }

    /// <summary>
    /// Returns the pixel dimensions of an image file, as stored (before EXIF orientation), read from the header
    /// without decoding the pixels.
    /// </summary>
    /// <param name="imagePath">The full path to the image file. Must not be null or empty.</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a tuple of <c>(width, height)</c> in pixels on success,
    /// or a failure result if the file is missing or cannot be decoded.
    /// </returns>
    public static Result<(int width, int height)> GetImageDimensions(string imagePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        try
        {
            if (!File.Exists(imagePath))
                return Result<(int, int)>.Failure(Error.NotFound("imaging.file_not_found", $"Image not found: {imagePath}"));

            using var stream = File.OpenRead(imagePath);
            if (ReadHeader(stream) is not { } header || header.Width <= 0 || header.Height <= 0)
                return Result<(int, int)>.Failure(Error.Validation("imaging.decode_failed", $"Cannot decode image: {imagePath}"));

            return Result<(int, int)>.Success((header.Width, header.Height));
        }
        catch (Exception ex)
        {
            return Result<(int, int)>.Failure(Error.FromException(ex, "imaging.dimensions_failed"));
        }
    }

    /// <summary>An expandable stream positioned at the start (callers may keep writing to it).</summary>
    private static MemoryStream ToStream(byte[] bytes)
    {
        var output = new MemoryStream(bytes.Length);
        output.Write(bytes);
        output.Position = 0;
        return output;
    }

    private static SKEncodedImageFormat ToSkiaFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageFormat.Png  => SKEncodedImageFormat.Png,
        ImageFormat.Webp => SKEncodedImageFormat.Webp,
        _                => SKEncodedImageFormat.Png
    };
}
