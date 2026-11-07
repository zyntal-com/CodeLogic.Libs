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

public sealed partial class GitRepository
{

    /// <summary>
    /// Cancellation for one network operation: the caller's token plus the repository timeout.
    /// libgit2 checks it in every progress callback, so a running transfer stops at the next one.
    /// A transfer that is still inside libgit2 <c>grace</c> after the timeout (no callback fired:
    /// a hung connection) is reported through <c>onStalled</c>.
    /// </summary>
    private sealed class Transfer : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _caller;
        private readonly TimeSpan _grace;
        private readonly Action _onStalled;
        private readonly Lock _sync = new();
        private Timer? _watchdog;
        private bool _running;
        private bool _completed;

        public Transfer(CancellationToken caller, int timeoutSeconds, TimeSpan grace, Action onStalled)
        {
            _caller = caller;
            _grace = grace;
            _onStalled = onStalled;
            TimeoutSeconds = timeoutSeconds;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            _cts.Token.UnsafeRegister(static state => ((Transfer)state!).OnCancelled(), this);
        }

        public int TimeoutSeconds { get; }
        public CancellationToken Token => _cts.Token;
        public bool Continue => !_cts.IsCancellationRequested;
        public bool TimedOut { get; private set; }

        /// <summary>The native call is about to start: a timeout from here on arms the stall watchdog.</summary>
        public void Begin()
        {
            lock (_sync)
            {
                _running = true;
                if (TimedOut) ArmWatchdog();
            }
        }

        /// <summary>The native call returned (or was never started): no stall can be reported any more.</summary>
        public void Complete()
        {
            lock (_sync)
            {
                _completed = true;
                _watchdog?.Dispose();
                _watchdog = null;
            }
        }

        private void OnCancelled()
        {
            if (_caller.IsCancellationRequested) return;   // the caller cancelled: libgit2 stops at its next callback
            lock (_sync)
            {
                TimedOut = true;
                if (_running) ArmWatchdog();
            }
        }

        private void ArmWatchdog()
        {
            if (_completed || _watchdog is not null) return;
            _watchdog = new Timer(static state => ((Transfer)state!).CheckStalled(), this, _grace, Timeout.InfiniteTimeSpan);
        }

        private void CheckStalled()
        {
            // Under the lock, so Complete() either sees the stall declared or prevents it.
            lock (_sync)
            {
                if (_completed) return;
                _onStalled();
            }
        }

        public void Dispose()
        {
            Complete();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Resolves the commit object that <see cref="ResetHardAsync"/> should target.
    /// </summary>
    private static Commit? ResolveResetTarget(Repository repo, string? branchName, string remoteName)
    {
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            var remoteTracking = repo.Branches[$"{remoteName}/{branchName}"];
            if (remoteTracking?.Tip is not null) return remoteTracking.Tip;

            var local = repo.Branches[branchName];
            if (local?.Tip is not null) return local.Tip;

            return null;
        }

        var head = repo.Head;
        if (head.IsTracking && head.TrackedBranch?.Tip is not null)
            return head.TrackedBranch.Tip;

        return head.Tip;
    }

    /// <summary>Opens the native repository on first use. Call with the lock held.</summary>
    private Repository EnsureOpen()
    {
        if (_repo is not null) return _repo;

        if (!IsCloned(_resolvedPath))
            throw new RepositoryNotFoundException(
                $"Repository not found at '{_resolvedPath}'. Clone it first.");

        return _repo = new Repository(_resolvedPath);
    }

    private static Signature GetSignature(Repository repo, string? name = null, string? email = null)
    {
        name ??= repo.Config.Get<string>("user.name")?.Value ?? "CodeLogic";
        email ??= repo.Config.Get<string>("user.email")?.Value ?? "codelogic@localhost";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    private RepositoryInfo BuildRepositoryInfo(Repository repo)
    {
        var isBare = repo.Info.IsBare;
        // A bare repository has no working tree, so it has no status to read.
        var status = isBare ? null : repo.RetrieveStatus();
        return new RepositoryInfo
        {
            Id = _config.Id,
            Name = _config.Name,
            LocalPath = _resolvedPath,
            // Remote URLs may carry user:token@; never hand those out.
            RemoteUrl = Redact(repo.Network.Remotes.FirstOrDefault()?.Url),
            CurrentBranch = repo.Head.FriendlyName,
            HeadCommitSha = repo.Head.Tip?.Sha ?? "",
            IsBare = isBare,
            State = repo.Info.CurrentOperation.ToString(),
            LocalBranchCount = repo.Branches.Count(b => !b.IsRemote),
            RemoteBranchCount = repo.Branches.Count(b => b.IsRemote),
            TagCount = repo.Tags.Count(),
            StashCount = isBare ? 0 : repo.Stashes.Count(),
            IsDirty = status?.IsDirty ?? false,
            ModifiedFiles = status?.Modified.Count() ?? 0,
            StagedFiles = status?.Staged.Count() ?? 0,
            UntrackedFiles = status?.Untracked.Count() ?? 0
        };
    }

    private static CommitInfo ConvertCommit(Commit c) => new()
    {
        Sha = c.Sha,
        Message = c.Message,
        AuthorName = c.Author.Name,
        AuthorEmail = c.Author.Email,
        AuthorDate = c.Author.When.DateTime,
        CommitterName = c.Committer.Name,
        CommitterEmail = c.Committer.Email,
        CommitDate = c.Committer.When.DateTime,
        ParentShas = c.Parents.Select(p => p.Sha).ToList()
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>True once the instance has been disposed; every operation then returns <see cref="GitErrors.RepositoryDisposedCode"/>.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Disposes this instance once the git operation currently running on it (if any) has
    /// finished. Used by <see cref="GitManager"/> when an instance is evicted or replaced.
    /// </summary>
    internal async Task DisposeWhenIdleAsync()
    {
        if (_disposed) return;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            ReleaseHandle();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Releases the native repository. A no-op for an instance obtained from <see cref="GitManager"/>
    /// (the manager owns and disposes it). For an instance that is not manager-owned and has an
    /// operation running, the handle is released when that operation finishes.
    /// </summary>
    public void Dispose()
    {
        if (_ownedByManager || _disposed) return;
        _disposed = true;
        if (!_lock.Wait(0)) return;    // an operation is running: Leave() releases the handle after it
        try { ReleaseHandle(); }
        finally { _lock.Release(); }
    }

    /// <summary>Call with the lock held.</summary>
    private void ReleaseHandle()
    {
        _repo?.Dispose();
        _repo = null;
    }
}
