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
/// </summary>
public static class CLU_Imaging
{
    /// <summary>
    /// Checks whether the image contained in the given stream does not exceed the specified dimensions.
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
            using var bitmap = SKBitmap.Decode(imageStream);
            return bitmap is not null && bitmap.Width <= maxWidth && bitmap.Height <= maxHeight;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the image in the stream has a valid (non-degenerate) aspect ratio.
    /// An aspect ratio is considered invalid if either dimension is zero.
    /// </summary>
    /// <param name="imageStream">A readable stream containing image data. Must not be null.</param>
    /// <returns><c>true</c> if the image has non-zero width and height; otherwise <c>false</c>.</returns>
    public static bool IsValidAspectRatio(Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        try
        {
            using var bitmap = SKBitmap.Decode(imageStream);
            return bitmap is not null && bitmap.Width > 0 && bitmap.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Converts the image in the given stream to the specified <see cref="ImageFormat"/>.
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
        try
        {
            using var bitmap = SKBitmap.Decode(imageStream);
            if (bitmap is null)
                return Result<MemoryStream>.Failure(Error.Validation("imaging.decode_failed", "Failed to decode source image."));

            var skFormat = ToSkiaFormat(format);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(skFormat, 90);
            if (data is null)
                return Result<MemoryStream>.Failure(Error.Internal("imaging.encode_failed", "Failed to encode image to target format."));

            var output = new MemoryStream();
            data.SaveTo(output);
            output.Position = 0;
            return Result<MemoryStream>.Success(output);
        }
        catch (Exception ex)
        {
            return Result<MemoryStream>.Failure(Error.FromException(ex, "imaging.convert_failed"));
        }
    }

    /// <summary>
    /// Resizes the image in the given stream to the specified dimensions.
    /// </summary>
    /// <param name="imageStream">A readable stream containing the source image. Must not be null.</param>
    /// <param name="targetWidth">The desired output width in pixels.</param>
    /// <param name="targetHeight">The desired output height in pixels.</param>
    /// <param name="allowCrop">
    /// When <c>true</c>, the image is cropped to fill the exact target dimensions.
    /// When <c>false</c>, the image is scaled to fit within the bounds, preserving aspect ratio.
    /// Default: <c>true</c>.
    /// </param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a <see cref="MemoryStream"/> with PNG-encoded resized image data on success,
    /// or a failure result with a descriptive error.
    /// </returns>
    public static Result<MemoryStream> ResizeImage(Stream imageStream, int targetWidth, int targetHeight, bool allowCrop = true)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        try
        {
            using var sourceBitmap = SKBitmap.Decode(imageStream);
            if (sourceBitmap is null)
                return Result<MemoryStream>.Failure(Error.Validation("imaging.decode_failed", "Failed to decode source image."));

            SKBitmap resized;
            if (allowCrop)
            {
                // Scale to fill, then crop center
                float scaleX = (float)targetWidth  / sourceBitmap.Width;
                float scaleY = (float)targetHeight / sourceBitmap.Height;
                float scale  = Math.Max(scaleX, scaleY);

                int scaledW = (int)(sourceBitmap.Width  * scale);
                int scaledH = (int)(sourceBitmap.Height * scale);

                resized = sourceBitmap.Resize(new SKImageInfo(scaledW, scaledH), SKSamplingOptions.Default);
                int cropX = (scaledW - targetWidth)  / 2;
                int cropY = (scaledH - targetHeight) / 2;

                using var cropped = new SKBitmap(targetWidth, targetHeight);
                using var canvas  = new SKCanvas(cropped);
                canvas.DrawBitmap(resized, -cropX, -cropY, SKSamplingOptions.Default);
                resized.Dispose();
                resized = cropped.Copy();
            }
            else
            {
                resized = sourceBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default);
            }

            using (resized)
            {
                using var image = SKImage.FromBitmap(resized);
                using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
                var output = new MemoryStream();
                data.SaveTo(output);
                output.Position = 0;
                return Result<MemoryStream>.Success(output);
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
    /// When <c>false</c>, the image is stretched to exactly <paramref name="width"/> × <paramref name="height"/>.
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
    /// Returns the pixel dimensions of an image file.
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
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null)
                return Result<(int, int)>.Failure(Error.Validation("imaging.decode_failed", $"Cannot decode image: {imagePath}"));

            return Result<(int, int)>.Success((bitmap.Width, bitmap.Height));
        }
        catch (Exception ex)
        {
            return Result<(int, int)>.Failure(Error.FromException(ex, "imaging.dimensions_failed"));
        }
    }

    private static SKEncodedImageFormat ToSkiaFormat(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageFormat.Png  => SKEncodedImageFormat.Png,
        ImageFormat.Webp => SKEncodedImageFormat.Webp,
        _                => SKEncodedImageFormat.Png
    };
}
