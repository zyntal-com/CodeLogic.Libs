using CodeLogic.Framework.Libraries;

namespace CL.GameNetQuery;

/// <summary>
/// CL.GameNetQuery library — game server query toolkit for CodeLogic applications.
/// Supports Valve Source Engine (CSS, CS2) and Minecraft server queries via UDP/RCON.
/// </summary>
public sealed class GameNetQueryLibrary : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id          = "cl.gamenetquery",
        Name        = "CL.GameNetQuery",
        Version     = CL.Internal.InternalLibraryVersion.Current,
        Description = "Game server query toolkit — Valve Source Engine and Minecraft UDP/RCON queries",
        Author      = "Zyntal.com",
        Dependencies = []
    };

    private LibraryContext? _context;

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} initialized");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"{Manifest.Name} started and ready");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<HealthStatus> HealthCheckAsync() =>
        Task.FromResult(HealthStatus.Healthy($"{Manifest.Name} is operational"));

    /// <inheritdoc/>
    public void Dispose() { }
}
