using SkiaSharp;

namespace CL.Imaging;

/// <summary>
/// A font for <see cref="ImagePipeline.DrawText"/> and <see cref="WatermarkText"/>: a system family, or font file bytes.
/// </summary>
/// <remarks>
/// On servers without fonts installed (e.g. minimal Linux containers using the NoDependencies native assets) a
/// family name may resolve to an empty typeface that draws nothing; ship a font file and use <see cref="FromFile"/>
/// or <see cref="FromBytes"/> to be independent of the machine.
/// </remarks>
public sealed record ImageFont
{
    /// <summary>Font family name; null uses the platform default.</summary>
    public string? Family { get; init; }

    /// <summary>Size in pixels (the em size). Default: 24.</summary>
    public float Size { get; init; } = 24;

    /// <summary>Bold weight.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic slant.</summary>
    public bool Italic { get; init; }

    /// <summary>TrueType/OpenType font file bytes; when set, <see cref="Family"/> is ignored.</summary>
    public ReadOnlyMemory<byte> FontData { get; init; }

    /// <summary>The platform default font at <paramref name="size"/> pixels.</summary>
    /// <param name="size">Size in pixels.</param>
    public static ImageFont Default(float size = 24) => new() { Size = size };

    /// <summary>A font loaded from a TrueType/OpenType file.</summary>
    /// <param name="path">The font file.</param>
    /// <param name="size">Size in pixels.</param>
    public static ImageFont FromFile(string path, float size = 24) => new() { FontData = File.ReadAllBytes(path), Size = size };

    /// <summary>A font from TrueType/OpenType file bytes.</summary>
    /// <param name="data">The font file bytes.</param>
    /// <param name="size">Size in pixels.</param>
    public static ImageFont FromBytes(ReadOnlyMemory<byte> data, float size = 24) => new() { FontData = data, Size = size };

    internal SKTypeface CreateTypeface()
    {
        if (!FontData.IsEmpty)
        {
            using var data = SKData.CreateCopy(FontData.Span);
            var fromData = SKTypeface.FromData(data);
            if (fromData is not null) return fromData;
        }
        var style = new SKFontStyle(Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        return SKTypeface.FromFamilyName(Family, style) ?? SKTypeface.CreateDefault();
    }
}

/// <summary>What <see cref="ImagePipeline.Watermark"/> draws: an image (<see cref="WatermarkImage"/>) or text (<see cref="WatermarkText"/>).</summary>
/// <remarks>A C# 15 closed hierarchy: a <c>switch</c> over the two kinds is exhaustive.</remarks>
public closed record Watermark;

/// <summary>An image watermark (a logo).</summary>
/// <param name="Image">The watermark image; it must stay undisposed until the pipeline has run.</param>
/// <param name="RelativeWidth">
/// Width as a fraction of the target image's width (0–1], keeping the aspect ratio; null draws it at its own size.
/// </param>
public sealed record WatermarkImage(ImageDocument Image, double? RelativeWidth = 0.25) : Watermark;

/// <summary>A text watermark.</summary>
/// <param name="Text">The text (one line).</param>
/// <param name="Font">The font; null uses the default font at 1/20 of the target image's width.</param>
/// <param name="Color">Text colour; null is white.</param>
public sealed record WatermarkText(string Text, ImageFont? Font = null, Color? Color = null) : Watermark;
