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
public sealed partial class GitManager : IDisposable, IAsyncDisposable
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
}
