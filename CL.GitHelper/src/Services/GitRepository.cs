using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

// Alias our model types that clash with LibGit2Sharp names.
using GitCloneOptions = CL.GitHelper.Models.CloneOptions;
using GitFetchOptions = CL.GitHelper.Models.FetchOptions;
using GitPullOptions = CL.GitHelper.Models.PullOptions;
using GitPushOptions = CL.GitHelper.Models.PushOptions;
using GitCommitOptions = CL.GitHelper.Models.CommitOptions;
using GitRepoStatus = CL.GitHelper.Models.RepositoryStatus;

namespace CL.GitHelper.Services;

/// <summary>
/// Wraps a LibGit2Sharp <see cref="Repository"/> and exposes typed Git operations.
/// Instances are created, cached and owned by <see cref="GitManager"/>: <see cref="Dispose"/> on an
/// instance obtained from the manager is a no-op, the manager disposes it when it is evicted, replaced
/// by a config change, or the library stops. Every operation, reads included, runs under one async
/// lock, so the native handle is never used by two operations at once nor disposed under a running one.
/// </summary>
public sealed partial class GitRepository : IDisposable
{
    /// <summary>
    /// How long after its timeout a clone, fetch or push may keep running inside libgit2 before it is
    /// declared stalled (30 s). See <see cref="IsStalled"/>.
    /// </summary>
    public static readonly TimeSpan DefaultStallGrace = TimeSpan.FromSeconds(30);

    private readonly RepositoryConfiguration _config;
    private readonly string _resolvedPath;
    private readonly ILogger? _logger;
    private readonly bool _ownedByManager;
    private readonly TimeSpan _stallGrace;
    private readonly int _timeoutSeconds;
    private Repository? _repo;
    private volatile bool _disposed;

    // The one lock every operation takes. It is never disposed: a disposed SemaphoreSlim leaves pending
    // waiters hanging forever, whereas a released one wakes them so they can see _disposed.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // A transfer that has not returned from libgit2 _stallGrace after its timeout holds _lock
    // indefinitely. Instead of queuing behind it, waiting and later callers are released with
    // git.timeout (via _stallSignal) until the native call returns and the flag is cleared.
    private readonly Lock _stallLock = new();
    private CancellationTokenSource _stallSignal = new();
    private bool _stalled;

    /// <summary>Initialises the repository service.</summary>
    /// <param name="config">Repository configuration entry.</param>
    /// <param name="resolvedPath">Absolute local path (pre-resolved by <see cref="GitManager"/>).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="defaultTimeoutSeconds">
    /// Timeout for network operations when <see cref="RepositoryConfiguration.TimeoutSeconds"/> is 0
    /// (<see cref="GitHelperConfig.DefaultTimeoutSeconds"/>).
    /// </param>
    /// <param name="ownedByManager">
    /// True for cached instances: <see cref="Dispose"/> is then a no-op and the manager disposes the
    /// instance through <see cref="DisposeWhenIdleAsync"/>.
    /// </param>
    /// <param name="stallGrace">How long after its timeout a transfer may run before it counts as stalled (<see cref="DefaultStallGrace"/>).</param>
    internal GitRepository(RepositoryConfiguration config, string resolvedPath, ILogger? logger = null,
        int defaultTimeoutSeconds = 300, bool ownedByManager = false, TimeSpan? stallGrace = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _resolvedPath = resolvedPath;
        _logger = logger;
        _ownedByManager = ownedByManager;
        _stallGrace = stallGrace ?? DefaultStallGrace;
        _timeoutSeconds = Math.Max(1, config.TimeoutSeconds > 0 ? config.TimeoutSeconds : defaultTimeoutSeconds);

        // SSH transport is not available in the LibGit2Sharp 0.30 managed binary. Warn loudly
        // rather than silently ignoring SSH config or failing with an opaque transport error later.
        if (!string.IsNullOrWhiteSpace(_config.SshKeyPath))
            _logger?.Warning(
                $"[GitHelper] Repository '{_config.Id}' has an SSH key configured, but SSH is not " +
                "supported (LibGit2Sharp 0.30 has no SSH transport). Use an HTTPS URL with a PAT instead.");
    }
}
