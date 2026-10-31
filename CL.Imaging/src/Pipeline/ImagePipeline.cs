using CL.Imaging.Errors;
using CL.Imaging.Internal;
using CodeLogic.Core.Results;
using SkiaSharp;

namespace CL.Imaging;

/// <summary>
/// A lazy, chainable list of edits on an <see cref="ImageDocument"/>. Methods only record the step and check its
/// arguments; nothing runs until <see cref="Apply"/> or <see cref="Encode(ImageEncodeOptions, CancellationToken)"/>.
/// The first invalid argument is reported as that call's <c>imaging.invalid_argument</c> error; no method throws on
/// bad input.
/// </summary>
/// <remarks>
/// Steps run in order on one working bitmap. Consecutive colour adjustments (brightness, contrast, saturation, gamma,
/// hue, exposure, grayscale, sepia, invert, tint) are fused into a single pass; drawing steps (text, shapes, borders,
/// watermarks, composites) draw in place; geometry and filter steps produce one new bitmap each. The source document
/// is never changed. A pipeline can be run more than once.
/// </remarks>
public sealed class ImagePipeline
{
    private readonly ImageDocument _source;
    private readonly List<Operation> _operations = [];
    private Error? _error;

    internal ImagePipeline(ImageDocument source) => _source = source;

    /// <summary>The number of recorded steps.</summary>
    public int Count => _operations.Count;

    // ── Geometry ───────────────────────────────────────────────────────────────

    /// <summary>Turns the pixels upright from the EXIF orientation (a no-op when already upright).</summary>
    public ImagePipeline AutoOrient() => Add(new AutoOrientOperation());

    /// <summary>Resizes into a box.</summary>
    /// <param name="width">Box width; 0 derives it from <paramref name="height"/> and the aspect ratio (not with Fill or Pad).</param>
    /// <param name="height">Box height; 0 derives it from <paramref name="width"/> (not with Fill or Pad).</param>
    /// <param name="mode">How the image is fitted into the box.</param>
    /// <param name="quality">Resampling quality.</param>
    /// <param name="background">Padding colour for <see cref="ResizeMode.Pad"/>; default transparent.</param>
    /// <param name="anchor">Which part is kept for <see cref="ResizeMode.Fill"/>, or where the image sits for <see cref="ResizeMode.Pad"/>.</param>
    public ImagePipeline Resize(int width, int height, ResizeMode mode = ResizeMode.Fit, ResampleQuality quality = ResampleQuality.High,
        Color? background = null, Anchor anchor = Anchor.Center)
    {
        if (width < 0 || height < 0 || (width == 0 && height == 0))
            return Fail("Resize: width and height must be non-negative and not both zero.");
        if (width > SkiaOps.MaxDimension || height > SkiaOps.MaxDimension)
            return Fail($"Resize: width and height must be at most {SkiaOps.MaxDimension}.");
        if ((width == 0 || height == 0) && mode is ResizeMode.Fill or ResizeMode.Pad)
            return Fail($"Resize: {mode} needs both a width and a height.");
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(quality) || !Enum.IsDefined(anchor))
            return Fail("Resize: unknown mode, quality or anchor.");
        return Add(new ResizeOperation(width, height, mode, quality, background ?? Color.Transparent, anchor));
    }

    /// <summary>Crops to a rectangle, which must lie inside the image when the step runs.</summary>
    /// <param name="rect">The area to keep.</param>
    public ImagePipeline Crop(PixelRect rect) =>
        rect.IsEmpty || rect.X < 0 || rect.Y < 0 ? Fail($"Crop: {rect} must have a positive size and a non-negative position.") : Add(new CropOperation(rect));

    /// <summary>
    /// Crops to the most detailed region with the aspect ratio of <paramref name="width"/>×<paramref name="height"/>
    /// (edge and entropy energy), then scales it to exactly that size.
    /// </summary>
    /// <param name="width">Output width.</param>
    /// <param name="height">Output height.</param>
    /// <param name="quality">Resampling quality.</param>
    public ImagePipeline SmartCrop(int width, int height, ResampleQuality quality = ResampleQuality.High) =>
        width is <= 0 or > SkiaOps.MaxDimension || height is <= 0 or > SkiaOps.MaxDimension
            ? Fail($"SmartCrop: width and height must be 1–{SkiaOps.MaxDimension}.")
            : Add(new SmartCropOperation(width, height, quality));

    /// <summary>
    /// Rotates clockwise. Multiples of 90° are exact; other angles enlarge the canvas to hold the rotated image and
    /// fill the corners with <paramref name="background"/>.
    /// </summary>
    /// <param name="degrees">Angle in degrees, clockwise (any finite value).</param>
    /// <param name="background">Corner fill for non-right angles; default transparent.</param>
    public ImagePipeline Rotate(double degrees, Color? background = null) =>
        double.IsFinite(degrees) ? Add(new RotateOperation(degrees, background ?? Color.Transparent)) : Fail("Rotate: degrees must be finite.");

    /// <summary>Mirrors left to right.</summary>
    public ImagePipeline FlipHorizontal() => Add(new FlipOperation(horizontal: true));

    /// <summary>Mirrors top to bottom.</summary>
    public ImagePipeline FlipVertical() => Add(new FlipOperation(horizontal: false));

    // ── Colour ─────────────────────────────────────────────────────────────────

    /// <summary>Adds <paramref name="amount"/> to every channel: −1 (black) … 0 (unchanged) … 1 (white).</summary>
    /// <param name="amount">−1 to 1.</param>
    public ImagePipeline Brightness(double amount) => Range(amount, -1, 1, nameof(Brightness)) ?? Add(MatrixOperation.Brightness(amount));

    /// <summary>Scales the distance from mid grey: −1 (flat grey) … 0 (unchanged) … 1 (double contrast).</summary>
    /// <param name="amount">−1 to 1.</param>
    public ImagePipeline Contrast(double amount) => Range(amount, -1, 1, nameof(Contrast)) ?? Add(MatrixOperation.Contrast(amount));

    /// <summary>Changes saturation: −1 (grey) … 0 (unchanged) … 1 (double).</summary>
    /// <param name="amount">−1 to 1.</param>
    public ImagePipeline Saturation(double amount) => Range(amount, -1, 1, nameof(Saturation)) ?? Add(MatrixOperation.Saturation(amount));

    /// <summary>Applies a gamma curve, <c>out = in^(1/gamma)</c>: above 1 brightens mid-tones, below 1 darkens them.</summary>
    /// <param name="gamma">0.1 to 10.</param>
    public ImagePipeline Gamma(double gamma) => Range(gamma, 0.1, 10, nameof(Gamma)) ?? Add(new GammaOperation(gamma));

    /// <summary>Rotates hues around the colour wheel.</summary>
    /// <param name="degrees">−360 to 360.</param>
    public ImagePipeline Hue(double degrees) => Range(degrees, -360, 360, nameof(Hue)) ?? Add(MatrixOperation.Hue(degrees));

    /// <summary>Multiplies the light by 2^<paramref name="stops"/> (photographic exposure).</summary>
    /// <param name="stops">−10 to 10.</param>
    public ImagePipeline Exposure(double stops) => Range(stops, -10, 10, nameof(Exposure)) ?? Add(MatrixOperation.Exposure(stops));

    /// <summary>Removes colour.</summary>
    /// <param name="amount">0 (unchanged) to 1 (fully grey).</param>
    public ImagePipeline Grayscale(double amount = 1) => Range(amount, 0, 1, nameof(Grayscale)) ?? Add(MatrixOperation.Grayscale(amount));

    /// <summary>Applies a sepia tone.</summary>
    /// <param name="amount">0 (unchanged) to 1 (full sepia).</param>
    public ImagePipeline Sepia(double amount = 1) => Range(amount, 0, 1, nameof(Sepia)) ?? Add(MatrixOperation.Sepia(amount));

    /// <summary>Inverts the colours (alpha is kept).</summary>
    public ImagePipeline Invert() => Add(MatrixOperation.Invert());

    /// <summary>Tints towards <paramref name="color"/>: the image's luminance coloured with it, mixed in by <paramref name="amount"/>.</summary>
    /// <param name="color">Tint colour (alpha ignored).</param>
    /// <param name="amount">0 (unchanged) to 1 (monochrome in the tint colour).</param>
    public ImagePipeline Tint(Color color, double amount = 0.5) => Range(amount, 0, 1, nameof(Tint)) ?? Add(MatrixOperation.Tint(color, amount));

    // ── Filters ────────────────────────────────────────────────────────────────

    /// <summary>Gaussian blur; edges are clamped, so they don't darken.</summary>
    /// <param name="sigma">Standard deviation in pixels, greater than 0 and at most 250.</param>
    public ImagePipeline GaussianBlur(double sigma) =>
        sigma is > 0 and <= 250 ? Add(new BlurOperation(sigma)) : Fail("GaussianBlur: sigma must be greater than 0 and at most 250.");

    /// <summary>Unsharp-mask sharpening: <c>source + amount × (source − blur(source, radius))</c>.</summary>
    /// <param name="amount">Strength, greater than 0 and at most 10 (0.5 is moderate).</param>
    /// <param name="radius">Blur sigma in pixels, greater than 0 and at most 50.</param>
    public ImagePipeline Sharpen(double amount = 0.5, double radius = 1) =>
        amount is > 0 and <= 10 && radius is > 0 and <= 50
            ? Add(new SharpenOperation(amount, radius))
            : Fail("Sharpen: amount must be in (0, 10] and radius in (0, 50].");

    /// <summary>Replaces each <paramref name="blockSize"/>×<paramref name="blockSize"/> block with its average colour.</summary>
    /// <param name="blockSize">Block size in pixels, 2–1024.</param>
    public ImagePipeline Pixelate(int blockSize) =>
        blockSize is >= 2 and <= 1024 ? Add(new PixelateOperation(blockSize)) : Fail("Pixelate: blockSize must be 2–1024.");

    // ── Frames ─────────────────────────────────────────────────────────────────

    /// <summary>Makes the corners transparent with the given radius (anti-aliased). JPEG output flattens them onto <see cref="ImageEncodeOptions.Background"/>.</summary>
    /// <param name="radius">Corner radius in pixels, greater than 0.</param>
    public ImagePipeline RoundCorners(float radius) =>
        radius > 0 && float.IsFinite(radius) ? Add(new RoundCornersOperation(radius)) : Fail("RoundCorners: radius must be greater than 0.");

    /// <summary>Paints a border of <paramref name="thickness"/> pixels inside the edges (the size is unchanged; use <see cref="Pad(int, Color?)"/> to grow).</summary>
    /// <param name="thickness">Border width in pixels, 1–4096.</param>
    /// <param name="color">Border colour.</param>
    public ImagePipeline Border(int thickness, Color color) =>
        thickness is >= 1 and <= 4096 ? Add(new BorderOperation(thickness, color)) : Fail("Border: thickness must be 1–4096.");

    /// <summary>Adds <paramref name="amount"/> pixels on every side.</summary>
    /// <param name="amount">Padding in pixels, 1–8192.</param>
    /// <param name="color">Padding colour; default transparent.</param>
    public ImagePipeline Pad(int amount, Color? color = null) => Pad(amount, amount, amount, amount, color);

    /// <summary>Adds padding per side.</summary>
    /// <param name="left">Left padding, 0–8192.</param>
    /// <param name="top">Top padding, 0–8192.</param>
    /// <param name="right">Right padding, 0–8192.</param>
    /// <param name="bottom">Bottom padding, 0–8192.</param>
    /// <param name="color">Padding colour; default transparent.</param>
    public ImagePipeline Pad(int left, int top, int right, int bottom, Color? color = null) =>
        left is < 0 or > 8192 || top is < 0 or > 8192 || right is < 0 or > 8192 || bottom is < 0 or > 8192 || left + top + right + bottom == 0
            ? Fail("Pad: each side must be 0–8192 and at least one side positive.")
            : Add(new PadOperation(left, top, right, bottom, color ?? Color.Transparent));

    // ── Drawing ────────────────────────────────────────────────────────────────

    /// <summary>Draws a watermark (image or text) at an anchor.</summary>
    /// <param name="watermark">A <see cref="WatermarkImage"/> or <see cref="WatermarkText"/>.</param>
    /// <param name="anchor">Where it goes.</param>
    /// <param name="opacity">0 (invisible) to 1 (opaque).</param>
    /// <param name="margin">Distance from the edges in pixels, 0 or more.</param>
    public ImagePipeline Watermark(Watermark watermark, Anchor anchor = Anchor.BottomRight, double opacity = 0.5, int margin = 16)
    {
        if (watermark is null) return Fail("Watermark: a watermark is required.");
        if (Range(opacity, 0, 1, nameof(Watermark)) is { } bad) return bad;
        if (margin < 0) return Fail("Watermark: margin must be 0 or more.");
        return watermark switch
        {
            WatermarkImage { RelativeWidth: <= 0 or > 1 } => Fail("Watermark: RelativeWidth must be in (0, 1]."),
            WatermarkText { Text: null or "" } => Fail("Watermark: the text is empty."),
            WatermarkText { Font.Size: <= 0 } => Fail("Watermark: the font size must be greater than 0."),
            _ => Add(new WatermarkOperation(watermark, anchor, opacity, margin)),
        };
    }

    /// <summary>Draws one line of text; <paramref name="position"/> is the top of the line box (left, centre or right per <paramref name="align"/>).</summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font.</param>
    /// <param name="position">Top-left (or top-centre / top-right) of the text.</param>
    /// <param name="color">Text colour.</param>
    /// <param name="align">Horizontal alignment relative to <paramref name="position"/>.</param>
    public ImagePipeline DrawText(string text, ImageFont font, ImagePoint position, Color color, TextAlign align = TextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return Fail("DrawText: the text is empty.");
        if (font is null || font.Size <= 0 || !float.IsFinite(font.Size)) return Fail("DrawText: the font size must be greater than 0.");
        return Add(new DrawTextOperation(text, font, position, color, align));
    }

    /// <summary>Draws a rectangle, filled or outlined.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="color">Colour.</param>
    /// <param name="strokeWidth">0 fills the rectangle; above 0 draws an outline that wide.</param>
    /// <param name="cornerRadius">Corner radius, 0 for square corners.</param>
    public ImagePipeline DrawRectangle(PixelRect rect, Color color, float strokeWidth = 0, float cornerRadius = 0)
    {
        if (rect.IsEmpty || strokeWidth < 0 || cornerRadius < 0) return Fail("DrawRectangle: the rectangle must be non-empty and the widths non-negative.");
        var r = rect.ToSkiaF();
        return Add(new ShapeOperation((canvas, paint) =>
        {
            if (cornerRadius > 0) canvas.DrawRoundRect(r, cornerRadius, cornerRadius, paint);
            else canvas.DrawRect(r, paint);
        }, color, strokeWidth));
    }

    /// <summary>Draws an ellipse inside <paramref name="bounds"/>, filled or outlined.</summary>
    /// <param name="bounds">The bounding rectangle.</param>
    /// <param name="color">Colour.</param>
    /// <param name="strokeWidth">0 fills; above 0 draws an outline that wide.</param>
    public ImagePipeline DrawEllipse(PixelRect bounds, Color color, float strokeWidth = 0)
    {
        if (bounds.IsEmpty || strokeWidth < 0) return Fail("DrawEllipse: the bounds must be non-empty and the stroke width non-negative.");
        var r = bounds.ToSkiaF();
        return Add(new ShapeOperation((canvas, paint) => canvas.DrawOval(r, paint), color, strokeWidth));
    }

    /// <summary>Draws a straight line.</summary>
    /// <param name="from">Start point.</param>
    /// <param name="to">End point.</param>
    /// <param name="color">Colour.</param>
    /// <param name="width">Line width in pixels, greater than 0.</param>
    public ImagePipeline DrawLine(ImagePoint from, ImagePoint to, Color color, float width = 1)
    {
        if (width <= 0 || !float.IsFinite(width)) return Fail("DrawLine: width must be greater than 0.");
        return Add(new ShapeOperation((canvas, paint) => canvas.DrawLine(from.ToSkia(), to.ToSkia(), paint), color, width));
    }

    /// <summary>Draws another image on top at <paramref name="position"/> with a blend mode.</summary>
    /// <param name="overlay">The image to draw; it must stay undisposed until the pipeline has run.</param>
    /// <param name="position">Top-left position of the overlay.</param>
    /// <param name="mode">Blend mode.</param>
    /// <param name="opacity">0 to 1.</param>
    public ImagePipeline Composite(ImageDocument overlay, ImagePoint position, BlendMode mode = BlendMode.Normal, double opacity = 1)
    {
        if (overlay is null) return Fail("Composite: an overlay image is required.");
        return Range(opacity, 0, 1, nameof(Composite)) ?? Add(new CompositeOperation(overlay, position, mode, opacity));
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    /// <summary>Drops EXIF and the ICC profile from the result (pixels are converted to sRGB on encode).</summary>
    public ImagePipeline StripMetadata() => Add(new StripMetadataOperation());

    // ── Running ────────────────────────────────────────────────────────────────

    /// <summary>Runs the steps and returns the result as a new document (the caller disposes it).</summary>
    /// <param name="ct">Checked between steps: <c>imaging.cancelled</c>.</param>
    /// <returns>The new document, or the first argument/processing error.</returns>
    public Result<ImageDocument> Apply(CancellationToken ct = default)
    {
        PipelineState? state = null;
        try
        {
            var run = Run(ct, out state);
            if (run is { } error) return error;
            var bitmap = state!.Detach();
            return ImageDocument.FromOwnedBitmap(bitmap, _source, state.Orientation, state.StripMetadata);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
        finally
        {
            state?.Dispose();
        }
    }

    /// <summary>Runs the steps and encodes the result.</summary>
    /// <param name="format">Output format; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="quality">Quality 1–100; null uses <see cref="ImagingDefaults"/>.</param>
    /// <param name="ct">Checked between steps: <c>imaging.cancelled</c>.</param>
    /// <returns>The encoded image, or the first argument/processing/encoding error.</returns>
    public Result<EncodedImage> Encode(ImageFormat? format = null, int? quality = null, CancellationToken ct = default) =>
        Encode(new ImageEncodeOptions { Format = format, Quality = quality }, ct);

    /// <summary>Runs the steps and encodes the result.</summary>
    /// <param name="options">Encoder options.</param>
    /// <param name="ct">Checked between steps: <c>imaging.cancelled</c>.</param>
    /// <returns>The encoded image, or the first argument/processing/encoding error.</returns>
    public Result<EncodedImage> Encode(ImageEncodeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Internal.Encoder.Validate(options, out _, out _) is { } invalid) return invalid;
        PipelineState? state = null;
        try
        {
            var run = Run(ct, out state);
            if (run is { } error) return error;
            ct.ThrowIfCancellationRequested();
            return Internal.Encoder.Encode(state!.Current, options, _source.Exif, _source.Icc,
                resetOrientation: state.OrientationApplied && _source.Orientation != ImageOrientation.Normal, strip: state.StripMetadata);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ImagingErrors.FromException(ex);
        }
        finally
        {
            state?.Dispose();
        }
    }

    private Error? Run(CancellationToken ct, out PipelineState? state)
    {
        state = null;
        if (_error is not null) return _error;
        state = new PipelineState(_source);
        try
        {
            for (var i = 0; i < _operations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_operations[i] is ColorOperation)
                {
                    var group = new List<ColorOperation>();
                    while (i < _operations.Count && _operations[i] is ColorOperation c)
                    {
                        group.Add(c);
                        i++;
                    }
                    i--;
                    ColorOperation.ApplyAll(state, group);
                }
                else
                {
                    _operations[i].Apply(state);
                }
            }
            return null;
        }
        catch (OperationFailedException ex)
        {
            return ex.Error;
        }
    }

    private ImagePipeline Add(Operation operation)
    {
        if (_error is null) _operations.Add(operation);
        return this;
    }

    private ImagePipeline Fail(string message)
    {
        _error ??= ImagingErrors.InvalidArgument(message);
        return this;
    }

    private ImagePipeline? Range(double value, double min, double max, string name) =>
        double.IsFinite(value) && value >= min && value <= max ? null : Fail($"{name}: the value must be between {min} and {max}.");
}
