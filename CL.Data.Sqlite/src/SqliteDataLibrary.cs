using CL.Data.Providers;
using CodeLogic.Framework.Libraries;

namespace CL.Data.Sqlite;

/// <summary>
/// CodeLogic library that registers the SQLite provider with CL.Data. Load it next to the CL.Data library;
/// connections with <c>"provider": "sqlite"</c> then work.
/// </summary>
public sealed class SqliteDataLibrary : ILibrary
{
    /// <inheritdoc />
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Data.Sqlite",
        Name = "CL.Data SQLite provider",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "SQLite provider for CL.Data (Microsoft.Data.Sqlite).",
        Author = "Zyntal.com",
        Tags = ["database", "sqlite", "cl.data"],
        Dependencies = [LibraryDependency.Required("CL.Data")],
    };

    /// <inheritdoc />
    public Task OnConfigureAsync(LibraryContext context)
    {
        var registered = DataProviderRegistry.Register(SqliteDataProvider.Instance);
        registered.ThrowIfFailure();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task OnInitializeAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStartAsync(LibraryContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnStopAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<HealthStatus> HealthCheckAsync() => Task.FromResult(HealthStatus.Healthy("SQLite provider registered"));

    /// <inheritdoc />
    public void Dispose() { }
}
