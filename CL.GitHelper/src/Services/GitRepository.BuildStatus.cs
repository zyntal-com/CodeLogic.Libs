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

    private static GitRepoStatus BuildStatus(LibGit2Sharp.RepositoryStatus raw)
    {
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

    // ── Repository info ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns summary metadata about the repository.
    /// </summary>
    /// <returns>
    /// The repository info, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/> (not cloned yet),
    /// <see cref="GitErrors.TimeoutCode"/>, <see cref="GitErrors.RepositoryDisposedCode"/> or
    /// <see cref="GitErrors.InfoFailedCode"/>.
    /// </returns>
    public Task<Result<RepositoryInfo>> GetRepositoryInfoAsync() =>
        ReadAsync("Repository info", GitErrors.InfoFailedCode, BuildRepositoryInfo);

    // ── Branches ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all branches in the repository.
    /// </summary>
    /// <param name="includeRemote">When true, remote-tracking branches are included.</param>
    /// <returns>
    /// The branches, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>, <see cref="GitErrors.TimeoutCode"/>,
    /// <see cref="GitErrors.RepositoryDisposedCode"/> or <see cref="GitErrors.ListBranchesFailedCode"/>.
    /// </returns>
    public Task<Result<List<BranchInfo>>> ListBranchesAsync(bool includeRemote = true) =>
        ReadAsync("Listing branches", GitErrors.ListBranchesFailedCode, repo =>
        {
            var list = new List<BranchInfo>();
            foreach (var branch in repo.Branches)
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
            if (await EnterAsync(cancellationToken).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                await Task.Run(() => Commands.Checkout(repo, branchName), cancellationToken).ConfigureAwait(false);
                _logger?.Info($"Checked out branch '{Redact(branchName)}'");
                return Result.Success();
            }
            finally
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            return Fail("Checkout", ex, GitErrors.CheckoutFailedCode, notFoundCode: GitErrors.BranchNotFoundCode);
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
            if (await EnterAsync(cancellationToken).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                var target = ResolveResetTarget(repo, branchName, remoteName);
                if (target is null)
                    return Error.NotFound(GitErrors.BranchNotFoundCode,
                        Redact($"Could not resolve reset target (branch='{branchName ?? "<upstream>"}', remote='{remoteName}')."));

                _logger?.Info($"Hard reset '{_config.Id}' → {target.Sha[..Math.Min(7, target.Sha.Length)]}");

                await Task.Run(() => repo.Reset(ResetMode.Hard, target), cancellationToken).ConfigureAwait(false);
                return Result.Success();
            }
            finally
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            return Fail("Reset", ex, GitErrors.ResetFailedCode);
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
            }).ConfigureAwait(false);
            if (cloneResult.IsFailure)
                return cloneResult;
            return await GetRepositoryInfoAsync().ConfigureAwait(false);
        }

        var fetch = await FetchAsync(new GitFetchOptions
        {
            RemoteName = remoteName,
            CancellationToken = cancellationToken
        }).ConfigureAwait(false);
        if (fetch.IsFailure)
            return fetch.Error!;

        var info = await GetRepositoryInfoAsync().ConfigureAwait(false);
        if (info.IsFailure)
            return info;

        // A bare repository has no working tree to reset: the fetch is the whole update.
        if (info.Value!.IsBare)
            return info;

        // Guard against silent data loss: the hard reset below discards uncommitted work.
        if (!discardLocalChanges && info.Value.IsDirty)
            return Error.Conflict(GitErrors.UncommittedChangesCode,
                "Working tree has uncommitted changes; refusing to hard-reset and lose them. " +
                "Commit/stash first, or call EnsureUpToDateAsync with discardLocalChanges: true.");

        var reset = await ResetHardAsync(branchName, remoteName, cancellationToken).ConfigureAwait(false);
        if (reset.IsFailure)
            return reset.Error!;

        return await GetRepositoryInfoAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the commit log for the current branch.
    /// </summary>
    /// <param name="maxCount">Maximum number of commits to return. 0 returns all.</param>
    /// <returns>
    /// The commits, newest first, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>,
    /// <see cref="GitErrors.TimeoutCode"/>, <see cref="GitErrors.RepositoryDisposedCode"/> or <see cref="GitErrors.LogFailedCode"/>.
    /// </returns>
    public Task<Result<List<CommitInfo>>> GetCommitLogAsync(int maxCount = 20) =>
        ReadAsync("Commit log", GitErrors.LogFailedCode, repo =>
        {
            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
            };
            var commits = repo.Commits.QueryBy(filter).AsEnumerable();
            if (maxCount > 0) commits = commits.Take(maxCount);
            return commits.Select(ConvertCommit).ToList();
        });
}
