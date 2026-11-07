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
/// Cached instances are owned by the manager: it disposes them when they are evicted, replaced by a
/// config change, or the manager is disposed, each once the operation running on it has finished.
/// </summary>
public sealed class GitManager : IDisposable, IAsyncDisposable
{
    /// <summary>How long <see cref="DisposeAsync()"/> waits for running operations before returning (30 s).</summary>
    public static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(30);

    // The manager's own copy of the config: runtime registrations and config changes replace it as a
    // whole (clone and swap under _cacheLock), so readers always see one consistent, never-mutated snapshot.
    private volatile GitHelperConfig _config;
    private volatile string _baseDirectory;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    // Never disposed: a disposed SemaphoreSlim leaves pending waiters hanging; a released one wakes them.
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    // Repositories added with RegisterRepository rather than the config file; kept across config changes.
    private readonly ConcurrentDictionary<string, byte> _runtimeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private Timer? _cleanupTimer;
    private volatile bool _disposed;
    private readonly Lock _disposeLock = new();
    private Task? _shutdown;

    // Background fetch per repository with AutoFetchIntervalMinutes > 0, once StartAutoFetch has run.
    private readonly Lock _autoFetchLock = new();
    private readonly Dictionary<string, AutoFetchEntry> _autoFetch = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private bool _autoFetchStarted;

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

    private void UpdateAutoFetchTimers()
    {
        lock (_autoFetchLock)
        {
            if (!_autoFetchStarted || _disposed) return;

            var wanted = _config.Repositories
                .Where(r => r.AutoFetchIntervalMinutes > 0)
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => TimeSpan.FromMinutes(g.First().AutoFetchIntervalMinutes),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var id in _autoFetch.Keys.ToList())
            {
                if (wanted.TryGetValue(id, out var interval) && interval == _autoFetch[id].Interval) continue;
                _autoFetch[id].Timer.Dispose();
                _autoFetch.Remove(id);
            }

            foreach (var (id, interval) in wanted)
            {
                if (_autoFetch.ContainsKey(id)) continue;
                var entry = new AutoFetchEntry(interval);
                entry.Timer = _time.CreateTimer(_ => _ = AutoFetchAsync(id, entry), null, interval, interval);
                _autoFetch[id] = entry;
            }
        }
    }

    /// <summary>One scheduled fetch. Skipped while the previous fetch of the same repository still runs.</summary>
    internal async Task AutoFetchAsync(string repositoryId, AutoFetchEntry? entry = null)
    {
        if (entry is not null && Interlocked.Exchange(ref entry.Running, 1) == 1) return;
        try
        {
            if (_disposed || _stopping.IsCancellationRequested) return;
            var repository = await GetRepositoryAsync(repositoryId).ConfigureAwait(false);
            if (repository.IsFailure) return;   // removed by a config change between the tick and the fetch
            var result = await repository.Value!.FetchAsync(new FetchOptions { CancellationToken = _stopping.Token })
                .ConfigureAwait(false);
            if (result.IsSuccess) _logger?.Debug($"Auto-fetch OK: {repositoryId}");
            else _logger?.Warning($"Auto-fetch failed for '{repositoryId}': {result.Error!.Message}");
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the tick and the fetch.
        }
        catch (Exception ex)
        {
            LogFailure("Auto-fetch", repositoryId, ex);
        }
        finally
        {
            if (entry is not null) Volatile.Write(ref entry.Running, 0);
        }
    }

    private void StopAutoFetch()
    {
        lock (_autoFetchLock)
        {
            _autoFetchStarted = false;
            foreach (var entry in _autoFetch.Values) entry.Timer.Dispose();
            _autoFetch.Clear();
        }
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal sealed class AutoFetchEntry(TimeSpan interval)
    {
        public TimeSpan Interval { get; } = interval;
        public ITimer Timer { get; set; } = null!;
        public int Running;
    }

    // ── Cache management ──────────────────────────────────────────────────────

    /// <summary>
    /// Removes a single cached repository and disposes it once the operation running on it (if any) has finished.
    /// </summary>
    public async Task EvictFromCacheAsync(string repositoryId)
    {
        await _cacheLock.WaitAsync().ConfigureAwait(false);
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
        await _cacheLock.WaitAsync().ConfigureAwait(false);
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
    /// Runs a delegate on every configured repository with bounded concurrency. A repository whose
    /// instance cannot be obtained (removed by a concurrent config change) is logged and left out of
    /// the result; an exception thrown by <paramref name="operation"/> is logged and rethrown.
    /// </summary>
    /// <param name="operation">The operation to run. Receives the repository and its ID.</param>
    /// <param name="maxConcurrency">Maximum parallel operations. 0 = use <see cref="GitHelperConfig.MaxConcurrentOperations"/>.</param>
    public async Task<Dictionary<string, TResult>> ExecuteOnAllAsync<TResult>(
        Func<GitRepository, string, Task<TResult>> operation,
        int maxConcurrency = 0)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var config = _config;
        var concurrency = Math.Max(1, maxConcurrency > 0 ? maxConcurrency : config.MaxConcurrentOperations);
        var results = new ConcurrentDictionary<string, TResult>();
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        var tasks = config.Repositories.Select(async cfg =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id).ConfigureAwait(false);
                if (repo.IsFailure)
                {
                    _logger?.Warning($"Batch operation skipped '{cfg.Id}': {repo.Error!.Message}");
                    return;
                }
                results[cfg.Id] = await operation(repo.Value!, cfg.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFailure("Batch operation", cfg.Id, ex);
                throw;
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(k => k.Key, k => k.Value);
    }

    /// <summary>
    /// Fetches all configured repositories concurrently.
    /// </summary>
    /// <param name="fetchOptions">Options applied to every fetch. Null uses defaults.</param>
    /// <param name="maxConcurrency">Maximum parallel fetches. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's fetch result (diagnostics or error).</returns>
    public Task<Dictionary<string, Result<GitDiagnostics>>> FetchAllAsync(
        FetchOptions? fetchOptions = null,
        int maxConcurrency = 0) =>
        ForEachAsync("Fetch", GitErrors.FetchFailedCode, repo => repo.FetchAsync(fetchOptions), maxConcurrency);

    /// <summary>
    /// Retrieves the working-tree status of all configured repositories concurrently.
    /// </summary>
    /// <param name="maxConcurrency">Maximum parallel status checks. 0 = use configuration default.</param>
    /// <returns>Repository ID → that repository's status result.</returns>
    public Task<Dictionary<string, Result<RepositoryStatus>>> GetAllStatusAsync(
        int maxConcurrency = 0) =>
        ForEachAsync("Status", GitErrors.StatusFailedCode, repo => repo.GetStatusAsync(), maxConcurrency);

    private async Task<Dictionary<string, Result<T>>> ForEachAsync<T>(string operation, string failureCode,
        Func<GitRepository, Task<Result<T>>> call, int maxConcurrency)
    {
        var config = _config;
        var concurrency = Math.Max(1, maxConcurrency > 0 ? maxConcurrency : config.MaxConcurrentOperations);
        var results = new ConcurrentDictionary<string, Result<T>>();
        using var sem = new SemaphoreSlim(concurrency, concurrency);

        var tasks = config.Repositories.Select(async cfg =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                var repo = await GetRepositoryAsync(cfg.Id).ConfigureAwait(false);
                results[cfg.Id] = repo.IsFailure ? repo.Error! : await call(repo.Value!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                results[cfg.Id] = Unexpected(operation, cfg.Id, ex, failureCode);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
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
                var repo = await GetRepositoryAsync(cfg.Id).ConfigureAwait(false);
                var info = repo.IsFailure ? repo.Error! : await repo.Value!.GetRepositoryInfoAsync().ConfigureAwait(false);
                results[cfg.Id] = info.IsSuccess;

                if (info.IsSuccess)
                    _logger?.Info($"  {cfg.Id}: healthy");
                else
                    _logger?.Error($"  {cfg.Id}: {info.Error!.Message}");
            }
            catch (Exception ex)
            {
                LogFailure("Health check", cfg.Id, ex);
                results[cfg.Id] = false;
            }
        }

        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private GitRepository CreateRepository(RepositoryConfiguration cfg, GitHelperConfig config, bool owned)
    {
        var resolved = ResolvePath(cfg.LocalPath);
        return new GitRepository(cfg, resolved, _logger, config.DefaultTimeoutSeconds, ownedByManager: owned);
    }

    private string ResolvePath(string localPath)
    {
        if (Path.IsPathRooted(localPath)) return Path.GetFullPath(localPath);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(_baseDirectory)
            ? localPath
            : Path.Combine(_baseDirectory, localPath));
    }

    private bool IsValid(CachedEntry entry)
    {
        // A disposed instance (consumer Dispose is a no-op, but a replaced instance may still be
        // referenced) must never be handed out again.
        if (entry.Repository.IsDisposed) return false;
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

    private static Error NotConfigured(string repositoryId) =>
        Error.NotFound(GitErrors.RepositoryNotFoundCode, $"Repository '{repositoryId}' is not configured.", GitErrors.NotConfiguredDetails);

    private static Error Disposed() =>
        Error.Unavailable(GitErrors.NotInitializedCode, "The Git manager has been stopped.");

    /// <summary>Logs an unexpected exception with the repository's token redacted and maps it to an error.</summary>
    private Error Unexpected(string operation, string repositoryId, Exception ex, string fallbackCode)
    {
        var message = LogFailure(operation, repositoryId, ex);
        return GitErrors.FromException(ex, $"{operation} failed: {message}", fallbackCode);
    }

    /// <summary>Logs an unexpected exception with the repository's token redacted; returns the redacted message.</summary>
    private string LogFailure(string operation, string repositoryId, Exception ex)
    {
        var secret = GetConfiguration(repositoryId)?.Password;
        var message = GitSecurity.Redact(ex.Message, secret);
        _logger?.Error($"{operation} failed for '{repositoryId}': {message}");
        _logger?.Debug($"{operation} failure detail for '{repositoryId}': {GitSecurity.Redact(ex.ToString(), secret)}");
        return message;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the auto-fetch timers and the cache sweep, cancels running background fetches, and
    /// disposes every cached repository once the operation running on it (if any) has finished,
    /// without waiting. <see cref="DisposeAsync()"/> waits for them, bounded by a timeout.
    /// </summary>
    public void Dispose() => Shutdown();

    /// <summary>Same as <see cref="DisposeAsync(TimeSpan)"/> with <see cref="DefaultDisposeTimeout"/>.</summary>
    public ValueTask DisposeAsync() => DisposeAsync(DefaultDisposeTimeout);

    /// <summary>
    /// Like <see cref="Dispose"/>, then waits up to <paramref name="timeout"/> for the cached repositories'
    /// running operations to finish and their instances to be disposed. A repository still busy when the
    /// timeout passes is logged and disposed later, when its operation returns.
    /// </summary>
    public async ValueTask DisposeAsync(TimeSpan timeout)
    {
        var shutdown = Shutdown();
        if (shutdown.IsCompleted) return;
        try
        {
            await shutdown.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.Warning($"[GitHelper] Repositories were still busy {timeout.TotalSeconds:F0}s after stop; " +
                "they are disposed when their operation finishes.");
        }
    }

    /// <summary>Runs the shutdown once; returns the task that completes when every retired repository is disposed.</summary>
    private Task Shutdown()
    {
        lock (_disposeLock)
        {
            if (_shutdown is not null) return _shutdown;
            _disposed = true;
            StopAutoFetch();
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;

            var pending = new List<Task>();
            foreach (var id in _cache.Keys.ToList())
                if (_cache.TryRemove(id, out var entry))
                    pending.Add(entry.Repository.DisposeWhenIdleAsync());
            return _shutdown = Task.WhenAll(pending);
        }
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
