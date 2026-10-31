using CL.GitHelper.Models;
using CodeLogic.Core.Results;
using CL.GitHelper.Services;
using CodeLogic.Core.Configuration;
using CodeLogic.Framework.Libraries;

namespace CL.GitHelper;

/// <summary>
/// <b>CL.GitHelper</b> — CodeLogic library providing managed Git repository operations
/// via <see href="https://github.com/libgit2/libgit2sharp">LibGit2Sharp</see>.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — registers <see cref="GitHelperConfig"/> (→ config.githelper.json).</description></item>
///   <item><description><b>Initialize</b> — loads config, resolves base directory, creates <see cref="GitManager"/>, probes repositories.</description></item>
///   <item><description><b>Start</b> — optionally triggers auto-fetch on repositories with <see cref="RepositoryConfiguration.AutoFetch"/> enabled.</description></item>
///   <item><description><b>Stop</b> — disposes the <see cref="GitManager"/> and all cached connections.</description></item>
/// </list>
/// Config changes made while running (admin UI, reload, file watcher) are applied live,
/// per repository — see <see cref="OnConfigChangedAsync"/>.
/// </para>
/// </summary>
public sealed class GitHelperLibrary : ILibrary, IConfigReloadable
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.GitHelper",
        Name = "Git Helper Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Git repository management with LibGit2Sharp integration",
        Author = "Zyntal.com",
        Tags = ["git", "vcs", "source-control"]
    };

    private LibraryContext? _context;
    private GitManager? _manager;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        // Register the config model → config.githelper.json will be generated if missing.
        context.Configuration.Register<GitHelperConfig>("githelper");

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<GitHelperConfig>();

        if (!config.Enabled)
        {
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return;
        }

        // Resolve the base directory for relative repository paths.
        var baseDir = ResolveBaseDirectory(context, config);

        _manager = new GitManager(config, baseDir, context.Logger);
        context.Logger.Info($"GitManager ready — {config.Repositories.Count} repository configuration(s)");

        // Probe each repository so callers know their state immediately.
        await ProbeRepositoriesAsync();

        context.Logger.Info($"{Manifest.Name} initialized");
    }

    private static string ResolveBaseDirectory(LibraryContext context, GitHelperConfig config)
    {
        var baseDir = string.IsNullOrWhiteSpace(config.BaseDirectory)
            ? Path.Combine(context.DataDirectory, "repositories")
            : config.BaseDirectory;

        Directory.CreateDirectory(baseDir);
        context.Logger.Info($"Repository base directory: {baseDir}");
        return baseDir;
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Applies a config change without a restart, touching only the repositories that changed.
    /// A removed or changed repository is dropped from the cache; its old <see cref="GitRepository"/>
    /// is disposed once the git operation running on it finishes, and the next
    /// <see cref="GetRepositoryAsync"/> opens it with the new settings. Untouched repositories keep
    /// their instances. Library-wide settings (cache, timeouts, concurrency, base directory) apply at
    /// once. Repositories added with <see cref="RegisterRepository"/> are kept. Added or changed
    /// repositories with <see cref="RepositoryConfiguration.AutoFetch"/> are fetched in the background.
    /// <see cref="GitHelperConfig.Enabled"/> can be switched either way.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (change.NewValue is not GitHelperConfig current) return;

        if (!current.Enabled)
        {
            var old = _manager;
            if (old is null) return;
            _manager = null;
            old.Retire();
            context.Logger.Info($"{Manifest.Name} disabled by a config change");
            return;
        }

        var baseDir = ResolveBaseDirectory(context, current);

        if (_manager is null)
        {
            _manager = new GitManager(current, baseDir, context.Logger);
            context.Logger.Info($"{Manifest.Name} enabled by a config change — {current.Repositories.Count} repository configuration(s)");
            await ProbeRepositoriesAsync();
            AutoFetchInBackground(context, current.Repositories.Where(r => r.AutoFetch).Select(r => r.Id));
            return;
        }

        var diff = await _manager.ApplyConfigurationAsync(current, baseDir).ConfigureAwait(false);

        foreach (var id in diff.Removed.Keys) context.Logger.Info($"Repository '{id}' removed");
        foreach (var id in diff.Changed.Keys) context.Logger.Info($"Repository '{id}' updated");
        foreach (var id in diff.Added.Keys) context.Logger.Info($"Repository '{id}' added");

        var refetch = diff.Added.Values.Concat(diff.Changed.Values.Select(c => c.New))
            .Where(r => r.AutoFetch)
            .Select(r => r.Id);
        AutoFetchInBackground(context, refetch);
    }

    private void AutoFetchInBackground(LibraryContext context, IEnumerable<string> repositoryIds)
    {
        var manager = _manager;
        var ids = repositoryIds.ToList();
        if (manager is null || ids.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var id in ids)
            {
                try
                {
                    var result = await (await manager.GetRepositoryAsync(id)).FetchAsync();
                    if (result.IsSuccess) context.Logger.Info($"  Auto-fetch OK: {id}");
                    else context.Logger.Warning($"  Auto-fetch failed for '{id}': {result.Error!.Message}");
                }
                catch (Exception ex)
                {
                    context.Logger.Error($"  Auto-fetch error for '{id}': {ex.Message}", ex);
                }
            }
        });
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");

        if (_manager is null) return;

        // Trigger auto-fetch on repositories that have it enabled.
        var autoFetchIds = _context
            .Configuration.Get<GitHelperConfig>()
            .Repositories
            .Where(r => r.AutoFetch)
            .Select(r => r.Id)
            .ToList();

        if (autoFetchIds.Count > 0)
        {
            context.Logger.Info($"Auto-fetching {autoFetchIds.Count} repository(ies)...");

            foreach (var id in autoFetchIds)
            {
                try
                {
                    var repo = await _manager.GetRepositoryAsync(id);
                    var result = await repo.FetchAsync();

                    if (result.IsSuccess)
                        context.Logger.Info($"  Auto-fetch OK: {id}");
                    else
                        context.Logger.Warning($"  Auto-fetch failed for '{id}': {result.Error!.Message}");
                }
                catch (Exception ex)
                {
                    context.Logger.Error($"  Auto-fetch error for '{id}': {ex.Message}", ex);
                }
            }
        }

        context.Logger.Info($"{Manifest.Name} started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");
        _manager?.Dispose();
        _manager = null;
        _context?.Logger.Info($"{Manifest.Name} stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (_manager is null)
            return HealthStatus.Unhealthy("Git manager not initialized.");

        try
        {
            var results = await _manager.HealthCheckAsync();
            if (results.Count == 0)
                return HealthStatus.Degraded("No repositories configured.");

            var healthy = results.Values.Count(v => v);
            var msg = $"{healthy}/{results.Count} repositories healthy";

            return healthy == results.Count
                ? HealthStatus.Healthy(msg)
                : healthy > 0
                    ? HealthStatus.Degraded(msg)
                    : HealthStatus.Unhealthy(msg);
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"Health check error: {ex.Message}", ex);
            return HealthStatus.FromException(ex);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="GitRepository"/> instance for the given configuration ID.
    /// Creates and caches the instance if not already cached.
    /// </summary>
    /// <param name="repositoryId">Repository ID from config (default: "Default").</param>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="OnInitializeAsync"/>.</exception>
    public Task<GitRepository> GetRepositoryAsync(string repositoryId = "Default")
    {
        if (_manager is null)
            throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

        return _manager.GetRepositoryAsync(repositoryId);
    }

    /// <summary>Returns the underlying <see cref="GitManager"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="OnInitializeAsync"/>.</exception>
    public GitManager GetManager()
    {
        if (_manager is null)
            throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

        return _manager;
    }

    /// <summary>
    /// Registers an additional repository configuration at runtime.
    /// Useful when repositories are discovered dynamically after startup.
    /// </summary>
    /// <param name="configuration">Repository to add. Must be valid and have a unique ID.</param>
    public void RegisterRepository(RepositoryConfiguration configuration)
    {
        if (_manager is null)
            throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

        _manager.RegisterRepository(configuration);
    }

    /// <summary>Returns a snapshot of the repository cache state.</summary>
    public CacheStats? GetCacheStats() => _manager?.GetCacheStats();

    /// <summary>Evicts all cached repository instances, forcing them to be re-opened on next access.</summary>
    public Task ClearCacheAsync() =>
        _manager?.ClearCacheAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Fetches all configured repositories concurrently.
    /// </summary>
    /// <param name="options">Fetch options applied to every repository.</param>
    /// <param name="maxConcurrency">Maximum parallel fetches. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's fetch result (diagnostics or error).</returns>
    public Task<Dictionary<string, Result<GitDiagnostics>>> FetchAllAsync(
        FetchOptions? options = null,
        int maxConcurrency = 0)
    {
        if (_manager is null)
            throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

        return _manager.FetchAllAsync(options, maxConcurrency);
    }

    /// <summary>
    /// Returns working-tree status for all configured repositories.
    /// </summary>
    /// <param name="maxConcurrency">Maximum parallel status checks. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's status result.</returns>
    public Task<Dictionary<string, Result<RepositoryStatus>>> GetAllStatusAsync(int maxConcurrency = 0)
    {
        if (_manager is null)
            throw new InvalidOperationException($"{Manifest.Name} has not been initialized.");

        return _manager.GetAllStatusAsync(maxConcurrency);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ProbeRepositoriesAsync()
    {
        if (_manager is null) return;

        foreach (var id in _manager.GetRepositoryIds())
        {
            try
            {
                var repo = await _manager.GetRepositoryAsync(id);
                var cfg = _manager.GetConfiguration(id)!;

                // Resolve path the same way the manager does (without exposing internals)
                var info = await repo.GetRepositoryInfoAsync();

                if (info.IsSuccess)
                {
                    _context!.Logger.Info(
                        $"  '{id}': branch={info.Value!.CurrentBranch}, dirty={info.Value.IsDirty}");
                }
                else
                {
                    _context!.Logger.Warning(
                        $"  '{id}': not yet cloned or inaccessible — {info.Error!.Message}");
                }
            }
            catch (Exception ex)
            {
                _context!.Logger.Error($"  '{id}': probe error — {ex.Message}", ex);
            }
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _manager?.Dispose();
        _manager = null;
    }
}
