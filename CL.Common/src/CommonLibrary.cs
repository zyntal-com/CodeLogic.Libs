using CodeLogic.Core.Logging;
using CodeLogic.Framework.Libraries;

namespace CL.Common;

/// <summary>
/// CL.Common library — general-purpose utility toolkit for CodeLogic applications.
/// Provides Security, Generators, Caching, Resilience, Scheduling, Hashing, Threading, Compression, Networking,
/// Data, FileHandling, Conversion, Parser, Time, and String utilities.
/// No external service dependencies — always healthy when initialized.
/// </summary>
public sealed class CommonLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id          = "cl.common",
        Name        = "CL.Common",
        Version     = CL.Internal.InternalLibraryVersion.Current,
        Description = "General-purpose utility toolkit — security, generators, caching, resilience, scheduling, hashing, and more",
        Author      = "Zyntal.com",
        Dependencies = []
    };

    private LibraryContext? _context;

    /// <inheritdoc/>
    public async Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} v{Manifest.Version} configured");
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} initialized");
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} started and ready");
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"{Manifest.Name} stopped");
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync() =>
        Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is operational"));

    /// <inheritdoc/>
    public void Dispose() { }
}
