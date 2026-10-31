using CL.GitHelper;
using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CL.GitHelper.Services;
using CodeLogic;                         // Libraries, CodeLogicOptions
using CodeLogic.Core.Results;
using LibGit2Sharp;                      // direct repo setup for the offline temp-repo fixture
using Xunit;
using CommitOptions = CL.GitHelper.Models.CommitOptions;   // disambiguate from LibGit2Sharp.CommitOptions
using FetchOptions = CL.GitHelper.Models.FetchOptions;
using PullOptions = CL.GitHelper.Models.PullOptions;
using PushOptions = CL.GitHelper.Models.PushOptions;

namespace GitHelper.Tests;

// ── CL.GitHelper tests ───────────────────────────────────────────────────────────
// HYBRID strategy:
//   • LOCAL git operations (init/commit/status/log/branch) are exercised OFFLINE against
//     a throwaway temp repository that the fixture creates on disk with LibGit2Sharp
//     directly (Init + write file + Stage + Commit). The real CodeLogic runtime is booted
//     once and pointed (via config.githelper.json) at that temp repo.
//   • Operations needing a remote (clone/fetch/push/pull) are NOT generally offline-
//     testable. The SSH fail-fast regression IS offline (it short-circuits before any
//     network) and is asserted directly. The live clone test is env-gated.
//
// The CodeLogic runtime is a process-wide singleton, so every test that needs the booted
// library lives behind a single shared fixture serialized by the [Collection] attribute.

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless the named
/// environment variable is set. xUnit 2.9.3 has no runtime <c>Assert.Skip</c>, so the
/// skip decision is made here (at discovery time) and reported as a proper "Skipped".
/// </summary>
internal sealed class FactRequiresEnvAttribute : FactAttribute
{
    public FactRequiresEnvAttribute(string envVar, string reason)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = reason;
    }
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────

public sealed class GitHelperRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_githelper_test_" + Guid.NewGuid().ToString("N"));

    /// <summary>Absolute path of the local working repo that LOCAL ops run against.</summary>
    public string LocalRepoPath { get; private set; } = "";

    /// <summary>Relative path of the seeded tracked file inside the repo.</summary>
    public const string TrackedFile = "tracked.txt";

    public GitHelperLibrary Library { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TempDir);

        // 1. Create a real local git repo on disk with one commit (offline, no remote).
        LocalRepoPath = Path.Combine(TempDir, "local-repo");
        Directory.CreateDirectory(LocalRepoPath);
        Repository.Init(LocalRepoPath);
        using (var repo = new Repository(LocalRepoPath))
        {
            File.WriteAllText(Path.Combine(LocalRepoPath, TrackedFile), "initial content\n");
            Commands.Stage(repo, TrackedFile);
            var sig = new Signature("Seed Author", "seed@example.com", DateTimeOffset.Now);
            repo.Commit("initial commit", sig, sig);
        }

        // 2. Boot the real CodeLogic runtime pointing the "Default" repo at the local repo,
        //    and a separate "Ssh" repo with an SSH URL for the fail-fast regression.
        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var localPathJson = LocalRepoPath.Replace("\\", "\\\\");
        var sshLocalPathJson = Path.Combine(TempDir, "ssh-repo").Replace("\\", "\\\\");

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.GitHelper");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.githelper.json"), $$"""
        {
          "enabled": true,
          "baseDirectory": "",
          "repositories": [
            {
              "id": "Default",
              "name": "Local Test Repo",
              "repositoryUrl": "file:///{{localPathJson}}",
              "localPath": "{{localPathJson}}",
              "defaultBranch": "main",
              "autoFetch": false
            },
            {
              "id": "Ssh",
              "name": "SSH Repo",
              "repositoryUrl": "git@github.com:org/repo.git",
              "localPath": "{{sshLocalPathJson}}",
              "defaultBranch": "main",
              "autoFetch": false
            }
          ]
        }
        """);

        await Libraries.LoadAsync<GitHelperLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<GitHelperLibrary>()
            ?? throw new InvalidOperationException("GitHelperLibrary not available after start.");
    }

    public async Task DisposeAsync()
    {
        try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* git pack files may linger briefly on Windows; ignore */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<GitHelperRuntimeFixture> { }

// ── Local (offline) operations against the temp repo ─────────────────────────────

[Collection("codelogic")]
public sealed class GitHelperLocalTests
{
    private readonly GitHelperRuntimeFixture _fx;

    public GitHelperLocalTests(GitHelperRuntimeFixture fx) => _fx = fx;

    private Task<GitRepository> RepoAsync() => _fx.Library.GetRepositoryAsync("Default");

    [Fact]
    public async Task GetRepositoryInfo_reports_branch_and_head()
    {
        var repo = await RepoAsync();
        var info = await repo.GetRepositoryInfoAsync();

        Assert.True(info.IsSuccess, info.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(info.Value!.CurrentBranch));
        Assert.False(string.IsNullOrWhiteSpace(info.Value.HeadCommitSha));
    }

    [Fact]
    public async Task GetStatus_sees_a_new_untracked_file()
    {
        var repo = await RepoAsync();
        var newFile = "untracked-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(_fx.LocalRepoPath, newFile), "hello\n");

        var status = await repo.GetStatusAsync();

        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.True(status.Value!.IsDirty || status.Value.UntrackedFiles.Count > 0);
        Assert.Contains(status.Value.UntrackedFiles, f => f.FilePath == newFile);

        // cleanup so we don't perturb later tests in this serialized class
        File.Delete(Path.Combine(_fx.LocalRepoPath, newFile));
    }

    [Fact]
    public async Task Commit_stages_and_records_a_commit()
    {
        var repo = await RepoAsync();
        var file = "commit-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(_fx.LocalRepoPath, file), "payload\n");

        var result = await repo.CommitAsync(new CommitOptions
        {
            Message = "test commit",
            AuthorName = "Test Author",
            AuthorEmail = "test@example.com",
            FilesToStage = [file]
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("test commit", result.Value!.Message.Trim());
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Sha));
    }

    [Fact]
    public async Task GetCommitLog_returns_at_least_one_commit()
    {
        var repo = await RepoAsync();
        var log = await repo.GetCommitLogAsync(10);

        Assert.True(log.IsSuccess, log.Error?.Message);
        Assert.NotEmpty(log.Value!);
    }

    [Fact]
    public async Task ListBranches_local_only_includes_the_default_branch()
    {
        var repo = await RepoAsync();
        var branches = await repo.ListBranchesAsync(includeRemote: false);

        Assert.True(branches.IsSuccess, branches.Error?.Message);
        Assert.NotEmpty(branches.Value!);
        Assert.All(branches.Value!, b => Assert.False(b.IsRemote));
    }

    // ── SSH fail-fast regression (cleanly offline) ───────────────────────────────
    // CloneAsync must short-circuit on an SSH URL before any network access and return a
    // failure whose message mentions SSH/HTTPS.
    [Fact]
    public async Task CloneAsync_with_ssh_url_fails_fast_offline()
    {
        var repo = await _fx.Library.GetRepositoryAsync("Ssh");
        var result = await repo.CloneAsync();

        Assert.False(result.IsSuccess);
        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(GitErrors.SshNotSupportedCode, error.Code);
        Assert.Contains("SSH", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── EnsureUpToDate dirty-guard regression ────────────────────────────────────
    // On the local temp repo (no reachable remote) make the working tree dirty, then call
    // EnsureUpToDateAsync(discardLocalChanges: false). It must return a failure rather than
    // discard the changes. (Offline, the fetch step fails first because the repo has no
    // remote; either way the contract — never silently discard — holds: result is failure
    // and the dirty file is still on disk.)
    [Fact]
    public async Task EnsureUpToDate_refuses_when_dirty_and_not_discarding()
    {
        var repo = await RepoAsync();

        var trackedPath = Path.Combine(_fx.LocalRepoPath, GitHelperRuntimeFixture.TrackedFile);
        var original = File.ReadAllText(trackedPath);
        File.WriteAllText(trackedPath, original + "local edit\n");
        try
        {
            var result = await repo.EnsureUpToDateAsync(discardLocalChanges: false);

            Assert.False(result.IsSuccess);
            // The local modification must NOT have been discarded.
            Assert.Contains("local edit", File.ReadAllText(trackedPath));
        }
        finally
        {
            File.WriteAllText(trackedPath, original);
        }
    }

    // ── Error kinds and codes ────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_with_missing_remote_returns_NotFoundError()
    {
        var repo = await RepoAsync();
        var result = await repo.FetchAsync(new FetchOptions { RemoteName = "no-such-remote" });

        Assert.True(result.IsFailure);
        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal("git.remote_not_found", error.Code);
        Assert.Contains("no-such-remote", error.Message);
    }

    [Fact]
    public async Task Push_with_missing_remote_returns_NotFoundError()
    {
        var repo = await RepoAsync();
        var result = await repo.PushAsync(new PushOptions { RemoteName = "no-such-remote" });

        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.RemoteNotFoundCode, error.Code);
    }

    [Fact]
    public async Task Pull_passes_through_the_fetch_error()
    {
        var repo = await RepoAsync();
        var result = await repo.PullAsync(new PullOptions
        {
            FetchOptions = new FetchOptions { RemoteName = "no-such-remote" }
        });

        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.RemoteNotFoundCode, error.Code);
    }

    [Fact]
    public async Task Commit_with_empty_message_returns_ValidationError()
    {
        var repo = await RepoAsync();
        var result = await repo.CommitAsync(new CommitOptions { Message = "   " });

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(GitErrors.EmptyCommitMessageCode, error.Code);
    }

    [Fact]
    public async Task Checkout_of_unknown_branch_returns_NotFoundError()
    {
        var repo = await RepoAsync();
        Result result = await repo.CheckoutBranchAsync("no-such-branch-" + Guid.NewGuid().ToString("N")[..8]);

        Assert.True(result.IsFailure);
        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.BranchNotFoundCode, error.Code);
        Assert.Equal("NotFoundException", error.Details);
    }

    [Fact]
    public async Task ResetHard_to_unknown_branch_returns_NotFoundError()
    {
        var repo = await RepoAsync();
        Result result = await repo.ResetHardAsync("no-such-branch");

        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.BranchNotFoundCode, error.Code);
    }

    [Fact]
    public async Task Operations_on_an_uncloned_repository_return_repository_not_found()
    {
        var repo = await _fx.Library.GetRepositoryAsync("Ssh");   // configured but never cloned
        var result = await repo.GetRepositoryInfoAsync();

        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.RepositoryNotFoundCode, error.Code);
        Assert.Equal("RepositoryNotFoundException", error.Details);
    }

    // A real offline remote: clone the fixture repo (file path), dirty the clone, and check
    // that EnsureUpToDate refuses with a ConflictError instead of discarding the change.
    [Fact]
    public async Task EnsureUpToDate_on_dirty_clone_returns_uncommitted_changes_conflict()
    {
        var clonePath = Path.Combine(_fx.TempDir, "dirty-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Clone(_fx.LocalRepoPath, clonePath);
        string branch;
        using (var clone = new Repository(clonePath)) branch = clone.Head.FriendlyName;

        var id = "Dirty" + Guid.NewGuid().ToString("N")[..8];
        _fx.Library.RegisterRepository(new RepositoryConfiguration
        {
            Id = id,
            Name = "Dirty Clone",
            RepositoryUrl = _fx.LocalRepoPath,
            LocalPath = clonePath,
            DefaultBranch = branch
        });

        var trackedPath = Path.Combine(clonePath, GitHelperRuntimeFixture.TrackedFile);
        File.AppendAllText(trackedPath, "local edit\n");

        var repo = await _fx.Library.GetRepositoryAsync(id);

        // Fetch succeeds against the file remote and carries diagnostics as its value.
        var fetch = await repo.FetchAsync();
        Assert.True(fetch.IsSuccess, fetch.Error?.Message);
        Assert.NotNull(fetch.Value!.Duration);

        var result = await repo.EnsureUpToDateAsync(discardLocalChanges: false);

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(GitErrors.UncommittedChangesCode, error.Code);
        Assert.Contains("local edit", File.ReadAllText(trackedPath));
    }

    [Fact]
    public async Task GetAllStatus_returns_a_result_per_repository()
    {
        var all = await _fx.Library.GetAllStatusAsync();

        Assert.True(all["Default"].IsSuccess, all["Default"].Error?.Message);
        Assert.IsType<NotFoundError>(all["Ssh"].Error);
    }

    [Fact]
    public async Task HealthCheck_is_healthy_for_a_real_local_repo()
    {
        var health = await _fx.Library.HealthCheckAsync();
        // Default repo is a valid local repo. (Ssh repo isn't cloned, so overall status may
        // be Degraded; assert it isn't outright Unhealthy.)
        Assert.NotEqual(CodeLogic.Framework.Libraries.HealthStatusLevel.Unhealthy, health.Status);
    }
}

// ── Gated live clone test (needs a real HTTPS remote) ────────────────────────────

[Collection("codelogic")]
public sealed class GitHelperLiveTests
{
    private readonly GitHelperRuntimeFixture _fx;

    public GitHelperLiveTests(GitHelperRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv("CL_GIT_TEST_REMOTE", "set CL_GIT_TEST_REMOTE to an https git url to run the live clone test")]
    public async Task Clone_real_https_remote()
    {
        var url = Environment.GetEnvironmentVariable("CL_GIT_TEST_REMOTE")!;
        var dest = Path.Combine(_fx.TempDir, "live-clone-" + Guid.NewGuid().ToString("N")[..8]);

        _fx.Library.RegisterRepository(new RepositoryConfiguration
        {
            Id = "Live",
            Name = "Live Remote",
            RepositoryUrl = url,
            LocalPath = dest,
            DefaultBranch = "main"
        });

        var repo = await _fx.Library.GetRepositoryAsync("Live");
        var result = await repo.EnsureUpToDateAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.HeadCommitSha));
    }
}

// ── Cache eviction never disposes a repository under a running operation ─────────

public sealed class GitManagerCacheEvictionTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "cl-git-cache-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly GitManager _manager;

    public GitManagerCacheEvictionTests()
    {
        Directory.CreateDirectory(_baseDir);
        _manager = new GitManager(new GitHelperConfig
        {
            EnableRepositoryCaching = true,
            CacheTimeoutMinutes = 5,
            Repositories = [new RepositoryConfiguration { Id = "Default", LocalPath = "repo" }]
        }, _baseDir, time: _clock);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Stands in for a clone or fetch in progress: every git operation holds the repository's lock.
    private static SemaphoreSlim OperationLock(GitRepository repository) =>
        (SemaphoreSlim)typeof(GitRepository).GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(repository)!;

    private static async Task AssertDisposedSoonAsync(GitRepository repository)
    {
        for (var i = 0; i < 100 && !repository.IsDisposed; i++)
            await Task.Delay(20);
        Assert.True(repository.IsDisposed);
    }

    private async Task AssertWaitsForRunningOperationAsync(Func<GitRepository, Task> evict)
    {
        var repository = await _manager.GetRepositoryAsync();
        var running = OperationLock(repository);
        await running.WaitAsync();

        await evict(repository);
        await Task.Delay(50);

        Assert.False(repository.IsDisposed);
        running.Release();      // threw ObjectDisposedException when eviction disposed immediately
        await AssertDisposedSoonAsync(repository);
    }

    [Fact]
    public Task Expired_entry_cleanup_waits_for_the_running_operation() =>
        AssertWaitsForRunningOperationAsync(_ =>
        {
            _clock.Now += TimeSpan.FromMinutes(6);
            _manager.CleanupExpiredEntries();
            Assert.Empty(_manager.GetCacheStats().Entries);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Stale_entry_replaced_by_GetRepositoryAsync_waits_for_the_running_operation() =>
        AssertWaitsForRunningOperationAsync(async stale =>
        {
            _clock.Now += TimeSpan.FromMinutes(6);
            var fresh = await _manager.GetRepositoryAsync();
            Assert.NotSame(stale, fresh);
        });

    [Fact]
    public Task EvictFromCacheAsync_waits_for_the_running_operation() =>
        AssertWaitsForRunningOperationAsync(_ => _manager.EvictFromCacheAsync("Default"));

    [Fact]
    public Task ClearCacheAsync_waits_for_the_running_operation() =>
        AssertWaitsForRunningOperationAsync(_ => _manager.ClearCacheAsync());

    [Fact]
    public async Task An_idle_expired_entry_is_disposed_right_away()
    {
        var repository = await _manager.GetRepositoryAsync();
        _clock.Now += TimeSpan.FromMinutes(6);

        _manager.CleanupExpiredEntries();

        await AssertDisposedSoonAsync(repository);
    }

    [Fact]
    public async Task A_recently_used_entry_is_kept()
    {
        var repository = await _manager.GetRepositoryAsync();
        _clock.Now += TimeSpan.FromMinutes(4);

        _manager.CleanupExpiredEntries();

        Assert.Same(repository, await _manager.GetRepositoryAsync());
        Assert.False(repository.IsDisposed);
    }
}

// ── GitErrors mapping (pure) ─────────────────────────────────────────────────────

public sealed class GitErrorsTests
{
    [Fact]
    public void Authentication_failure_maps_to_UnauthorizedError()
    {
        var error = GitErrors.FromException(
            new LibGit2SharpException("too many redirects or authentication replays"),
            "Fetch failed: too many redirects or authentication replays",
            GitErrors.FetchFailedCode);

        var unauthorized = Assert.IsType<UnauthorizedError>(error);
        Assert.Equal(GitErrors.AuthenticationFailedCode, unauthorized.Code);
        Assert.Equal("LibGit2SharpException", unauthorized.Details);
    }

    [Fact]
    public void Unknown_exception_maps_to_InternalError_with_fallback_code_and_type_name()
    {
        var error = GitErrors.FromException(new IOException("disk full"), "Clone failed: disk full", GitErrors.CloneFailedCode);

        var internalError = Assert.IsType<InternalError>(error);
        Assert.Equal("git.clone_failed", internalError.Code);
        Assert.Equal("Clone failed: disk full", internalError.Message);
        Assert.Equal("IOException", internalError.Details);
    }

    [Fact]
    public void Cancellation_maps_to_UnavailableError()
    {
        var error = GitErrors.FromException(new OperationCanceledException(), "Fetch failed: cancelled", GitErrors.FetchFailedCode);

        Assert.IsType<UnavailableError>(error);
        Assert.Equal(GitErrors.CancelledCode, error.Code);
    }
}
