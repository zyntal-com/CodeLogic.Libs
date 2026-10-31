using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

namespace CL.Imaging;

/// <summary>
/// <b>CL.Imaging</b> as a CodeLogic library. Loading it is optional: it only applies the <c>imaging</c> config section
/// (<see cref="ImagingConfig"/>) to <see cref="ImagingDefaults"/>, live on config changes. All imaging APIs work
/// without it.
/// </summary>
public sealed class ImagingLibrary : ILibrary, IConfigReloadable
{
    private LibraryContext? _context;

    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Imaging",
        Name = "Imaging Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Image decoding, editing pipeline, metadata, analysis and web helpers (SkiaSharp)",
        Author = "Zyntal.com",
        Tags = ["imaging", "image", "resize", "thumbnail", "webp", "exif", "blurhash"],
    };

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Configuration.Register<ImagingConfig>("imaging");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        var config = context.Configuration.Get<ImagingConfig>();
        var validation = config.Validate();
        if (!validation.IsValid)
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {string.Join("; ", validation.Errors)}");
        ImagingDefaults.Current = config.ToSettings();
        context.Logger.Info($"{Manifest.Name} defaults: max {config.DefaultMaxPixels:N0} px, {config.DefaultFormat} q{config.DefaultQuality}");
        return Task.CompletedTask;
    }

    /// <summary>Applies a changed <c>imaging</c> section to <see cref="ImagingDefaults"/> (invalid values are logged and ignored).</summary>
    public Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (change.NewValue is not ImagingConfig config) return Task.CompletedTask;
        var validation = config.Validate();
        if (!validation.IsValid)
        {
            context.Logger.Warning($"{Manifest.Name}: ignored invalid configuration: {string.Join("; ", validation.Errors)}");
            return Task.CompletedTask;
        }
        ImagingDefaults.Current = config.ToSettings();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        ImagingDefaults.Reset();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync()
    {
        var settings = ImagingDefaults.Current;
        return Task.FromResult(HealthStatus.Healthy($"Defaults: {settings.Format} q{settings.Quality}, max {settings.MaxPixels:N0} px"));
    }

    /// <inheritdoc/>
    public void Dispose() => _context = null;
}
