using CL.GitHelper.Models;
using CodeLogic.Core.Logging;
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
/// Instances are created and cached by <see cref="GitManager"/>.
/// All write operations are guarded by an async semaphore to prevent concurrent access.
/// </summary>
public sealed class GitRepository : IDisposable
{
    private readonly RepositoryConfiguration _config;
    private readonly string _resolvedPath;
    private readonly ILogger? _logger;
    private Repository? _repo;
    private bool _disposed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Initialises the repository service.</summary>
    /// <param name="config">Repository configuration entry.</param>
    /// <param name="resolvedPath">Absolute local path (pre-resolved by <see cref="GitManager"/>).</param>
    /// <param name="logger">Optional scoped logger.</param>
    internal GitRepository(RepositoryConfiguration config, string resolvedPath, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _resolvedPath = resolvedPath;
        _logger = logger;

        // SSH transport is not available in the LibGit2Sharp 0.30 managed binary. Warn loudly
        // rather than silently ignoring SSH config or failing with an opaque transport error later.
        if (!string.IsNullOrWhiteSpace(_config.SshKeyPath))
            _logger?.Warning(
                $"[GitHelper] Repository '{_config.Id}' has an SSH key configured, but SSH is not " +
                "supported (LibGit2Sharp 0.30 has no SSH transport). Use an HTTPS URL with a PAT instead.");
    }

    /// <summary>
    /// True when the URL uses the SSH transport (<c>git@host:org/repo</c> or <c>ssh://…</c>),
    /// which this library cannot service. Used to fail fast with a clear message.
    /// </summary>
    private static bool IsSshUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("git@", StringComparison.OrdinalIgnoreCase));

    // ── Clone ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clones the configured remote repository into the local path.
    /// Fails if the local directory already exists and is non-empty.
    /// </summary>
    /// <param name="options">Optional clone settings.</param>
    public async Task<GitResult<RepositoryInfo>> CloneAsync(GitCloneOptions? options = null)
    {
        options ??= new GitCloneOptions();
        var diag = new GitDiagnostics();
        diag.AddMessage("Starting clone");

        try
        {
            if (IsSshUrl(_config.RepositoryUrl))
                return GitResult<RepositoryInfo>.Fail(
                    "SSH URLs are not supported — LibGit2Sharp 0.30 has no SSH transport. " +
                    "Use an HTTPS URL with a personal access token.", null, diag);

            if (Directory.Exists(_resolvedPath))
            {
                if (Directory.GetFileSystemEntries(_resolvedPath).Length > 0)
                    return GitResult<RepositoryInfo>.Fail(
                        $"Directory '{_resolvedPath}' already exists and is not empty.", null, diag);
            }
            else
            {
                Directory.CreateDirectory(_resolvedPath);
                diag.AddMessage($"Created directory: {_resolvedPath}");
            }

            _logger?.Info($"Cloning {_config.RepositoryUrl} → {_resolvedPath}");

            var libOptions = new LibGit2Sharp.CloneOptions
            {
                BranchName = options.BranchName,
                RecurseSubmodules = options.RecurseSubmodules,
                IsBare = options.Bare
            };
            var credHandler = BuildCredentialsHandler();
            if (credHandler is not null)
                libOptions.FetchOptions.CredentialsProvider = credHandler;

            await Task.Run(() =>
                Repository.Clone(_config.RepositoryUrl, _resolvedPath, libOptions),
                options.CancellationToken);

            diag.Complete();
            _logger?.Info($"Clone complete in {diag.Duration?.TotalSeconds:F2}s");

            _repo = new Repository(_resolvedPath);
            var info = BuildRepositoryInfo();

            return GitResult<RepositoryInfo>.Ok(info, diag);
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Clone failed: {ex.Message}", ex);
            return GitResult<RepositoryInfo>.Fail($"Clone failed: {ex.Message}", ex, diag);
        }
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches changes from the configured remote without merging.
    /// </summary>
    /// <param name="options">Optional fetch settings.</param>
    public async Task<GitResult<bool>> FetchAsync(GitFetchOptions? options = null)
    {
        options ??= new GitFetchOptions();
        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                diag.AddMessage($"Fetching from {options.RemoteName}");
                _logger?.Info($"Fetching '{_config.Id}' from {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitResult<bool>.Fail($"Remote '{options.RemoteName}' not found", null, diag);

                var refSpecs = remote.FetchRefSpecs.Select(s => s.Specification);

                var fetchOpts = new LibGit2Sharp.FetchOptions
                {
                    Prune = options.Prune,
                    TagFetchMode = options.FetchTags ? TagFetchMode.Auto : TagFetchMode.None
                };
                var credHandler = BuildCredentialsHandler();
                if (credHandler is not null)
                    fetchOpts.CredentialsProvider = credHandler;

                await Task.Run(() =>
                    Commands.Fetch(_repo, options.RemoteName, refSpecs, fetchOpts, null),
                    options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Fetch complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Fetch failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Fetch failed: {ex.Message}", ex, diag);
        }
    }

    // ── Pull ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches and integrates remote changes.
    /// </summary>
    /// <param name="options">Optional pull settings.</param>
    public async Task<GitResult<bool>> PullAsync(GitPullOptions? options = null)
    {
        options ??= new GitPullOptions();
        var diag = new GitDiagnostics();

        try
        {
            var fetchResult = await FetchAsync(options.FetchOptions);
            if (!fetchResult.IsSuccess)
                return GitResult<bool>.Fail(fetchResult.ErrorMessage!, fetchResult.Exception, diag);

            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                diag.AddMessage("Merging fetched changes");
                _logger?.Info($"Pulling '{_config.Id}'");

                var sig = GetSignature();
                await Task.Run(() => Commands.Pull(_repo!, sig, null), options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Pull complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Pull failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Pull failed: {ex.Message}", ex, diag);
        }
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes local commits to the remote.
    /// </summary>
    /// <param name="options">Optional push settings.</param>
    public async Task<GitResult<bool>> PushAsync(GitPushOptions? options = null)
    {
        options ??= new GitPushOptions();
        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                var branch = options.BranchName ?? _repo!.Head.FriendlyName;
                diag.AddMessage($"Pushing {branch} to {options.RemoteName}");
                _logger?.Info($"Pushing '{_config.Id}':{branch} to {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitResult<bool>.Fail($"Remote '{options.RemoteName}' not found", null, diag);

                var refSpec = options.Force
                    ? $"+refs/heads/{branch}:refs/heads/{branch}"
                    : $"refs/heads/{branch}:refs/heads/{branch}";

                await Task.Run(() =>
                    _repo.Network.Push(remote, refSpec, (LibGit2Sharp.PushOptions?)null),
                    options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Push complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Push failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Push failed: {ex.Message}", ex, diag);
        }
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit from the current index.
    /// Optionally stages specific files before committing.
    /// </summary>
    /// <param name="options">Commit options including message and optional files to stage.</param>
    public async Task<GitResult<CommitInfo>> CommitAsync(GitCommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Message))
            return GitResult<CommitInfo>.Fail("Commit message must not be empty.");

        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                if (options.FilesToStage?.Count > 0)
                {
                    foreach (var file in options.FilesToStage)
                    {
                        Commands.Stage(_repo!, file);
                        diag.AddMessage($"Staged: {file}");
                    }
                }

                var sig = GetSignature(options.AuthorName, options.AuthorEmail);

                var commit = await Task.Run(() =>
                    _repo!.Commit(options.Message, sig, sig), options.CancellationToken);

                var info = ConvertCommit(commit);
                diag.CommitsProcessed = 1;
                diag.Complete();

                _logger?.Info($"Committed {info.ShortSha}: {info.ShortMessage}");
                return GitResult<CommitInfo>.Ok(info, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Commit failed: {ex.Message}", ex);
            return GitResult<CommitInfo>.Fail($"Commit failed: {ex.Message}", ex, diag);
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current working-tree status of the repository.
    /// </summary>
    public async Task<GitResult<GitRepoStatus>> GetStatusAsync()
    {
        var diag = new GitDiagnostics();
        try
        {
            await EnsureOpenAsync();

            var raw = await Task.Run(() => _repo!.RetrieveStatus());
            var status = new GitRepoStatus { IsDirty = raw.IsDirty };

            foreach (var item in raw)
            {
                var entry = new FileStatusEntry
                {
                    FilePath = item.FilePath,
                    Status = item.State.ToString(),
                    IsStaged = item.State.HasFlag(FileStatus.ModifiedInIndex) ||
                               item.State.HasFlag(FileStatus.NewInIndex)
                };

                if (item.State.HasFlag(FileStatus.Conflicted))
                    status.ConflictedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.Ignored))
                    status.IgnoredFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.NewInIndex))
                    status.StagedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.ModifiedInIndex))
                    status.StagedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.NewInWorkdir))
                    status.UntrackedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.ModifiedInWorkdir))
                    status.ModifiedFiles.Add(entry);
            }

            diag.FilesChanged = status.TotalChangedFiles;
            diag.Complete();
            return GitResult<GitRepoStatus>.Ok(status, diag);
        }
        catch (Exception ex)
        {
            diag.Complete();
            return GitResult<GitRepoStatus>.Fail($"Failed to get status: {ex.Message}", ex, diag);
        }
    }

    // ── Repository info ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns summary metadata about the repository.
    /// </summary>
    public async Task<GitResult<RepositoryInfo>> GetRepositoryInfoAsync()
    {
        try
        {
            await EnsureOpenAsync();
            var info = BuildRepositoryInfo();
            return GitResult<RepositoryInfo>.Ok(info);
        }
        catch (Exception ex)
        {
            return GitResult<RepositoryInfo>.Fail($"Failed to get repository info: {ex.Message}", ex);
        }
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all branches in the repository.
    /// </summary>
    /// <param name="includeRemote">When true, remote-tracking branches are included.</param>
    public async Task<GitResult<List<BranchInfo>>> ListBranchesAsync(bool includeRemote = true)
    {
        try
        {
            await EnsureOpenAsync();

            var branches = await Task.Run(() =>
            {
                var list = new List<BranchInfo>();
                foreach (var branch in _repo!.Branches)
                {
                    if (!includeRemote && branch.IsRemote) continue;
                    list.Add(new BranchInfo
                    {
                        Name = branch.CanonicalName,
                        FriendlyName = branch.FriendlyName,
                        IsCurrent = branch.IsCurrentRepositoryHead,
                        IsRemote = branch.IsRemote,
                        IsTracking = branch.IsTracking,
                        TrackedBranchName = branch.TrackedBranch?.FriendlyName,
                        TipSha = branch.Tip?.Sha ?? "",
                        AheadBy = branch.TrackingDetails.AheadBy,
                        BehindBy = branch.TrackingDetails.BehindBy
                    });
                }
                return list;
            });

            return GitResult<List<BranchInfo>>.Ok(branches);
        }
        catch (Exception ex)
        {
            return GitResult<List<BranchInfo>>.Fail($"Failed to list branches: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks out the named branch.
    /// </summary>
    /// <param name="branchName">Branch to check out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitResult<bool>> CheckoutBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() => Commands.Checkout(_repo!, branchName), cancellationToken);
                _logger?.Info($"Checked out branch '{branchName}'");
                return GitResult<bool>.Ok(true);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Checkout failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Checkout failed: {ex.Message}", ex);
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs a hard reset of the working tree and index to the tip of the named branch.
    /// Equivalent to <c>git reset --hard origin/&lt;branch&gt;</c>.
    /// </summary>
    /// <param name="branchName">
    /// Branch to reset to. If null, resets to the current branch's tracked upstream tip
    /// (or the configured <see cref="RepositoryConfiguration.DefaultBranch"/>'s remote-tracking
    /// branch when HEAD has no upstream).
    /// </param>
    /// <param name="remoteName">Remote name for resolving the upstream tip. Defaults to "origin".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitResult<bool>> ResetHardAsync(
        string? branchName = null,
        string remoteName = "origin",
        CancellationToken cancellationToken = default)
    {
        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var target = ResolveResetTarget(_repo!, branchName, remoteName);
                if (target is null)
                    return GitResult<bool>.Fail(
                        $"Could not resolve reset target (branch='{branchName ?? "<upstream>"}', remote='{remoteName}').",
                        null, diag);

                diag.AddMessage($"Hard reset to {target.Sha[..Math.Min(7, target.Sha.Length)]}");
                _logger?.Info($"Hard reset '{_config.Id}' → {target.Sha[..Math.Min(7, target.Sha.Length)]}");

                await Task.Run(() => _repo!.Reset(ResetMode.Hard, target), cancellationToken);

                diag.Complete();
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Reset failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Reset failed: {ex.Message}", ex, diag);
        }
    }

    // ── Ensure up to date ─────────────────────────────────────────────────────

    /// <summary>
    /// Convenience: clones the repository if it does not exist locally, otherwise fetches
    /// and hard-resets to the tip of the named branch on the remote. Idempotent.
    /// </summary>
    /// <param name="branchName">
    /// Branch to track. Null uses <see cref="RepositoryConfiguration.DefaultBranch"/>.
    /// </param>
    /// <param name="remoteName">Remote name. Defaults to "origin".</param>
    /// <param name="discardLocalChanges">
    /// When the working tree has uncommitted changes, this method hard-resets and would
    /// DISCARD them. To prevent accidental data loss it refuses to do so unless this is
    /// set to <c>true</c>. Defaults to <c>false</c> (fail instead of discarding).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitResult<RepositoryInfo>> EnsureUpToDateAsync(
        string? branchName = null,
        string remoteName = "origin",
        bool discardLocalChanges = false,
        CancellationToken cancellationToken = default)
    {
        branchName ??= string.IsNullOrWhiteSpace(_config.DefaultBranch) ? "main" : _config.DefaultBranch;

        var alreadyCloned =
            Directory.Exists(_resolvedPath) &&
            Directory.Exists(Path.Combine(_resolvedPath, ".git"));

        if (!alreadyCloned)
        {
            var cloneResult = await CloneAsync(new GitCloneOptions
            {
                BranchName = branchName,
                CancellationToken = cancellationToken
            });
            if (!cloneResult.IsSuccess)
                return cloneResult;
            return await GetRepositoryInfoAsync();
        }

        var fetch = await FetchAsync(new GitFetchOptions
        {
            RemoteName = remoteName,
            CancellationToken = cancellationToken
        });
        if (!fetch.IsSuccess)
            return GitResult<RepositoryInfo>.Fail(fetch.ErrorMessage!, fetch.Exception);

        // Guard against silent data loss: the hard reset below discards uncommitted work.
        if (!discardLocalChanges)
        {
            var status = await GetStatusAsync();
            if (status.IsSuccess && status.Value!.IsDirty)
                return GitResult<RepositoryInfo>.Fail(
                    "Working tree has uncommitted changes; refusing to hard-reset and lose them. " +
                    "Commit/stash first, or call EnsureUpToDateAsync with discardLocalChanges: true.",
                    null);
        }

        var reset = await ResetHardAsync(branchName, remoteName, cancellationToken);
        if (!reset.IsSuccess)
            return GitResult<RepositoryInfo>.Fail(reset.ErrorMessage!, reset.Exception);

        return await GetRepositoryInfoAsync();
    }

    /// <summary>
    /// Retrieves the commit log for the current branch.
    /// </summary>
    /// <param name="maxCount">Maximum number of commits to return. 0 returns all.</param>
    public async Task<GitResult<List<CommitInfo>>> GetCommitLogAsync(int maxCount = 20)
    {
        try
        {
            await EnsureOpenAsync();

            var commits = await Task.Run(() =>
            {
                var filter = new CommitFilter
                {
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                };
                var commits = _repo!.Commits.QueryBy(filter).AsEnumerable();
                if (maxCount > 0) commits = commits.Take(maxCount);
                return commits.Select(ConvertCommit).ToList();
            });

            return GitResult<List<CommitInfo>>.Ok(commits);
        }
        catch (Exception ex)
        {
            return GitResult<List<CommitInfo>>.Fail($"Failed to get commit log: {ex.Message}", ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a LibGit2Sharp credentials callback for HTTPS authentication from the
    /// repository configuration. Returns null when no credentials are configured (anonymous).
    /// </summary>
    /// <remarks>
    /// When only <see cref="RepositoryConfiguration.Password"/> is set, the username is sent
    /// as <c>x-access-token</c> — the canonical placeholder GitHub accepts for PAT-only auth.
    /// When both are set, both are forwarded verbatim. SSH key fields on the configuration
    /// are preserved for future use but are not currently wired (the LibGit2Sharp 0.30 managed
    /// binary ships without the native SSH transport).
    /// </remarks>
    private CredentialsHandler? BuildCredentialsHandler()
    {
        if (string.IsNullOrWhiteSpace(_config.Password)) return null;

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = string.IsNullOrWhiteSpace(_config.Username) ? "x-access-token" : _config.Username!,
            Password = _config.Password ?? ""
        };
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

    private async Task EnsureOpenAsync()
    {
        if (_repo is not null) return;

        if (!Directory.Exists(_resolvedPath) ||
            !Directory.Exists(Path.Combine(_resolvedPath, ".git")))
            throw new InvalidOperationException(
                $"Repository not found at '{_resolvedPath}'. Clone it first.");

        await Task.Run(() => _repo = new Repository(_resolvedPath));
    }

    private Signature GetSignature(string? name = null, string? email = null)
    {
        name ??= _repo?.Config.Get<string>("user.name")?.Value ?? "CodeLogic";
        email ??= _repo?.Config.Get<string>("user.email")?.Value ?? "codelogic@localhost";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    private RepositoryInfo BuildRepositoryInfo()
    {
        var status = _repo!.RetrieveStatus();
        return new RepositoryInfo
        {
            Id = _config.Id,
            Name = _config.Name,
            LocalPath = _resolvedPath,
            RemoteUrl = _repo!.Network.Remotes.FirstOrDefault()?.Url ?? "",
            CurrentBranch = _repo.Head.FriendlyName,
            HeadCommitSha = _repo.Head.Tip?.Sha ?? "",
            IsBare = _repo.Info.IsBare,
            State = _repo.Info.CurrentOperation.ToString(),
            LocalBranchCount = _repo.Branches.Count(b => !b.IsRemote),
            RemoteBranchCount = _repo.Branches.Count(b => b.IsRemote),
            TagCount = _repo.Tags.Count(),
            StashCount = _repo.Stashes.Count(),
            IsDirty = status.IsDirty,
            ModifiedFiles = status.Modified.Count(),
            StagedFiles = status.Staged.Count(),
            UntrackedFiles = status.Untracked.Count()
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

    /// <summary>
    /// Disposes this instance once the git operation currently running on it (if any) has
    /// finished. Used when a config change replaces the repository.
    /// </summary>
    internal async Task DisposeWhenIdleAsync()
    {
        if (_disposed) return;
        try { await _lock.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _repo?.Dispose();
        _lock.Dispose();
        _disposed = true;
    }
}
