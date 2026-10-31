using CodeLogic.Core.Results;

namespace CL.Imaging.Errors;

/// <summary>Creates imaging errors with stable <c>imaging.*</c> codes.</summary>
public static class ImagingErrors
{
    /// <summary>Stable code for an image over the pixel or byte limit (checked from the header, before decoding).</summary>
    public const string TooLargeCode = "imaging.too_large";
    /// <summary>Stable code for a recognised format that cannot be decoded or encoded on this platform.</summary>
    public const string UnsupportedFormatCode = "imaging.unsupported_format";
    /// <summary>Stable code for data that is not a readable image (unknown, truncated or corrupt).</summary>
    public const string DecodeFailedCode = "imaging.decode_failed";
    /// <summary>Stable code for an encoder that produced no output.</summary>
    public const string EncodeFailedCode = "imaging.encode_failed";
    /// <summary>Stable code for an argument outside its documented range (sizes, amounts, quality, rectangles).</summary>
    public const string InvalidArgumentCode = "imaging.invalid_argument";
    /// <summary>Stable code for an operation cancelled through its <see cref="CancellationToken"/>.</summary>
    public const string CancelledCode = "imaging.cancelled";
    /// <summary>Stable code for a missing input file.</summary>
    public const string FileNotFoundCode = "imaging.file_not_found";
    /// <summary>Stable code for a read or write failure on a stream or file.</summary>
    public const string IoFailedCode = "imaging.io_failed";
    /// <summary>Stable code for an unexpected failure inside the imaging engine.</summary>
    public const string ProcessingFailedCode = "imaging.processing_failed";

    /// <summary>Creates a too-large error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional diagnostics.</param>
    /// <returns>A validation error with <see cref="TooLargeCode"/>.</returns>
    public static Error TooLarge(string message, string? details = null) => Error.Validation(TooLargeCode, message, details);

    /// <summary>Creates an unsupported-format error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional diagnostics.</param>
    /// <returns>A validation error with <see cref="UnsupportedFormatCode"/>.</returns>
    public static Error UnsupportedFormat(string message, string? details = null) => Error.Validation(UnsupportedFormatCode, message, details);

    /// <summary>Creates a decode-failed error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional diagnostics.</param>
    /// <returns>A validation error with <see cref="DecodeFailedCode"/>.</returns>
    public static Error DecodeFailed(string message = "The data is not a readable image.", string? details = null) =>
        Error.Validation(DecodeFailedCode, message, details);

    /// <summary>Creates an encode-failed error.</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <param name="details">Optional diagnostics.</param>
    /// <returns>An internal error with <see cref="EncodeFailedCode"/>.</returns>
    public static Error EncodeFailed(string message, string? details = null) => Error.Internal(EncodeFailedCode, message, details);

    /// <summary>Creates an invalid-argument error.</summary>
    /// <param name="message">Safe caller-facing explanation, naming the argument and its allowed range.</param>
    /// <param name="details">Optional diagnostics.</param>
    /// <returns>A validation error with <see cref="InvalidArgumentCode"/>.</returns>
    public static Error InvalidArgument(string message, string? details = null) => Error.Validation(InvalidArgumentCode, message, details);

    /// <summary>Creates a cancelled error (an unavailable error, following the CodeLogic convention).</summary>
    /// <param name="message">Safe caller-facing explanation.</param>
    /// <returns>An unavailable error with <see cref="CancelledCode"/>.</returns>
    public static Error Cancelled(string message = "The imaging operation was cancelled.") => Error.Unavailable(CancelledCode, message);

    /// <summary>Creates a file-not-found error.</summary>
    /// <param name="path">The missing path.</param>
    /// <returns>A not-found error with <see cref="FileNotFoundCode"/>.</returns>
    public static Error FileNotFound(string path) => Error.NotFound(FileNotFoundCode, $"Image file not found: {path}");

    /// <summary>Creates an I/O error from an exception.</summary>
    /// <param name="exception">The I/O exception.</param>
    /// <returns>An internal error with <see cref="IoFailedCode"/>.</returns>
    public static Error IoFailed(Exception exception) => Error.FromException(exception, IoFailedCode);

    /// <summary>Creates an unexpected-failure error from an exception.</summary>
    /// <param name="exception">The exception thrown by the engine.</param>
    /// <returns>An internal error with <see cref="ProcessingFailedCode"/>.</returns>
    public static Error ProcessingFailed(Exception exception) => Error.FromException(exception, ProcessingFailedCode);

    /// <summary>Maps an exception caught around an imaging call to the matching error.</summary>
    internal static Error FromException(Exception exception) => exception switch
    {
        OperationCanceledException => Cancelled(),
        Internal.AllocationFailedException => TooLarge(exception.Message),
        IOException or UnauthorizedAccessException => IoFailed(exception),
        _ => ProcessingFailed(exception),
    };
}
