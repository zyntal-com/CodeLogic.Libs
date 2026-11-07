using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CL.GitHelper.Services;

public sealed partial class GitManager
{

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Repository access ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="GitRepository"/> for the given configuration ID, creating and caching
    /// the instance when caching is enabled. A cached instance that was disposed is replaced.
    /// </summary>
    /// <param name="repositoryId">Repository configuration ID (default: "Default"), case-insensitive.</param>
    /// <returns>
    /// The repository, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> (no configuration
    /// with that id; <see cref="Error.Details"/> is <see cref="GitErrors.NotConfiguredDetails"/>) or
    /// <see cref="GitErrors.NotInitializedCode"/> (the manager is disposed).
    /// </returns>
    public async Task<Result<GitRepository>> GetRepositoryAsync(string repositoryId = "Default")
    {
        if (_disposed) return Disposed();

        var config = _config;
        var repoConfig = config.GetRepository(repositoryId);
        if (repoConfig is null) return NotConfigured(repositoryId);

        if (!config.EnableRepositoryCaching)
            return CreateRepository(repoConfig, config, owned: false);

        // Fast path: entry exists and is valid
        if (_cache.TryGetValue(repoConfig.Id, out var entry) && IsValid(entry))
        {
            entry.LastAccessed = UtcNow;
            return entry.Repository;
        }

        // Slow path: create under lock (double-check)
        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return Disposed();
            if (_cache.TryGetValue(repoConfig.Id, out entry) && IsValid(entry))
            {
                entry.LastAccessed = UtcNow;
                return entry.Repository;
            }

            // Evict a stale entry. Idle time only counts calls to this method, so a caller may still be
            // running a long clone or fetch on it: dispose it once that operation has finished.
            if (_cache.TryRemove(repoConfig.Id, out var stale))
                _ = stale.Repository.DisposeWhenIdleAsync();

            var repo = CreateRepository(repoConfig, config, owned: true);
            _cache[repoConfig.Id] = new CachedEntry(repo, UtcNow);
            _logger?.Debug($"Cached new repository instance: {repoConfig.Id}");
            return repo;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Returns IDs of all configured repositories.</summary>
    public IReadOnlyList<string> GetRepositoryIds() =>
        _config.Repositories.Select(r => r.Id).ToList();

    /// <summary>Returns the <see cref="RepositoryConfiguration"/> for the given ID, or null.</summary>
    public RepositoryConfiguration? GetConfiguration(string repositoryId) =>
        _config.GetRepository(repositoryId);

    // ── Dynamic registration ──────────────────────────────────────────────────

    /// <summary>
    /// Adds a new repository configuration at runtime.
    /// </summary>
    /// <param name="configuration">Configuration to add. Must be valid and have a unique ID.</param>
    /// <returns>
    /// Success, or an error: <see cref="GitErrors.InvalidConfigurationCode"/> (no URL or local path, or
    /// a relative local path that leaves the base directory), <see cref="GitErrors.RepositoryExistsCode"/>
    /// (the id is already registered, case-insensitive) or <see cref="GitErrors.NotInitializedCode"/>.
    /// </returns>
    public Result RegisterRepository(RepositoryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_disposed) return Disposed();

        if (string.IsNullOrWhiteSpace(configuration.RepositoryUrl) || string.IsNullOrWhiteSpace(configuration.LocalPath))
            return Error.Validation(GitErrors.InvalidConfigurationCode,
                $"Repository '{configuration.Id}': RepositoryUrl and LocalPath are required.");
        if (RepositoryConfiguration.LocalPathEscapesBaseDirectory(configuration.LocalPath))
            return Error.Validation(GitErrors.InvalidConfigurationCode,
                $"Repository '{configuration.Id}': LocalPath '{configuration.LocalPath}' leaves the base directory; use a path inside it or an absolute path.");

        _cacheLock.Wait();
        try
        {
            if (_config.GetRepository(configuration.Id) is not null)
                return Error.Conflict(GitErrors.RepositoryExistsCode, $"Repository '{configuration.Id}' is already registered.");

            var next = Clone(_config);
            next.Repositories.Add(configuration);
            _config = next;
            _runtimeIds[configuration.Id] = 0;
        }
        finally
        {
            _cacheLock.Release();
        }
        UpdateAutoFetchTimers();
        _logger?.Info($"Registered repository: {configuration.Id}");
        return Result.Success();
    }

    /// <summary>
    /// Removes a repository configuration and evicts any cached instance.
    /// </summary>
    /// <param name="repositoryId">ID of the repository to remove.</param>
    /// <returns>True if the configuration was found and removed.</returns>
    public async Task<bool> UnregisterRepositoryAsync(string repositoryId)
    {
        await EvictFromCacheAsync(repositoryId).ConfigureAwait(false);

        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cfg = _config.GetRepository(repositoryId);
            if (cfg is null) return false;

            var next = Clone(_config);
            next.Repositories.RemoveAll(r => r.Id.Equals(cfg.Id, StringComparison.OrdinalIgnoreCase));
            _config = next;
            _runtimeIds.TryRemove(cfg.Id, out _);
        }
        finally
        {
            _cacheLock.Release();
        }
        UpdateAutoFetchTimers();
        _logger?.Info($"Unregistered repository: {repositoryId}");
        return true;
    }

    // ── Live config changes ──────────────────────────────────────────────────

    /// <summary>
    /// Switches to a changed configuration. Repositories that were removed or whose settings
    /// changed are evicted from the cache and disposed once their running git operation finishes;
    /// untouched repositories keep their instances. A changed base directory re-resolves every
    /// relative path, so all cached instances are replaced. Repositories added with
    /// <see cref="RegisterRepository"/> are kept unless the file now defines the same ID.
    /// </summary>
    internal async Task<KeyedDiff<RepositoryConfiguration>> ApplyConfigurationAsync(GitHelperConfig config, string baseDirectory)
    {
        var next = Clone(config);
        foreach (var id in next.Repositories.Select(r => r.Id))
            _runtimeIds.TryRemove(id, out _);
        next.Repositories.AddRange(_config.Repositories.Where(r => _runtimeIds.ContainsKey(r.Id)));

        var diff = ConfigDiff.Keyed(ToMap(_config), ToMap(next), StringComparer.OrdinalIgnoreCase);
        var retired = new List<GitRepository>();

        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var evictAll = !string.Equals(baseDirectory, _baseDirectory, StringComparison.Ordinal)
                           || !next.EnableRepositoryCaching
                           || next.DefaultTimeoutSeconds != _config.DefaultTimeoutSeconds;
            var evict = evictAll
                ? _cache.Keys.ToList()
                : diff.Removed.Keys.Concat(diff.Changed.Keys).ToList();
            foreach (var id in evict)
                if (_cache.TryRemove(id, out var entry))
                    retired.Add(entry.Repository);

            _config = next;
            _baseDirectory = baseDirectory;
            UpdateCleanupTimer();
        }
        finally
        {
            _cacheLock.Release();
        }
        UpdateAutoFetchTimers();

        // Not awaited: a long clone or fetch on a retired instance must not block the config change.
        foreach (var repository in retired)
            _ = repository.DisposeWhenIdleAsync();

        return diff;
    }

    private static Dictionary<string, RepositoryConfiguration> ToMap(GitHelperConfig config)
    {
        var map = new Dictionary<string, RepositoryConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in config.Repositories) map.TryAdd(repository.Id, repository);
        return map;
    }

    private static GitHelperConfig Clone(GitHelperConfig config) =>
        JsonSerializer.Deserialize<GitHelperConfig>(JsonSerializer.Serialize(config))!;

    private void UpdateCleanupTimer()
    {
        var needed = _config.EnableRepositoryCaching && _config.CacheTimeoutMinutes > 0;
        if (needed && _cleanupTimer is null)
            _cleanupTimer = new Timer(_ => CleanupExpiredEntries(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        else if (!needed && _cleanupTimer is not null)
        {
            _cleanupTimer.Dispose();
            _cleanupTimer = null;
        }
    }

    // ── Background auto-fetch ─────────────────────────────────────────────────

    /// <summary>
    /// Starts a background fetch timer for every repository whose
    /// <see cref="RepositoryConfiguration.AutoFetchIntervalMinutes"/> is above 0. Config changes
    /// add, retime or remove timers from then on. A fetch still running when its next tick comes
    /// is not started twice; disposing the manager cancels running fetches.
    /// </summary>
    internal void StartAutoFetch()
    {
        lock (_autoFetchLock)
        {
            if (_disposed) return;
            _autoFetchStarted = true;
        }
        UpdateAutoFetchTimers();
    }

    /// <summary>Repository ID → auto-fetch interval for every timer currently scheduled.</summary>
    internal IReadOnlyDictionary<string, TimeSpan> AutoFetchSchedule
    {
        get
        {
            lock (_autoFetchLock)
                return _autoFetch.ToDictionary(e => e.Key, e => e.Value.Interval, StringComparer.OrdinalIgnoreCase);
        }
    }
}
