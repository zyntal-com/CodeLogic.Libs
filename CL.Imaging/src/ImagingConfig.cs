using CodeLogic.Core.Configuration;

namespace CL.Imaging;

/// <summary>
/// Configuration for <see cref="ImagingLibrary"/> (<c>config.imaging.json</c>). It only sets the process-wide
/// <see cref="ImagingDefaults"/>; every option can still be overridden per call.
/// </summary>
[ConfigSection("imaging")]
public sealed class ImagingConfig : ConfigModelBase
{
    /// <summary>Largest source image, in pixels, that is decoded (checked from the header). 0 = no limit. Default 40 000 000.</summary>
    [ConfigField(Label = "Max pixels", Description = "Largest image (width × height) that is decoded; 0 = no limit.", Group = "Limits", Order = 0)]
    public long DefaultMaxPixels { get; set; } = ImagingSettings.BuiltIn.MaxPixels;

    /// <summary>Largest encoded input, in bytes. 0 = no limit. Default 100 MB.</summary>
    [ConfigField(Label = "Max bytes", Description = "Largest encoded image that is read, in bytes; 0 = no limit.", Group = "Limits", Order = 1)]
    public long DefaultMaxBytes { get; set; } = ImagingSettings.BuiltIn.MaxBytes;

    /// <summary>Encoder quality for lossy formats, 1–100. Default 85.</summary>
    [ConfigField(Label = "Quality", Description = "Encoder quality for JPEG and lossy WebP (1–100).", Group = "Output", Order = 10)]
    public int DefaultQuality { get; set; } = ImagingSettings.BuiltIn.Quality;

    /// <summary>Output format when a call does not choose one: Jpeg, Png or Webp. Default Webp.</summary>
    [ConfigField(Label = "Format", Description = "Output format when a call doesn't choose one (Jpeg, Png or Webp).", Group = "Output", Order = 11)]
    public ImageFormat DefaultFormat { get; set; } = ImagingSettings.BuiltIn.Format;

    /// <summary>Whether encoding drops EXIF unless a call asks to keep it. Default true.</summary>
    [ConfigField(Label = "Strip metadata", Description = "Drop EXIF (camera, GPS) on encode unless a call asks to keep it.", Group = "Output", Order = 12)]
    public bool StripMetadataByDefault { get; set; } = ImagingSettings.BuiltIn.StripMetadata;

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        List<string> errors = [];
        if (DefaultMaxPixels < 0) errors.Add("DefaultMaxPixels must be 0 or more.");
        if (DefaultMaxBytes < 0) errors.Add("DefaultMaxBytes must be 0 or more.");
        if (DefaultQuality is < 1 or > 100) errors.Add("DefaultQuality must be between 1 and 100.");
        if (!DefaultFormat.CanEncode()) errors.Add("DefaultFormat must be Jpeg, Png or Webp.");
        return errors.Count > 0 ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
    }

    /// <summary>The settings this configuration describes.</summary>
    public ImagingSettings ToSettings() => new(DefaultMaxPixels, DefaultMaxBytes, DefaultQuality, DefaultFormat, StripMetadataByDefault);
}
