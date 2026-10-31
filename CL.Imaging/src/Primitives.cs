using System.Globalization;
using SkiaSharp;

namespace CL.Imaging;

/// <summary>Image file formats CL.Imaging recognises.</summary>
/// <remarks>
/// Decoding covers every member except <see cref="Unknown"/>; <see cref="Heif"/> and <see cref="Avif"/> decode
/// only where the platform's Skia build includes those codecs (otherwise <c>imaging.unsupported_format</c>).
/// Encoding supports <see cref="Jpeg"/>, <see cref="Png"/> and <see cref="Webp"/>.
/// </remarks>
public enum ImageFormat
{
    /// <summary>Not recognised.</summary>
    Unknown,
    /// <summary>JPEG: lossy, for photographs. Encodable.</summary>
    Jpeg,
    /// <summary>PNG: lossless, with transparency. Encodable.</summary>
    Png,
    /// <summary>WebP: lossy or lossless, with transparency. Encodable.</summary>
    Webp,
    /// <summary>GIF: the first frame is decoded.</summary>
    Gif,
    /// <summary>Windows bitmap.</summary>
    Bmp,
    /// <summary>Windows icon: the best entry is decoded.</summary>
    Ico,
    /// <summary>HEIF/HEIC: decoded where the platform supports it.</summary>
    Heif,
    /// <summary>AVIF: decoded where the platform supports it.</summary>
    Avif,
}

/// <summary>The EXIF orientation (tag 0x0112): how the stored pixels must be turned to be shown upright.</summary>
public enum ImageOrientation
{
    /// <summary>1: stored upright.</summary>
    Normal = 1,
    /// <summary>2: mirrored left to right.</summary>
    FlipHorizontal = 2,
    /// <summary>3: upside down.</summary>
    Rotate180 = 3,
    /// <summary>4: mirrored top to bottom.</summary>
    FlipVertical = 4,
    /// <summary>5: mirrored along the main diagonal.</summary>
    Transpose = 5,
    /// <summary>6: shown rotated 90° clockwise.</summary>
    Rotate90 = 6,
    /// <summary>7: mirrored along the anti-diagonal.</summary>
    Transverse = 7,
    /// <summary>8: shown rotated 270° clockwise (90° counter-clockwise).</summary>
    Rotate270 = 8,
}

/// <summary>How <see cref="ImagePipeline.Resize"/> fits the image into the requested box.</summary>
public enum ResizeMode
{
    /// <summary>Scale (up or down) to fit inside the box, keeping the aspect ratio.</summary>
    Fit,
    /// <summary>Scale to cover the box, keeping the aspect ratio, and crop the overflow at the anchor ("cover").</summary>
    Fill,
    /// <summary>Scale to exactly the box, ignoring the aspect ratio.</summary>
    Stretch,
    /// <summary>Fit inside the box, then pad to exactly the box with the background colour ("contain").</summary>
    Pad,
    /// <summary>Like <see cref="Fit"/>, but never scale up.</summary>
    Max,
}

/// <summary>Resampling quality for scaling.</summary>
public enum ResampleQuality
{
    /// <summary>Nearest neighbour: blocky, fastest; for pixel art.</summary>
    Nearest,
    /// <summary>Bilinear with mipmaps.</summary>
    Medium,
    /// <summary>Bicubic (Mitchell); large reductions are pre-reduced by halving first to avoid aliasing.</summary>
    High,
}

/// <summary>A position in a box, for crops, padding, watermarks and alignment.</summary>
public enum Anchor
{
    /// <summary>Top left.</summary>
    TopLeft,
    /// <summary>Top centre.</summary>
    Top,
    /// <summary>Top right.</summary>
    TopRight,
    /// <summary>Middle left.</summary>
    Left,
    /// <summary>Centre.</summary>
    Center,
    /// <summary>Middle right.</summary>
    Right,
    /// <summary>Bottom left.</summary>
    BottomLeft,
    /// <summary>Bottom centre.</summary>
    Bottom,
    /// <summary>Bottom right.</summary>
    BottomRight,
}

/// <summary>Blend modes for <see cref="ImagePipeline.Composite"/>.</summary>
public enum BlendMode
{
    /// <summary>Source over destination.</summary>
    Normal,
    /// <summary>Multiply.</summary>
    Multiply,
    /// <summary>Screen.</summary>
    Screen,
    /// <summary>Overlay.</summary>
    Overlay,
    /// <summary>Darken.</summary>
    Darken,
    /// <summary>Lighten.</summary>
    Lighten,
    /// <summary>Colour dodge.</summary>
    ColorDodge,
    /// <summary>Colour burn.</summary>
    ColorBurn,
    /// <summary>Hard light.</summary>
    HardLight,
    /// <summary>Soft light.</summary>
    SoftLight,
    /// <summary>Difference.</summary>
    Difference,
    /// <summary>Exclusion.</summary>
    Exclusion,
    /// <summary>Hue of the source, saturation and luminosity of the destination.</summary>
    Hue,
    /// <summary>Saturation of the source.</summary>
    Saturation,
    /// <summary>Hue and saturation of the source.</summary>
    Color,
    /// <summary>Luminosity of the source.</summary>
    Luminosity,
    /// <summary>Additive.</summary>
    Plus,
}

/// <summary>Horizontal text alignment relative to the text position.</summary>
public enum TextAlign
{
    /// <summary>The position is the left edge.</summary>
    Left,
    /// <summary>The position is the centre.</summary>
    Center,
    /// <summary>The position is the right edge.</summary>
    Right,
}

/// <summary>An 8-bit sRGB colour with straight (unpremultiplied) alpha.</summary>
/// <param name="R">Red, 0–255.</param>
/// <param name="G">Green, 0–255.</param>
/// <param name="B">Blue, 0–255.</param>
/// <param name="A">Alpha, 0 (transparent) – 255 (opaque).</param>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Fully transparent black.</summary>
    public static Color Transparent => new(0, 0, 0, 0);
    /// <summary>Opaque black.</summary>
    public static Color Black => new(0, 0, 0);
    /// <summary>Opaque white.</summary>
    public static Color White => new(255, 255, 255);
    /// <summary>Opaque red.</summary>
    public static Color Red => new(255, 0, 0);
    /// <summary>Opaque green (#00FF00).</summary>
    public static Color Green => new(0, 255, 0);
    /// <summary>Opaque blue.</summary>
    public static Color Blue => new(0, 0, 255);
    /// <summary>Opaque middle grey (#808080).</summary>
    public static Color Gray => new(128, 128, 128);

    /// <summary>Returns this colour with a different alpha.</summary>
    /// <param name="alpha">The new alpha, 0–255.</param>
    public Color WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>Hex form: <c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when not opaque.</summary>
    public string ToHex() => A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the <c>#</c> is optional).</summary>
    /// <param name="hex">The hex text.</param>
    /// <param name="color">The parsed colour.</param>
    /// <returns><c>true</c> when the text is a valid colour.</returns>
    public static bool TryParseHex(ReadOnlySpan<char> hex, out Color color)
    {
        color = default;
        if (hex.StartsWith("#")) hex = hex[1..];
        if (hex.Length == 3)
        {
            if (!TryNibble(hex[0], out var r) || !TryNibble(hex[1], out var g) || !TryNibble(hex[2], out var b)) return false;
            color = new Color((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            return true;
        }
        if (hex.Length is not (6 or 8)) return false;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v)) return false;
        color = hex.Length == 6
            ? new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Color((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;

        static bool TryNibble(char c, out int value) =>
            int.TryParse([c], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Relative luminance (Rec. 709 weights on the 0–255 values), 0–255.</summary>
    public double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    /// <summary>Converts to a SkiaSharp colour.</summary>
    /// <param name="color">The colour.</param>
    public static implicit operator SKColor(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>Converts from a SkiaSharp colour.</summary>
    /// <param name="color">The colour.</param>
    public static implicit operator Color(SKColor color) => new(color.Red, color.Green, color.Blue, color.Alpha);

    /// <inheritdoc/>
    public override string ToString() => ToHex();
}

/// <summary>A rectangle in whole pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width, greater than zero for a non-empty rectangle.</param>
/// <param name="Height">Height, greater than zero for a non-empty rectangle.</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>The right edge (exclusive).</summary>
    public int Right => X + Width;
    /// <summary>The bottom edge (exclusive).</summary>
    public int Bottom => Y + Height;
    /// <summary>Whether the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whether the rectangle lies entirely inside a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    public bool FitsIn(int width, int height) => !IsEmpty && X >= 0 && Y >= 0 && Right <= width && Bottom <= height;

    internal SKRectI ToSkia() => new(X, Y, Right, Bottom);
    internal SKRect ToSkiaF() => new(X, Y, Right, Bottom);
}

/// <summary>A point in image coordinates (pixels, fractional allowed).</summary>
/// <param name="X">Horizontal position from the left edge.</param>
/// <param name="Y">Vertical position from the top edge.</param>
public readonly record struct ImagePoint(float X, float Y)
{
    internal SKPoint ToSkia() => new(X, Y);
}
