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
    /// <see cref="GitErrors.CancelledCode"/>, <see cref="GitErrors.TimeoutCode"/>,
    /// <see cref="GitErrors.RepositoryDisposedCode"/> or <see cref="GitErrors.CloneFailedCode"/>.
    /// </returns>
    public async Task<Result<RepositoryInfo>> CloneAsync(GitCloneOptions? options = null)
    {
        options ??= new GitCloneOptions();
        var diag = new GitDiagnostics();
        diag.AddMessage("Starting clone");
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds, _stallGrace, () => MarkStalled("Clone"));

        try
        {
            if (IsSshUrl(_config.RepositoryUrl))
                return Error.Validation(GitErrors.SshNotSupportedCode,
                    "SSH URLs are not supported — LibGit2Sharp 0.30 has no SSH transport. " +
                    "Use an HTTPS URL with a personal access token.");

            if (await EnterAsync(transfer.Token).ConfigureAwait(false) is { } busy)
                return busy;
            var createdDirectory = false;
            try
            {
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

                transfer.Begin();
                await Task.Run(() =>
                    Repository.Clone(_config.RepositoryUrl, _resolvedPath, libOptions),
                    transfer.Token).ConfigureAwait(false);

                diag.Complete();
                _logger?.Info($"Clone complete in {diag.Duration?.TotalSeconds:F2}s");
                _logger?.Debug($"Clone diagnostics: {string.Join("; ", diag.Messages)}");

                _repo?.Dispose();
                _repo = new Repository(_resolvedPath);
                return BuildRepositoryInfo(_repo);
            }
            catch (Exception)
            {
                // Leave no half-written clone behind: it would make every later clone fail with directory_not_empty.
                if (createdDirectory) TryDeleteDirectory(_resolvedPath);
                throw;
            }
            finally
            {
                Leave(transfer);
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
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
    /// <see cref="GitErrors.TimeoutCode"/>, <see cref="GitErrors.RepositoryDisposedCode"/> or
    /// <see cref="GitErrors.FetchFailedCode"/>.
    /// </returns>
    public async Task<Result<GitDiagnostics>> FetchAsync(GitFetchOptions? options = null)
    {
        options ??= new GitFetchOptions();
        var diag = new GitDiagnostics();
        using var transfer = new Transfer(options.CancellationToken, _timeoutSeconds, _stallGrace, () => MarkStalled("Fetch"));

        try
        {
            if (await EnterAsync(transfer.Token).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                diag.AddMessage($"Fetching from {options.RemoteName}");
                _logger?.Info($"Fetching '{_config.Id}' from {options.RemoteName}");

                var remote = repo.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitErrors.RemoteNotFound(options.RemoteName);

                var refSpecs = remote.FetchRefSpecs.Select(s => s.Specification).ToList();

                var fetchOpts = new LibGit2Sharp.FetchOptions
                {
                    Prune = options.Prune,
                    TagFetchMode = options.FetchTags ? TagFetchMode.Auto : TagFetchMode.None
                };
                ApplyFetchCallbacks(fetchOpts, transfer, options.OnProgress, diag);

                transfer.Begin();
                await Task.Run(() =>
                    Commands.Fetch(repo, options.RemoteName, refSpecs, fetchOpts, null),
                    transfer.Token).ConfigureAwait(false);

                diag.Complete();
                _logger?.Info($"Fetch complete in {diag.Duration?.TotalSeconds:F2}s");
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
            }).ConfigureAwait(false);
            if (fetchResult.IsFailure)
                return fetchResult;
            diag.Messages.AddRange(fetchResult.Value!.Messages);
            diag.ObjectCount = fetchResult.Value.ObjectCount;
            diag.BytesTransferred = fetchResult.Value.BytesTransferred;

            if (await EnterAsync(options.CancellationToken).ConfigureAwait(false) is { } busy)
                return busy;
            try
            {
                var repo = EnsureOpen();
                var upstream = ResolvePullUpstream(repo, remoteName);
                if (upstream?.Tip is null)
                    return Error.NotFound(GitErrors.BranchNotFoundCode,
                        $"Branch '{repo.Head.FriendlyName}' has no upstream branch on remote '{remoteName}'.");

                diag.AddMessage($"Integrating {upstream.FriendlyName} ({options.Strategy})");
                _logger?.Info($"Pulling '{_config.Id}' from {upstream.FriendlyName} ({options.Strategy})");

                var sig = GetSignature(repo);
                var conflict = await Task.Run(() => Integrate(repo, upstream, sig, options.Strategy, diag),
                    options.CancellationToken).ConfigureAwait(false);
                if (conflict is not null)
                    return conflict;

                diag.Complete();
                _logger?.Info($"Pull complete in {diag.Duration?.TotalSeconds:F2}s");
                return diag;
            }
            finally
            {
                Leave();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            return Fail("Pull", ex, GitErrors.PullFailedCode);
        }
    }
}
