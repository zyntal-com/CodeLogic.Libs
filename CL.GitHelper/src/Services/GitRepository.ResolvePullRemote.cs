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
    /// <see cref="GitErrors.CancelledCode"/>, <see cref="GitErrors.TimeoutCode"/>,
    /// <see cref="GitErrors.RepositoryDisposedCode"/> or <see cref="GitErrors.PushFailedCode"/>.
    /// </returns>
    public async Task<Result<GitDiagnostics>> PushAsync(GitPushOptions? options = null)
    {
        options ??= new GitPushOptions();
        var diag = new GitDiagnostics();
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds, _stallGrace, () => MarkStalled("Push"));

        try
        {
            if (await EnterAsync(transfer.Token).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                var branch = options.BranchName ?? repo.Head.FriendlyName;
                diag.AddMessage($"Pushing {branch} to {options.RemoteName}");
                _logger?.Info($"Pushing '{_config.Id}':{branch} to {options.RemoteName}");

                var remote = repo.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitErrors.RemoteNotFound(options.RemoteName);

                var refSpecs = BuildPushRefSpecs(branch, options.Force,
                    options.PushTags ? repo.Tags.Select(t => t.CanonicalName) : []);

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

                transfer.Begin();
                await Task.Run(() => repo.Network.Push(remote, refSpecs, pushOpts), transfer.Token).ConfigureAwait(false);

                if (rejected.Count > 0)
                    return Error.Conflict(GitErrors.PushRejectedCode,
                        $"The remote rejected the push: {Redact(string.Join("; ", rejected))}");

                diag.Complete();
                _logger?.Info($"Push complete in {diag.Duration?.TotalSeconds:F2}s");
                return diag;
            }
            finally
            {
                Leave(transfer);
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
            if (await EnterAsync(options.CancellationToken).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                if (options.FilesToStage?.Count > 0)
                {
                    foreach (var file in options.FilesToStage)
                    {
                        Commands.Stage(repo, file);
                        _logger?.Debug($"Staged: {file}");
                    }
                }

                var sig = GetSignature(repo, options.AuthorName, options.AuthorEmail);

                var commit = await Task.Run(() =>
                    repo.Commit(options.Message, sig, sig), options.CancellationToken).ConfigureAwait(false);

                var info = ConvertCommit(commit);
                _logger?.Info($"Committed {info.ShortSha}: {info.ShortMessage}");
                return info;
            }
            finally
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            return Fail("Commit", ex, GitErrors.CommitFailedCode);
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current working-tree status of the repository.
    /// </summary>
    /// <returns>
    /// The status, or an error: <see cref="GitErrors.RepositoryNotFoundCode"/>,
    /// <see cref="GitErrors.TimeoutCode"/> (a transfer on the repository has stalled),
    /// <see cref="GitErrors.RepositoryDisposedCode"/> or <see cref="GitErrors.StatusFailedCode"/>.
    /// </returns>
    public Task<Result<GitRepoStatus>> GetStatusAsync() =>
        ReadAsync("Status", GitErrors.StatusFailedCode, repo => BuildStatus(repo.RetrieveStatus()));
}
