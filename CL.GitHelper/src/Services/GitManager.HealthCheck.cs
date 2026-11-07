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
