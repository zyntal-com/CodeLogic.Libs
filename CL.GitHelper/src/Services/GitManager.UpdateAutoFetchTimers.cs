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
}
