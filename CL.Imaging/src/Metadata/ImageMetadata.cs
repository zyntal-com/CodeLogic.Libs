namespace CL.Imaging.Metadata;

/// <summary>A GPS position from EXIF.</summary>
/// <param name="Latitude">Degrees, positive north.</param>
/// <param name="Longitude">Degrees, positive east.</param>
/// <param name="Altitude">Metres above sea level (negative below), when recorded.</param>
public sealed record GpsCoordinates(double Latitude, double Longitude, double? Altitude);

/// <summary>
/// Metadata read from an image's EXIF block (JPEG APP1, PNG <c>eXIf</c>, WebP <c>EXIF</c>) and container. Every
/// field is optional: images without EXIF give an instance with only <see cref="Width"/>, <see cref="Height"/> and
/// <see cref="Format"/> set.
/// </summary>
public sealed record ImageMetadata
{
    /// <summary>The container format.</summary>
    public ImageFormat Format { get; init; }
    /// <summary>Stored width in pixels (before orientation), from the image header.</summary>
    public int Width { get; init; }
    /// <summary>Stored height in pixels (before orientation), from the image header.</summary>
    public int Height { get; init; }
    /// <summary>The EXIF orientation; <see cref="ImageOrientation.Normal"/> when absent.</summary>
    public ImageOrientation Orientation { get; init; } = ImageOrientation.Normal;
    /// <summary>Whether the image has an EXIF block.</summary>
    public bool HasExif { get; init; }
    /// <summary>Whether the image embeds an ICC colour profile.</summary>
    public bool HasIccProfile { get; init; }
    /// <summary>Camera manufacturer (EXIF Make).</summary>
    public string? CameraMake { get; init; }
    /// <summary>Camera model (EXIF Model).</summary>
    public string? CameraModel { get; init; }
    /// <summary>Lens manufacturer (EXIF LensMake).</summary>
    public string? LensMake { get; init; }
    /// <summary>Lens model (EXIF LensModel).</summary>
    public string? LensModel { get; init; }
    /// <summary>Software that wrote the image (EXIF Software).</summary>
    public string? Software { get; init; }
    /// <summary>Author (EXIF Artist).</summary>
    public string? Artist { get; init; }
    /// <summary>Copyright notice (EXIF Copyright).</summary>
    public string? Copyright { get; init; }
    /// <summary>When the photo was taken (DateTimeOriginal, else DateTime), in the camera's local time.</summary>
    public DateTime? DateTaken { get; init; }
    /// <summary>The UTC offset of <see cref="DateTaken"/> (OffsetTimeOriginal), when recorded.</summary>
    public TimeSpan? DateTakenOffset { get; init; }
    /// <summary>The GPS position, when recorded.</summary>
    public GpsCoordinates? Gps { get; init; }
    /// <summary>ISO speed (PhotographicSensitivity).</summary>
    public int? Iso { get; init; }
    /// <summary>Exposure time in seconds.</summary>
    public double? ExposureTime { get; init; }
    /// <summary>F-number (aperture).</summary>
    public double? FNumber { get; init; }
    /// <summary>Focal length in millimetres.</summary>
    public double? FocalLength { get; init; }
    /// <summary>35 mm equivalent focal length in millimetres.</summary>
    public int? FocalLength35mm { get; init; }
    /// <summary>Whether the flash fired, when recorded.</summary>
    public bool? FlashFired { get; init; }
    /// <summary>Pixel width recorded in EXIF (PixelXDimension); may differ from <see cref="Width"/> after editing.</summary>
    public int? ExifWidth { get; init; }
    /// <summary>Pixel height recorded in EXIF (PixelYDimension).</summary>
    public int? ExifHeight { get; init; }

    /// <summary>Exposure time as photographers write it, e.g. <c>1/250</c> or <c>2s</c>.</summary>
    public string? ExposureTimeText => ExposureTime switch
    {
        null => null,
        <= 0 => null,
        < 1 and var t => $"1/{Math.Round(1 / t):0}",
        var t => $"{t:0.#}s",
    };
}
