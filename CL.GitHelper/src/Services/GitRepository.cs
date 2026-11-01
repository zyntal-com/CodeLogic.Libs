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
    private readonly int _timeoutSeconds;

    /// <summary>Initialises the repository service.</summary>
    /// <param name="config">Repository configuration entry.</param>
    /// <param name="resolvedPath">Absolute local path (pre-resolved by <see cref="GitManager"/>).</param>
    /// <param name="logger">Optional scoped logger.</param>
    /// <param name="defaultTimeoutSeconds">
    /// Timeout for network operations when <see cref="RepositoryConfiguration.TimeoutSeconds"/> is 0
    /// (<see cref="GitHelperConfig.DefaultTimeoutSeconds"/>).
    /// </param>
    internal GitRepository(RepositoryConfiguration config, string resolvedPath, ILogger? logger = null,
        int defaultTimeoutSeconds = 300)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _resolvedPath = resolvedPath;
        _logger = logger;
        _timeoutSeconds = Math.Max(1, config.TimeoutSeconds > 0 ? config.TimeoutSeconds : defaultTimeoutSeconds);

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
    /// <returns>
    /// The cloned repository's info, or an error: <see cref="GitErrors.SshNotSupportedCode"/>,
    /// <see cref="GitErrors.DirectoryNotEmptyCode"/>, <see cref="GitErrors.AuthenticationFailedCode"/>,
    /// <see cref="GitErrors.CancelledCode"/>, <see cref="GitErrors.TimeoutCode"/> or
    /// <see cref="GitErrors.CloneFailedCode"/>.
    /// </returns>
    public async Task<Result<RepositoryInfo>> CloneAsync(GitCloneOptions? options = null)
    {
        options ??= new GitCloneOptions();
        var diag = new GitDiagnostics();
        diag.AddMessage("Starting clone");
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds);
        var createdDirectory = false;

        try
        {
            if (IsSshUrl(_config.RepositoryUrl))
                return Error.Validation(GitErrors.SshNotSupportedCode,
                    "SSH URLs are not supported — LibGit2Sharp 0.30 has no SSH transport. " +
                    "Use an HTTPS URL with a personal access token.");

            if (Directory.Exists(_resolvedPath))
            {
                if (Directory.GetFileSystemEntries(_resolvedPath).Length > 0)
                    return Error.Conflict(GitErrors.DirectoryNotEmptyCode,
                        $"Directory '{_resolvedPath}' already exists and is not empty.");
            }
            else
            {
                Directory.CreateDirectory(_resolvedPath);
                createdDirectory = true;
                diag.AddMessage($"Created directory: {_resolvedPath}");
            }

            _logger?.Info($"Cloning {Redact(_config.RepositoryUrl)} → {_resolvedPath}");

            var libOptions = new LibGit2Sharp.CloneOptions
            {
                BranchName = options.BranchName,
                RecurseSubmodules = options.RecurseSubmodules,
                IsBare = options.Bare,
                OnCheckoutProgress = (_, completed, total) => Report(options.OnProgress, new GitProgress
                {
                    Stage = "Checking out files",
                    ProgressPercentage = Percent(completed, total),
                    Message = $"{completed}/{total}"
                })
            };
            ApplyFetchCallbacks(libOptions.FetchOptions, transfer, options.OnProgress, diag);

            await Task.Run(() =>
                Repository.Clone(_config.RepositoryUrl, _resolvedPath, libOptions),
                transfer.Token);

            diag.Complete();
            _logger?.Info($"Clone complete in {diag.Duration?.TotalSeconds:F2}s");
            _logger?.Debug($"Clone diagnostics: {string.Join("; ", diag.Messages)}");

            _repo = new Repository(_resolvedPath);
            return BuildRepositoryInfo();
        }
        catch (Exception ex)
        {
            diag.Complete();
            // Leave no half-written clone behind: it would make every later clone fail with directory_not_empty.
            if (createdDirectory) TryDeleteDirectory(_resolvedPath);
            return Fail("Clone", ex, GitErrors.CloneFailedCode, transfer);
        }
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches changes from the configured remote without merging.
    /// </summary>
    /// <param name="options">Optional fetch settings.</param>
    /// <returns>
    /// The transfer diagnostics (timing and messages) on success, or an error:
    /// <see cref="GitErrors.RepositoryNotFoundCode"/>, <see cref="GitErrors.RemoteNotFoundCode"/>,
    /// <see cref="GitErrors.AuthenticationFailedCode"/>, <see cref="GitErrors.CancelledCode"/>,
    /// <see cref="GitErrors.TimeoutCode"/> or <see cref="GitErrors.FetchFailedCode"/>.
    /// </returns>
    public async Task<Result<GitDiagnostics>> FetchAsync(GitFetchOptions? options = null)
    {
        options ??= new GitFetchOptions();
        var diag = new GitDiagnostics();
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds);

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(transfer.Token);
            try
            {
                diag.AddMessage($"Fetching from {options.RemoteName}");
                _logger?.Info($"Fetching '{_config.Id}' from {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitErrors.RemoteNotFound(options.RemoteName);

                var refSpecs = remote.FetchRefSpecs.Select(s => s.Specification).ToList();

                var fetchOpts = new LibGit2Sharp.FetchOptions
                {
                    Prune = options.Prune,
                    TagFetchMode = options.FetchTags ? TagFetchMode.Auto : TagFetchMode.None
                };
                ApplyFetchCallbacks(fetchOpts, transfer, options.OnProgress, diag);

                await Task.Run(() =>
                    Commands.Fetch(_repo, options.RemoteName, refSpecs, fetchOpts, null),
                    transfer.Token);

                diag.Complete();
                _logger?.Info($"Fetch complete in {diag.Duration?.TotalSeconds:F2}s");
                return diag;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            return Fail("Fetch", ex, GitErrors.FetchFailedCode, transfer);
        }
    }

    // ── Pull ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches from <see cref="GitPullOptions.RemoteName"/> and integrates the current branch's
    /// upstream with <see cref="GitPullOptions.Strategy"/>.
    /// </summary>
    /// <param name="options">Optional pull settings.</param>
    /// <returns>
    /// The diagnostics of the pull (including the fetch step's messages) on success, or an error:
    /// any error <see cref="FetchAsync"/> returns, <see cref="GitErrors.BranchNotFoundCode"/> (no
    /// upstream branch on the remote), <see cref="GitErrors.NonFastForwardCode"/>
    /// (<see cref="MergeStrategy.FastForwardOnly"/> and the branches diverged),
    /// <see cref="GitErrors.MergeConflictCode"/>, <see cref="GitErrors.CheckoutConflictCode"/>,
    /// <see cref="GitErrors.CancelledCode"/> or <see cref="GitErrors.PullFailedCode"/>.
    /// </returns>
    /// <remarks>
    /// A merge that stops on conflicts leaves the conflicted files in the working tree, as
    /// <c>git pull</c> does; <see cref="ResetHardAsync"/> discards them. A rebase that stops on
    /// conflicts is aborted, so the branch is left as it was before the pull.
    /// </remarks>
    public async Task<Result<GitDiagnostics>> PullAsync(GitPullOptions? options = null)
    {
        options ??= new GitPullOptions();
        var diag = new GitDiagnostics();
        var remoteName = ResolvePullRemote(options);

        try
        {
            var fetch = options.FetchOptions ?? new GitFetchOptions();
            var fetchResult = await FetchAsync(new GitFetchOptions
            {
                RemoteName = remoteName,
                Prune = fetch.Prune,
                FetchTags = fetch.FetchTags,
                OnProgress = fetch.OnProgress,
                CancellationToken = fetch.CancellationToken.CanBeCanceled
                    ? fetch.CancellationToken
                    : options.CancellationToken
            });
            if (fetchResult.IsFailure)
                return fetchResult;
            diag.Messages.AddRange(fetchResult.Value!.Messages);
            diag.ObjectCount = fetchResult.Value.ObjectCount;
            diag.BytesTransferred = fetchResult.Value.BytesTransferred;

            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                var repo = _repo!;
                var upstream = ResolvePullUpstream(repo, remoteName);
                if (upstream?.Tip is null)
                    return Error.NotFound(GitErrors.BranchNotFoundCode,
                        $"Branch '{repo.Head.FriendlyName}' has no upstream branch on remote '{remoteName}'.");

                diag.AddMessage($"Integrating {upstream.FriendlyName} ({options.Strategy})");
                _logger?.Info($"Pulling '{_config.Id}' from {upstream.FriendlyName} ({options.Strategy})");

                var sig = GetSignature();
                var conflict = await Task.Run(() => Integrate(repo, upstream, sig, options.Strategy, diag),
                    options.CancellationToken);
                if (conflict is not null)
                    return conflict;

                diag.Complete();
                _logger?.Info($"Pull complete in {diag.Duration?.TotalSeconds:F2}s");
                return diag;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            return Fail("Pull", ex, GitErrors.PullFailedCode);
        }
    }

    /// <summary>
    /// The remote a pull uses: <see cref="GitPullOptions.RemoteName"/>, or
    /// <see cref="GitPullOptions.FetchOptions"/>' remote when <c>RemoteName</c> is left at "origin".
    /// </summary>
    internal static string ResolvePullRemote(GitPullOptions options)
    {
        const string defaultRemote = "origin";
        var fetchRemote = options.FetchOptions?.RemoteName;
        if (string.Equals(options.RemoteName, defaultRemote, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(fetchRemote))
            return fetchRemote;
        return string.IsNullOrWhiteSpace(options.RemoteName) ? defaultRemote : options.RemoteName;
    }

    private static Branch? ResolvePullUpstream(Repository repo, string remoteName)
    {
        var head = repo.Head;
        if (head.IsTracking && string.Equals(head.TrackedBranch?.RemoteName, remoteName, StringComparison.Ordinal))
            return head.TrackedBranch;
        return repo.Branches[$"{remoteName}/{head.FriendlyName}"];
    }

    /// <summary>Merges or rebases onto <paramref name="upstream"/>. Returns an error on conflicts, null on success.</summary>
    private static Error? Integrate(Repository repo, Branch upstream, Signature sig, MergeStrategy strategy, GitDiagnostics diag)
    {
        if (strategy == MergeStrategy.Rebase)
        {
            var result = repo.Rebase.Start(repo.Head, upstream, null, new Identity(sig.Name, sig.Email), new RebaseOptions());
            diag.AddMetric("RebaseStatus", result.Status.ToString());
            diag.CommitsProcessed = (int)result.CompletedStepCount;
            if (result.Status == RebaseStatus.Complete)
                return null;

            repo.Rebase.Abort();
            return Error.Conflict(GitErrors.MergeConflictCode,
                $"Rebase onto {upstream.FriendlyName} stopped on conflicting changes and was aborted.");
        }

        var merge = repo.Merge(upstream, sig, new MergeOptions
        {
            FastForwardStrategy = strategy == MergeStrategy.FastForwardOnly
                ? FastForwardStrategy.FastForwardOnly
                : FastForwardStrategy.Default
        });
        diag.AddMetric("MergeStatus", merge.Status.ToString());
        return merge.Status == MergeStatus.Conflicts
            ? Error.Conflict(GitErrors.MergeConflictCode,
                $"Merging {upstream.FriendlyName} left conflicting files in the working tree.")
            : null;
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes local commits to the remote, with the configured credentials.
    /// </summary>
    /// <param name="options">Optional push settings.</param>
    /// <returns>
    /// The transfer diagnostics on success, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>,
    /// <see cref="GitErrors.RemoteNotFoundCode"/>, <see cref="GitErrors.AuthenticationFailedCode"/>,
    /// <see cref="GitErrors.NonFastForwardCode"/>, <see cref="GitErrors.PushRejectedCode"/>,
    /// <see cref="GitErrors.CancelledCode"/>, <see cref="GitErrors.TimeoutCode"/> or
    /// <see cref="GitErrors.PushFailedCode"/>.
    /// </returns>
    public async Task<Result<GitDiagnostics>> PushAsync(GitPushOptions? options = null)
    {
        options ??= new GitPushOptions();
        var diag = new GitDiagnostics();
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds);

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(transfer.Token);
            try
            {
                var branch = options.BranchName ?? _repo!.Head.FriendlyName;
                diag.AddMessage($"Pushing {branch} to {options.RemoteName}");
                _logger?.Info($"Pushing '{_config.Id}':{branch} to {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitErrors.RemoteNotFound(options.RemoteName);

                var refSpecs = BuildPushRefSpecs(branch, options.Force,
                    options.PushTags ? _repo.Tags.Select(t => t.CanonicalName) : []);

                var rejected = new List<string>();
                var pushOpts = new LibGit2Sharp.PushOptions
                {
                    CredentialsProvider = BuildCredentialsHandler(),
                    OnPushStatusError = error => rejected.Add($"{error.Reference}: {error.Message}"),
                    OnPushTransferProgress = (current, total, bytes) =>
                    {
                        diag.ObjectCount = current;
                        diag.BytesTransferred = bytes;
                        Report(options.OnProgress, new GitProgress
                        {
                            Stage = "Writing objects",
                            ProgressPercentage = Percent(current, total),
                            ObjectsReceived = current,
                            TotalObjects = total,
                            BytesReceived = bytes
                        });
                        return transfer.Continue;
                    },
                    OnPackBuilderProgress = (stage, current, total) =>
                    {
                        Report(options.OnProgress, new GitProgress
                        {
                            Stage = stage == PackBuilderStage.Counting ? "Counting objects" : "Compressing objects",
                            ProgressPercentage = Percent(current, total),
                            ObjectsReceived = current,
                            TotalObjects = total
                        });
                        return transfer.Continue;
                    }
                };

                await Task.Run(() => _repo.Network.Push(remote, refSpecs, pushOpts), transfer.Token);

                if (rejected.Count > 0)
                    return Error.Conflict(GitErrors.PushRejectedCode,
                        $"The remote rejected the push: {Redact(string.Join("; ", rejected))}");

                diag.Complete();
                _logger?.Info($"Push complete in {diag.Duration?.TotalSeconds:F2}s");
                return diag;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            return Fail("Push", ex, GitErrors.PushFailedCode, transfer);
        }
    }

    /// <summary>
    /// Refspecs for a push: the branch (forced with a leading <c>+</c> when <paramref name="force"/>)
    /// and each tag. Tags are never forced, so a push cannot move a tag that exists on the remote.
    /// </summary>
    internal static List<string> BuildPushRefSpecs(string branch, bool force, IEnumerable<string> tagRefs)
    {
        var branchRef = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";
        List<string> specs = [$"{(force ? "+" : "")}{branchRef}:{branchRef}"];
        specs.AddRange(tagRefs.Select(tag => $"{tag}:{tag}"));
        return specs;
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit from the current index.
    /// Optionally stages specific files before committing.
    /// </summary>
    /// <param name="options">Commit options including message and optional files to stage.</param>
    /// <returns>
    /// The new commit, or an error: <see cref="GitErrors.EmptyCommitMessageCode"/>,
    /// <see cref="GitErrors.NothingToCommitCode"/>, <see cref="GitErrors.RepositoryNotFoundCode"/>,
    /// <see cref="GitErrors.NotFoundCode"/> (a file to stage does not exist),
    /// <see cref="GitErrors.CancelledCode"/> or <see cref="GitErrors.CommitFailedCode"/>.
    /// </returns>
    public async Task<Result<CommitInfo>> CommitAsync(GitCommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Message))
            return Error.Validation(GitErrors.EmptyCommitMessageCode, "Commit message must not be empty.");

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
                        _logger?.Debug($"Staged: {file}");
                    }
                }

                var sig = GetSignature(options.AuthorName, options.AuthorEmail);

                var commit = await Task.Run(() =>
                    _repo!.Commit(options.Message, sig, sig), options.CancellationToken);

                var info = ConvertCommit(commit);
                _logger?.Info($"Committed {info.ShortSha}: {info.ShortMessage}");
                return info;
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Commit failed: {ex.Message}", ex);
            return GitErrors.FromException(ex, $"Commit failed: {ex.Message}", GitErrors.CommitFailedCode);
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current working-tree status of the repository.
    /// </summary>
    /// <returns>
    /// The status, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> or <see cref="GitErrors.StatusFailedCode"/>.
    /// </returns>
    public async Task<Result<GitRepoStatus>> GetStatusAsync()
    {
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

            return status;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"Status of '{_config.Id}' failed: {ex}");
            return GitErrors.FromException(ex, $"Failed to get status: {ex.Message}", GitErrors.StatusFailedCode);
        }
    }

    // ── Repository info ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns summary metadata about the repository.
    /// </summary>
    /// <returns>
    /// The repository info, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> (not cloned yet)
    /// or <see cref="GitErrors.InfoFailedCode"/>.
    /// </returns>
    public async Task<Result<RepositoryInfo>> GetRepositoryInfoAsync()
    {
        try
        {
            await EnsureOpenAsync();
            return BuildRepositoryInfo();
        }
        catch (Exception ex)
        {
            _logger?.Debug($"Repository info of '{_config.Id}' failed: {ex}");
            return GitErrors.FromException(ex, $"Failed to get repository info: {ex.Message}", GitErrors.InfoFailedCode);
        }
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all branches in the repository.
    /// </summary>
    /// <param name="includeRemote">When true, remote-tracking branches are included.</param>
    /// <returns>
    /// The branches, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> or <see cref="GitErrors.ListBranchesFailedCode"/>.
    /// </returns>
    public async Task<Result<List<BranchInfo>>> ListBranchesAsync(bool includeRemote = true)
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

            return branches;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"Listing branches of '{_config.Id}' failed: {ex}");
            return GitErrors.FromException(ex, $"Failed to list branches: {ex.Message}", GitErrors.ListBranchesFailedCode);
        }
    }

    /// <summary>
    /// Checks out the named branch.
    /// </summary>
    /// <param name="branchName">Branch to check out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>, <see cref="GitErrors.BranchNotFoundCode"/>,
    /// <see cref="GitErrors.CheckoutConflictCode"/>, <see cref="GitErrors.CancelledCode"/> or
    /// <see cref="GitErrors.CheckoutFailedCode"/>.
    /// </returns>
    public async Task<Result> CheckoutBranchAsync(
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
                return Result.Success();
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Checkout failed: {ex.Message}", ex);
            return GitErrors.FromException(ex, $"Checkout failed: {ex.Message}",
                GitErrors.CheckoutFailedCode, GitErrors.BranchNotFoundCode);
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
    /// <returns>
    /// Success, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>, <see cref="GitErrors.BranchNotFoundCode"/>
    /// (reset target not resolvable), <see cref="GitErrors.CancelledCode"/> or <see cref="GitErrors.ResetFailedCode"/>.
    /// </returns>
    public async Task<Result> ResetHardAsync(
        string? branchName = null,
        string remoteName = "origin",
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(cancellationToken);
            try
            {
                var target = ResolveResetTarget(_repo!, branchName, remoteName);
                if (target is null)
                    return Error.NotFound(GitErrors.BranchNotFoundCode,
                        $"Could not resolve reset target (branch='{branchName ?? "<upstream>"}', remote='{remoteName}').");

                _logger?.Info($"Hard reset '{_config.Id}' → {target.Sha[..Math.Min(7, target.Sha.Length)]}");

                await Task.Run(() => _repo!.Reset(ResetMode.Hard, target), cancellationToken);
                return Result.Success();
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Reset failed: {ex.Message}", ex);
            return GitErrors.FromException(ex, $"Reset failed: {ex.Message}", GitErrors.ResetFailedCode);
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
    /// <returns>
    /// The up-to-date repository info, or the error of the failing step (clone, fetch or reset), or
    /// <see cref="GitErrors.UncommittedChangesCode"/> when the working tree is dirty and
    /// <paramref name="discardLocalChanges"/> is false.
    /// </returns>
    public async Task<Result<RepositoryInfo>> EnsureUpToDateAsync(
        string? branchName = null,
        string remoteName = "origin",
        bool discardLocalChanges = false,
        CancellationToken cancellationToken = default)
    {
        branchName ??= string.IsNullOrWhiteSpace(_config.DefaultBranch) ? "main" : _config.DefaultBranch;

        if (!IsCloned(_resolvedPath))
        {
            var cloneResult = await CloneAsync(new GitCloneOptions
            {
                BranchName = branchName,
                CancellationToken = cancellationToken
            });
            if (cloneResult.IsFailure)
                return cloneResult;
            return await GetRepositoryInfoAsync();
        }

        var fetch = await FetchAsync(new GitFetchOptions
        {
            RemoteName = remoteName,
            CancellationToken = cancellationToken
        });
        if (fetch.IsFailure)
            return fetch.Error!;

        // A bare repository has no working tree to reset: the fetch is the whole update.
        if (_repo?.Info.IsBare == true)
            return await GetRepositoryInfoAsync();

        // Guard against silent data loss: the hard reset below discards uncommitted work.
        if (!discardLocalChanges)
        {
            var status = await GetStatusAsync();
            if (status.IsSuccess && status.Value!.IsDirty)
                return Error.Conflict(GitErrors.UncommittedChangesCode,
                    "Working tree has uncommitted changes; refusing to hard-reset and lose them. " +
                    "Commit/stash first, or call EnsureUpToDateAsync with discardLocalChanges: true.");
        }

        var reset = await ResetHardAsync(branchName, remoteName, cancellationToken);
        if (reset.IsFailure)
            return reset.Error!;

        return await GetRepositoryInfoAsync();
    }

    /// <summary>
    /// Retrieves the commit log for the current branch.
    /// </summary>
    /// <param name="maxCount">Maximum number of commits to return. 0 returns all.</param>
    /// <returns>
    /// The commits, newest first, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> or <see cref="GitErrors.LogFailedCode"/>.
    /// </returns>
    public async Task<Result<List<CommitInfo>>> GetCommitLogAsync(int maxCount = 20)
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

            return commits;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"Commit log of '{_config.Id}' failed: {ex}");
            return GitErrors.FromException(ex, $"Failed to get commit log: {ex.Message}", GitErrors.LogFailedCode);
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
    private CredentialsHandler? BuildCredentialsHandler() => CreateCredentialsHandler(_config, _logger);

    /// <summary>
    /// Credentials callback that hands out the configured user name and token only to the
    /// configured repository's scheme, host and port (<see cref="GitSecurity.CredentialsAllowedFor"/>).
    /// Any other host — a submodule elsewhere, a redirect, a second remote — gets empty credentials,
    /// so the request fails with <see cref="GitErrors.AuthenticationFailedCode"/> instead of leaking the token.
    /// Returns null when no token is configured (anonymous access).
    /// </summary>
    internal static CredentialsHandler? CreateCredentialsHandler(RepositoryConfiguration config, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(config.Password)) return null;

        var configuredUrl = config.RepositoryUrl;
        var username = string.IsNullOrWhiteSpace(config.Username) ? "x-access-token" : config.Username!;
        var password = config.Password;

        // Never throw here: the callback runs inside a native libgit2 call.
        return (url, _, _) =>
        {
            if (GitSecurity.CredentialsAllowedFor(configuredUrl, url))
                return new UsernamePasswordCredentials { Username = username, Password = password };

            logger?.Warning(
                $"[GitHelper] Repository '{config.Id}': {GitSecurity.Redact(url, password)} asked for credentials, " +
                "but they are configured for another host; none were sent.");
            return new UsernamePasswordCredentials { Username = "", Password = "" };
        };
    }

    /// <summary>
    /// True when <paramref name="path"/> holds a git repository: a working tree with <c>.git</c>
    /// (directory or worktree file) or a bare repository.
    /// </summary>
    internal static bool IsCloned(string path) => Directory.Exists(path) && Repository.IsValid(path);

    /// <summary>Wires credentials, progress reporting and cancellation into a fetch or clone.</summary>
    private void ApplyFetchCallbacks(FetchOptionsBase target, Transfer transfer,
        Action<GitProgress>? onProgress, GitDiagnostics diag)
    {
        target.CredentialsProvider = BuildCredentialsHandler();
        target.OnTransferProgress = progress =>
        {
            diag.ObjectCount = progress.ReceivedObjects;
            diag.BytesTransferred = progress.ReceivedBytes;
            var receiving = progress.ReceivedObjects < progress.TotalObjects;
            Report(onProgress, new GitProgress
            {
                Stage = receiving ? "Receiving objects" : "Indexing objects",
                ProgressPercentage = Percent(receiving ? progress.ReceivedObjects : progress.IndexedObjects, progress.TotalObjects),
                ObjectsReceived = progress.ReceivedObjects,
                TotalObjects = progress.TotalObjects,
                BytesReceived = progress.ReceivedBytes
            });
            // Returning false makes libgit2 abort the transfer (UserCancelledException).
            return transfer.Continue;
        };
        target.OnProgress = serverText =>
        {
            Report(onProgress, new GitProgress { Stage = "Remote", Message = serverText.TrimEnd() });
            return transfer.Continue;
        };
    }

    private void Report(Action<GitProgress>? onProgress, GitProgress progress)
    {
        if (onProgress is null) return;
        // The callback runs inside a native libgit2 call: an exception must not escape.
        try { onProgress(progress); }
        catch (Exception ex) { _logger?.Debug($"[GitHelper] OnProgress callback of '{_config.Id}' threw: {ex.Message}"); }
    }

    private static int Percent(long done, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);

    private string Redact(string? text) => GitSecurity.Redact(text, _config.Password);

    /// <summary>
    /// Logs a failed operation and maps the exception to an error. Credentials in URLs and the
    /// configured token are redacted from both. A transfer stopped by the timeout becomes
    /// <see cref="GitErrors.TimeoutCode"/>.
    /// </summary>
    private Error Fail(string operation, Exception ex, string fallbackCode, Transfer? transfer = null)
    {
        var message = Redact(ex.Message);
        if (transfer?.TimedOut == true)
        {
            _logger?.Error($"{operation} of '{_config.Id}' timed out after {transfer.TimeoutSeconds}s");
            return Error.Timeout(GitErrors.TimeoutCode,
                $"{operation} timed out after {transfer.TimeoutSeconds}s.", ex.GetType().Name);
        }

        _logger?.Error($"{operation} failed: {message}");
        _logger?.Debug($"{operation} failure detail: {Redact(ex.ToString())}");
        return GitErrors.FromException(ex, $"{operation} failed: {message}", fallbackCode);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            // libgit2 writes read-only pack files; clear the flag so the delete succeeds on Windows.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Cancellation for one network operation: the caller's token plus the repository timeout.
    /// libgit2 checks it in every progress callback, so a running transfer stops at the next one.
    /// </summary>
    private sealed class Transfer : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _caller;

        public Transfer(CancellationToken caller, int timeoutSeconds)
        {
            _caller = caller;
            TimeoutSeconds = timeoutSeconds;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        public int TimeoutSeconds { get; }
        public CancellationToken Token => _cts.Token;
        public bool Continue => !_cts.IsCancellationRequested;
        public bool TimedOut => _cts.IsCancellationRequested && !_caller.IsCancellationRequested;
        public void Dispose() => _cts.Dispose();
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

        if (!IsCloned(_resolvedPath))
            throw new RepositoryNotFoundException(
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
        var repo = _repo!;
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

    internal bool IsDisposed => _disposed;

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
