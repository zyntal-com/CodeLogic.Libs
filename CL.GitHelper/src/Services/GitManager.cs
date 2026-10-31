using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CL.GitHelper.Services;

/// <summary>
/// Manages a pool of <see cref="GitRepository"/> instances.
/// Resolves local paths, applies caching, and exposes batch operations across all configured repositories.
/// </summary>
public sealed class GitManager : IDisposable
{
    // The manager's own copy of the config: runtime registrations change it, and a config
    // change swaps it as a whole, so readers always see one consistent snapshot.
    private volatile GitHelperConfig _config;
    private volatile string _baseDirectory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    // Repositories added with RegisterRepository rather than the config file; kept across config changes.
    private readonly ConcurrentDictionary<string, byte> _runtimeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private Timer? _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Initialises the manager.
    /// </summary>
    /// <param name="config">The loaded Git configuration.</param>
    /// <param name="baseDirectory">
    /// Absolute base directory used to resolve relative <see cref="RepositoryConfiguration.LocalPath"/> values.
    /// </param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="time">Clock for cache idle times (tests); the system clock by default.</param>
    internal GitManager(GitHelperConfig config, string baseDirectory, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = Clone(config);
        _baseDirectory = baseDirectory;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        UpdateCleanupTimer();
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Repository access ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="GitRepository"/> for the given configuration ID.
    /// Caches the instance when caching is enabled.
    /// </summary>
    /// <param name="repositoryId">Repository configuration ID (default: "Default").</param>
    /// <exception cref="ArgumentException">Thrown when no configuration matches <paramref name="repositoryId"/>.</exception>
    public async Task<GitRepository> GetRepositoryAsync(string repositoryId = "Default")
    {
        var repoConfig = _config.GetRepository(repositoryId)
            ?? throw new ArgumentException($"Repository configuration '{repositoryId}' not found.", nameof(repositoryId));

        if (!_config.EnableRepositoryCaching)
            return CreateRepository(repoConfig);

        // Fast path: entry exists and is valid
        if (_cache.TryGetValue(repositoryId, out var entry) && IsValid(entry))
        {
            entry.LastAccessed = UtcNow;
            return entry.Repository;
        }

        // Slow path: create under lock (double-check)
        await _cacheLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(repositoryId, out entry) && IsValid(entry))
            {
                entry.LastAccessed = UtcNow;
                return entry.Repository;
            }

            // Evict a stale entry. Idle time only counts calls to this method, so a caller may still be
            // running a long clone or fetch on it: dispose it once that operation has finished.
            if (_cache.TryRemove(repositoryId, out var stale))
                _ = stale.Repository.DisposeWhenIdleAsync();

            var repo = CreateRepository(repoConfig);
            _cache[repositoryId] = new CachedEntry(repo, UtcNow);
            _logger?.Debug($"Cached new repository instance: {repositoryId}");
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
    /// <exception cref="ArgumentException">Thrown when a configuration with the same ID already exists or is invalid.</exception>
    public void RegisterRepository(RepositoryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.IsValid())
            throw new ArgumentException("Invalid repository configuration.", nameof(configuration));

        if (_config.GetRepository(configuration.Id) is not null)
            throw new ArgumentException($"Repository '{configuration.Id}' is already registered.", nameof(configuration));

        _config.Repositories.Add(configuration);
        _runtimeIds[configuration.Id] = 0;
        _logger?.Info($"Registered repository: {configuration.Id}");
    }

    /// <summary>
    /// Removes a repository configuration and evicts any cached instance.
    /// </summary>
    /// <param name="repositoryId">ID of the repository to remove.</param>
    /// <returns>True if the configuration was found and removed.</returns>
    public async Task<bool> UnregisterRepositoryAsync(string repositoryId)
    {
        await EvictFromCacheAsync(repositoryId);

        var cfg = _config.GetRepository(repositoryId);
        if (cfg is null) return false;

        _config.Repositories.Remove(cfg);
        _runtimeIds.TryRemove(repositoryId, out _);
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
                           || !next.EnableRepositoryCaching;
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

        // Not awaited: a long clone or fetch on a retired instance must not block the config change.
        foreach (var repository in retired)
            _ = repository.DisposeWhenIdleAsync();

        return diff;
    }

    /// <summary>Disposes every cached repository once idle and stops the cleanup timer (library disabled live).</summary>
    internal void Retire()
    {
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        foreach (var id in _cache.Keys.ToList())
            if (_cache.TryRemove(id, out var entry))
                _ = entry.Repository.DisposeWhenIdleAsync();
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

    // ── Cache management ──────────────────────────────────────────────────────

    /// <summary>
    /// Removes a single cached repository and disposes it once the operation running on it (if any) has finished.
    /// </summary>
    public async Task EvictFromCacheAsync(string repositoryId)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (_cache.TryRemove(repositoryId, out var entry))
                _ = entry.Repository.DisposeWhenIdleAsync();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Removes all cached repositories and disposes each once the operation running on it (if any) has finished.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            foreach (var id in _cache.Keys.ToList())
                if (_cache.TryRemove(id, out var e))
                    _ = e.Repository.DisposeWhenIdleAsync();
            _logger?.Info("Repository cache cleared");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Returns a snapshot of the current cache state.</summary>
    public CacheStats GetCacheStats() => new()
    {
        CacheEnabled = _config.EnableRepositoryCaching,
        CacheTimeoutMinutes = _config.CacheTimeoutMinutes,
        Entries = _cache.Select(kvp => new CacheEntryStats
        {
            RepositoryId = kvp.Key,
            Age = UtcNow - kvp.Value.CreatedAt,
            TimeSinceLastAccess = UtcNow - kvp.Value.LastAccessed,
            IsExpired = !IsValid(kvp.Value)
        }).ToList()
    };

    // ── Batch operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs a delegate on every configured repository with bounded concurrency.
    /// </summary>
    /// <param name="operation">The operation to run. Receives the repository and its ID.</param>
    /// <param name="maxConcurrency">Maximum parallel operations. 0 = use <see cref="GitHelperConfig.MaxConcurrentOperations"/>.</param>
    public async Task<Dictionary<string, TResult>> ExecuteOnAllAsync<TResult>(
        Func<GitRepository, string, Task<TResult>> operation,
        int maxConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var concurrency = Math.Max(1, maxConcurrency > 0 ? maxConcurrency : _config.MaxConcurrentOperations);
        var results = new ConcurrentDictionary<string, TResult>();
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        var tasks = _config.Repositories.Select(async cfg =>
        {
            await sem.WaitAsync();
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id);
                results[cfg.Id] = await operation(repo, cfg.Id);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Batch operation failed for '{cfg.Id}': {ex.Message}", ex);
                // TResult must be constructable from the caller — let caller handle the cast/default
                throw;
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToDictionary(k => k.Key, k => k.Value);
    }

    /// <summary>
    /// Fetches all configured repositories concurrently.
    /// </summary>
    /// <param name="fetchOptions">Options applied to every fetch. Null uses defaults.</param>
    /// <param name="maxConcurrency">Maximum parallel fetches. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's fetch result (diagnostics or error).</returns>
    public async Task<Dictionary<string, Result<GitDiagnostics>>> FetchAllAsync(
        FetchOptions? fetchOptions = null,
        int maxConcurrency = 0)
    {
        var concurrency = Math.Max(1, maxConcurrency > 0 ? maxConcurrency : _config.MaxConcurrentOperations);
        var results = new ConcurrentDictionary<string, Result<GitDiagnostics>>();
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        var tasks = _config.Repositories.Select(async cfg =>
        {
            await sem.WaitAsync();
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id);
                results[cfg.Id] = await repo.FetchAsync(fetchOptions);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Fetch failed for '{cfg.Id}': {ex.Message}", ex);
                results[cfg.Id] = GitErrors.FromException(ex, ex.Message, GitErrors.FetchFailedCode);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToDictionary(k => k.Key, k => k.Value);
    }

    /// <summary>
    /// Retrieves the working-tree status of all configured repositories concurrently.
    /// </summary>
    /// <param name="maxConcurrency">Maximum parallel status checks. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's status result.</returns>
    public async Task<Dictionary<string, Result<RepositoryStatus>>> GetAllStatusAsync(
        int maxConcurrency = 0)
    {
        var concurrency = Math.Max(1, maxConcurrency > 0 ? maxConcurrency : _config.MaxConcurrentOperations);
        var results = new ConcurrentDictionary<string, Result<RepositoryStatus>>();
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        var tasks = _config.Repositories.Select(async cfg =>
        {
            await sem.WaitAsync();
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id);
                results[cfg.Id] = await repo.GetStatusAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error($"Status failed for '{cfg.Id}': {ex.Message}", ex);
                results[cfg.Id] = GitErrors.FromException(ex, ex.Message, GitErrors.StatusFailedCode);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToDictionary(k => k.Key, k => k.Value);
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a health check on every configured repository.
    /// </summary>
    /// <returns>Dictionary mapping repository ID → healthy flag.</returns>
    public async Task<Dictionary<string, bool>> HealthCheckAsync()
    {
        var results = new Dictionary<string, bool>();

        foreach (var cfg in _config.Repositories)
        {
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id);
                var info = await repo.GetRepositoryInfoAsync();
                results[cfg.Id] = info.IsSuccess;

                if (info.IsSuccess)
                    _logger?.Info($"  {cfg.Id}: healthy");
                else
                    _logger?.Error($"  {cfg.Id}: {info.Error!.Message}");
            }
            catch (Exception ex)
            {
                _logger?.Error($"  {cfg.Id}: {ex.Message}", ex);
                results[cfg.Id] = false;
            }
        }

        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private GitRepository CreateRepository(RepositoryConfiguration cfg)
    {
        var resolved = ResolvePath(cfg.LocalPath);
        return new GitRepository(cfg, resolved, _logger);
    }

    private string ResolvePath(string localPath)
    {
        if (Path.IsPathRooted(localPath)) return localPath;
        return string.IsNullOrWhiteSpace(_baseDirectory)
            ? Path.GetFullPath(localPath)
            : Path.Combine(_baseDirectory, localPath);
    }

    private bool IsValid(CachedEntry entry)
    {
        if (_config.CacheTimeoutMinutes <= 0) return true;
        // Evict on IDLE time, not absolute age: a recently-accessed entry is presumed
        // in use, so the cleanup timer won't dispose a repository out from under an
        // active caller. Only genuinely idle entries expire.
        return (UtcNow - entry.LastAccessed).TotalMinutes < _config.CacheTimeoutMinutes;
    }

    internal void CleanupExpiredEntries()
    {
        if (_disposed) return;
        var expired = _cache.Where(kvp => !IsValid(kvp.Value)).Select(kvp => kvp.Key).ToList();
        foreach (var id in expired)
        {
            // Idle means no GetRepositoryAsync call, not no running operation: wait for one to finish.
            if (_cache.TryRemove(id, out var e))
                _ = e.Repository.DisposeWhenIdleAsync();
        }

        if (expired.Count > 0)
            _logger?.Debug($"Evicted {expired.Count} expired cache entr(ies)");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _cleanupTimer?.Dispose();
        foreach (var e in _cache.Values) e.Repository.Dispose();
        _cache.Clear();
        _cacheLock.Dispose();
        _disposed = true;
    }

    // ── Internal types ────────────────────────────────────────────────────────

    private sealed class CachedEntry
    {
        public GitRepository Repository { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastAccessed { get; set; }

        public CachedEntry(GitRepository repo, DateTime now)
        {
            Repository = repo;
            CreatedAt = now;
            LastAccessed = now;
        }
    }
}

// ── Public cache stats DTOs ───────────────────────────────────────────────────

/// <summary>Snapshot of the repository cache state.</summary>
public sealed class CacheStats
{
    /// <summary>Whether caching is enabled in the configuration.</summary>
    public bool CacheEnabled { get; init; }

    /// <summary>Configured cache entry lifetime in minutes.</summary>
    public int CacheTimeoutMinutes { get; init; }

    /// <summary>Per-entry details.</summary>
    public List<CacheEntryStats> Entries { get; init; } = [];

    /// <summary>Total number of currently cached entries.</summary>
    public int TotalCached => Entries.Count;
}

/// <summary>Statistics for a single cached repository entry.</summary>
public sealed class CacheEntryStats
{
    /// <summary>Repository configuration ID.</summary>
    public string RepositoryId { get; init; } = "";

    /// <summary>How long this entry has been in the cache.</summary>
    public TimeSpan Age { get; init; }

    /// <summary>Time elapsed since the entry was last accessed.</summary>
    public TimeSpan TimeSinceLastAccess { get; init; }

    /// <summary>Whether this entry has exceeded the configured timeout.</summary>
    public bool IsExpired { get; init; }
}
