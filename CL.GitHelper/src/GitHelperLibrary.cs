using CL.GitHelper.Errors;
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
///   <item><description><b>Stop</b> — disposes the <see cref="GitManager"/> and every cached repository once its running operation finishes (bounded by <see cref="GitManager.DefaultDisposeTimeout"/>).</description></item>
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
    private bool _disabled;
    private bool _started;

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
            _disabled = true;
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return;
        }
        _disabled = false;

        // Resolve the base directory for relative repository paths.
        var baseDir = ResolveBaseDirectory(context, config);

        _manager = new GitManager(config, baseDir, context.Logger);
        context.Logger.Info($"GitManager ready — {config.Repositories.Count} repository configuration(s)");

        // Probe each repository so callers know their state immediately.
        await ProbeRepositoriesAsync(_manager).ConfigureAwait(false);

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
    /// <see cref="GitHelperConfig.Enabled"/> can be switched either way. A configuration that fails
    /// <see cref="GitHelperConfig.Validate"/> is rejected: it is logged and the current settings stay in effect,
    /// as an invalid file stops initialization.
    /// </summary>
    public async Task OnConfigChangedAsync(LibraryContext context, ConfigChangedEventArgs change, CancellationToken cancellationToken)
    {
        if (change.NewValue is not GitHelperConfig current) return;

        var validation = current.Validate();
        if (!validation.IsValid)
        {
            context.Logger.Error($"{Manifest.Name}: config change rejected, keeping the current settings — {validation}");
            return;
        }

        if (!current.Enabled)
        {
            _disabled = true;
            var old = _manager;
            if (old is null) return;
            _manager = null;
            old.Dispose();   // cached repositories are disposed once their running operation finishes
            context.Logger.Info($"{Manifest.Name} disabled by a config change");
            return;
        }
        _disabled = false;

        var baseDir = ResolveBaseDirectory(context, current);

        if (_manager is null)
        {
            var manager = _manager = new GitManager(current, baseDir, context.Logger);
            context.Logger.Info($"{Manifest.Name} enabled by a config change — {current.Repositories.Count} repository configuration(s)");
            await ProbeRepositoriesAsync(manager).ConfigureAwait(false);
            if (_started) manager.StartAutoFetch();
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
                await AutoFetchOneAsync(context, manager, id).ConfigureAwait(false);
        });
    }

    /// <summary>Fetches one repository at start-up or after a config change, logging the outcome.</summary>
    private static async Task AutoFetchOneAsync(LibraryContext context, GitManager manager, string id)
    {
        try
        {
            var repo = await manager.GetRepositoryAsync(id).ConfigureAwait(false);
            var result = repo.IsFailure ? repo.Error! : await repo.Value!.FetchAsync().ConfigureAwait(false);
            if (result.IsSuccess) context.Logger.Info($"  Auto-fetch OK: {id}");
            else context.Logger.Warning($"  Auto-fetch failed for '{id}': {result.Error!.Message}");
        }
        catch (Exception ex)
        {
            context.Logger.Error($"  Auto-fetch error for '{id}': {Redact(manager, id, ex)}");
        }
    }

    /// <summary>An exception message with the repository's configured token redacted.</summary>
    private static string Redact(GitManager manager, string repositoryId, Exception ex) =>
        GitSecurity.Redact(ex.Message, manager.GetConfiguration(repositoryId)?.Password);

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");
        _started = true;

        var manager = _manager;
        if (manager is null) return;
        manager.StartAutoFetch();

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
                await AutoFetchOneAsync(context, manager, id).ConfigureAwait(false);
        }

        context.Logger.Info($"{Manifest.Name} started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the library: background fetches are cancelled and every cached repository is disposed once
    /// the operation running on it finishes. Waits for that, bounded by <see cref="GitManager.DefaultDisposeTimeout"/>;
    /// a repository still busy after that is disposed when its operation returns.
    /// </summary>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");
        _started = false;
        var manager = _manager;
        _manager = null;
        if (manager is not null)
            await manager.DisposeAsync().ConfigureAwait(false);
        _context?.Logger.Info($"{Manifest.Name} stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        // Disabled on purpose is not a fault.
        if (_disabled)
            return HealthStatus.Healthy($"{Manifest.Name} is disabled in configuration.");

        var manager = _manager;
        if (manager is null)
            return HealthStatus.Unhealthy("Git manager not initialized.");

        try
        {
            var results = await manager.HealthCheckAsync().ConfigureAwait(false);
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
    /// <returns>
    /// The repository, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> (unknown id) or
    /// <see cref="GitErrors.NotInitializedCode"/> (called before <see cref="OnInitializeAsync"/>, while
    /// disabled, or after <see cref="OnStopAsync"/>).
    /// </returns>
    public Task<Result<GitRepository>> GetRepositoryAsync(string repositoryId = "Default") =>
        _manager is { } manager
            ? manager.GetRepositoryAsync(repositoryId)
            : Task.FromResult<Result<GitRepository>>(NotInitialized());

    /// <summary>Returns the underlying <see cref="GitManager"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when called before <see cref="OnInitializeAsync"/>, while disabled, or after <see cref="OnStopAsync"/>.</exception>
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
    /// <returns>
    /// Success, or an error: <see cref="GitErrors.InvalidConfigurationCode"/>,
    /// <see cref="GitErrors.RepositoryExistsCode"/> or <see cref="GitErrors.NotInitializedCode"/>.
    /// </returns>
    public Result RegisterRepository(RepositoryConfiguration configuration) =>
        _manager is { } manager ? manager.RegisterRepository(configuration) : NotInitialized();

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
    /// <returns>
    /// Repository ID → that repository's fetch result (diagnostics or error), or
    /// <see cref="GitErrors.NotInitializedCode"/> when the library is not running.
    /// </returns>
    public async Task<Result<Dictionary<string, Result<GitDiagnostics>>>> FetchAllAsync(
        FetchOptions? options = null,
        int maxConcurrency = 0)
    {
        if (_manager is not { } manager) return NotInitialized();
        return await manager.FetchAllAsync(options, maxConcurrency).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns working-tree status for all configured repositories.
    /// </summary>
    /// <param name="maxConcurrency">Maximum parallel status checks. 0 = use configuration default.</param>
    /// <returns>
    /// Repository ID → that repository's status result, or <see cref="GitErrors.NotInitializedCode"/>
    /// when the library is not running.
    /// </returns>
    public async Task<Result<Dictionary<string, Result<RepositoryStatus>>>> GetAllStatusAsync(int maxConcurrency = 0)
    {
        if (_manager is not { } manager) return NotInitialized();
        return await manager.GetAllStatusAsync(maxConcurrency).ConfigureAwait(false);
    }

    private Error NotInitialized() =>
        Error.Unavailable(GitErrors.NotInitializedCode, $"{Manifest.Name} is not initialized, is disabled, or has been stopped.");

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ProbeRepositoriesAsync(GitManager manager)
    {
        foreach (var id in manager.GetRepositoryIds())
        {
            try
            {
                var repo = await manager.GetRepositoryAsync(id).ConfigureAwait(false);
                var info = repo.IsFailure ? repo.Error! : await repo.Value!.GetRepositoryInfoAsync().ConfigureAwait(false);

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
                _context!.Logger.Error($"  '{id}': probe error — {Redact(manager, id, ex)}");
            }
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>
    /// Releases the manager without waiting: cached repositories are disposed once their running
    /// operation finishes. <see cref="OnStopAsync"/> is the bounded, awaited form.
    /// </summary>
    public void Dispose()
    {
        _manager?.Dispose();
        _manager = null;
    }
}
