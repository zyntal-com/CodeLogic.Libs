namespace CL.Imaging;

/// <summary>The defaults every CL.Imaging call falls back to when an option is not given.</summary>
/// <param name="MaxPixels">Largest source image, in pixels, that is decoded (checked from the header). 0 = no limit.</param>
/// <param name="MaxBytes">Largest encoded input, in bytes, that is read. 0 = no limit.</param>
/// <param name="Quality">Encoder quality for lossy formats, 1–100.</param>
/// <param name="Format">Output format when none is given.</param>
/// <param name="StripMetadata">Whether encoding drops EXIF unless the caller asks to keep it.</param>
public sealed record ImagingSettings(long MaxPixels, long MaxBytes, int Quality, ImageFormat Format, bool StripMetadata)
{
    /// <summary>Built-in defaults: 40 megapixels, 100 MB, quality 85, WebP, strip metadata.</summary>
    public static ImagingSettings BuiltIn { get; } = new(40_000_000, 100L * 1024 * 1024, 85, ImageFormat.Webp, true);
}

/// <summary>
/// Process-wide defaults. They start as <see cref="ImagingSettings.BuiltIn"/>; <see cref="ImagingLibrary"/> replaces them
/// from its <c>imaging</c> config section when loaded. Everything in CL.Imaging works without the library.
/// </summary>
public static class ImagingDefaults
{
    private static volatile ImagingSettings _current = ImagingSettings.BuiltIn;

    /// <summary>The settings in effect. Replace the whole record to change them (thread-safe).</summary>
    public static ImagingSettings Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Restores <see cref="ImagingSettings.BuiltIn"/>.</summary>
    public static void Reset() => _current = ImagingSettings.BuiltIn;
}
