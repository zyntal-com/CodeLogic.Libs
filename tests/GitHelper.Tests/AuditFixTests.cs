using System.Diagnostics;
using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CL.GitHelper.Services;
using CodeLogic.Core.Results;
using LibGit2Sharp;
using Xunit;
using CommitOptions = CL.GitHelper.Models.CommitOptions;

namespace GitHelper.Tests;

// Regression tests for the 2026-11 audit fixes: locked reads, disposal, config swaps, result-returning
// lookups and path validation. A real local repository (one commit, no remote) backs the manager.

public sealed class GitManagerAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-git-audit-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly GitManager _manager;

    public GitManagerAuditTests()
    {
        _repoPath = Path.Combine(_root, "local");
        Directory.CreateDirectory(_repoPath);
        Repository.Init(_repoPath);
        using (var repo = new Repository(_repoPath))
        {
            File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "one\n");
            Commands.Stage(repo, "a.txt");
            var sig = new Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("seed", sig, sig);
        }

        _manager = new GitManager(new GitHelperConfig
        {
            EnableRepositoryCaching = true,
            CacheTimeoutMinutes = 0,
            Repositories = [new RepositoryConfiguration { Id = "Local", RepositoryUrl = _repoPath, LocalPath = _repoPath }]
        }, _root);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { /* best effort */ }
    }

    private async Task<GitRepository> LocalAsync() => (await _manager.GetRepositoryAsync("Local")).ValueOrThrow();

    private static SemaphoreSlim OperationLock(GitRepository repository) =>
        (SemaphoreSlim)typeof(GitRepository).GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(repository)!;

    private static void AssertClean<T>(Result<T> result)
    {
        if (result.IsFailure)
            Assert.StartsWith("git.", result.Error!.Code);
    }

    // ── 1. Reads take the lock, so eviction cannot dispose the handle under them ──

    [Fact]
    public async Task Reads_racing_eviction_never_throw_and_only_fail_with_git_errors()
    {
        for (var round = 0; round < 25; round++)
        {
            var repo = await LocalAsync();
            var reads = new List<Task>();
            for (var i = 0; i < 4; i++)
            {
                reads.Add(Task.Run(async () => AssertClean(await repo.GetStatusAsync())));
                reads.Add(Task.Run(async () => AssertClean(await repo.GetRepositoryInfoAsync())));
                reads.Add(Task.Run(async () => AssertClean(await repo.ListBranchesAsync())));
                reads.Add(Task.Run(async () => AssertClean(await repo.GetCommitLogAsync(5))));
            }
            var evict = round % 2 == 0
                ? Task.Run(() => _manager.EvictFromCacheAsync("Local"))
                : Task.Run(() => repo.DisposeWhenIdleAsync());

            await Task.WhenAll([.. reads, evict]);   // an ObjectDisposedException or AccessViolation would surface here
        }

        // After every round the next lookup hands out a live instance.
        var live = await LocalAsync();
        Assert.True((await live.GetStatusAsync()).IsSuccess);
    }

    [Fact]
    public async Task A_disposed_instance_returns_repository_disposed()
    {
        var repo = await LocalAsync();
        await repo.DisposeWhenIdleAsync();

        var error = Assert.IsType<UnavailableError>((await repo.GetStatusAsync()).Error);
        Assert.Equal(GitErrors.RepositoryDisposedCode, error.Code);
        Assert.Equal(GitErrors.RepositoryDisposedCode, (await repo.FetchAsync()).Error!.Code);
        Assert.Equal(GitErrors.RepositoryDisposedCode, (await repo.CommitAsync(new CommitOptions { Message = "x" })).Error!.Code);
    }

    // ── 2. A consumer Dispose cannot poison the cache; a disposed entry is replaced ──

    [Fact]
    public async Task Consumer_Dispose_on_a_cached_instance_is_a_no_op()
    {
        var repo = await LocalAsync();

        repo.Dispose();

        Assert.False(repo.IsDisposed);
        Assert.Same(repo, await LocalAsync());
        Assert.True((await repo.GetStatusAsync()).IsSuccess);
    }

    [Fact]
    public async Task A_disposed_cached_instance_is_replaced_on_the_next_lookup()
    {
        var repo = await LocalAsync();
        await repo.DisposeWhenIdleAsync();   // what eviction does; a stale reference may still exist
        Assert.True(repo.IsDisposed);

        var fresh = await LocalAsync();

        Assert.NotSame(repo, fresh);
        Assert.False(fresh.IsDisposed);
        Assert.True((await fresh.GetStatusAsync()).IsSuccess, "the replacement must be usable");
        Assert.All(_manager.GetCacheStats().Entries, e => Assert.False(e.IsExpired));
    }

    // ── 3. Registration swaps the config instead of mutating the list others enumerate ──

    [Fact]
    public async Task Registering_and_unregistering_while_others_enumerate_does_not_throw()
    {
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(n => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = _manager.GetRepositoryIds();
                _ = _manager.GetCacheStats();
                _ = await _manager.GetAllStatusAsync();
                _ = await _manager.HealthCheckAsync();
            }
        })).ToArray();

        for (var i = 0; i < 150; i++)
        {
            var id = "R" + i;
            _manager.RegisterRepository(new RepositoryConfiguration { Id = id, RepositoryUrl = "https://example.invalid/r.git", LocalPath = id }).ThrowIfFailure();
            if (i % 3 == 0) Assert.True(await _manager.UnregisterRepositoryAsync(id));
        }
        stop.Cancel();

        await Task.WhenAll(readers);   // InvalidOperationException ("collection was modified") would surface here
        Assert.Equal(1 + 100, _manager.GetRepositoryIds().Count);
    }

    // ── 4. Stopping waits for running operations, but only up to the timeout ──

    [Fact]
    public async Task DisposeAsync_waits_for_running_operations_bounded_by_the_timeout()
    {
        var repo = await LocalAsync();
        var running = OperationLock(repo);
        await running.WaitAsync();   // stands in for a clone in progress

        var watch = Stopwatch.StartNew();
        await _manager.DisposeAsync(TimeSpan.FromMilliseconds(300));
        watch.Stop();

        Assert.InRange(watch.ElapsedMilliseconds, 200, 10_000);
        Assert.False(repo.IsDisposed, "a busy repository must not be disposed under its operation");
        Assert.Equal(GitErrors.NotInitializedCode, (await _manager.GetRepositoryAsync("Local")).Error!.Code);

        running.Release();
        for (var i = 0; i < 100 && !repo.IsDisposed; i++) await Task.Delay(20);
        Assert.True(repo.IsDisposed, "the repository is disposed once its operation finishes");
    }

    [Fact]
    public async Task DisposeAsync_returns_at_once_when_nothing_is_running()
    {
        _ = await LocalAsync();
        var watch = Stopwatch.StartNew();
        await _manager.DisposeAsync(TimeSpan.FromSeconds(30));
        Assert.True(watch.ElapsedMilliseconds < 5_000);
    }

    // ── 7. Expected failures are results, not exceptions ──

    [Fact]
    public async Task Unknown_repository_id_is_a_not_found_result()
    {
        var result = await _manager.GetRepositoryAsync("nope");

        var error = Assert.IsType<NotFoundError>(result.Error);
        Assert.Equal(GitErrors.RepositoryNotFoundCode, error.Code);
        Assert.Equal(GitErrors.NotConfiguredDetails, error.Details);
    }

    [Fact]
    public async Task Repository_ids_are_case_insensitive_and_share_one_cache_entry()
    {
        var a = await LocalAsync();
        var b = (await _manager.GetRepositoryAsync("local")).ValueOrThrow();
        Assert.Same(a, b);
        Assert.Single(_manager.GetCacheStats().Entries);
    }

    [Fact]
    public void Duplicate_registration_is_a_conflict_result()
    {
        var result = _manager.RegisterRepository(new RepositoryConfiguration { Id = "LOCAL", RepositoryUrl = "https://x/r.git", LocalPath = "other" });

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(GitErrors.RepositoryExistsCode, error.Code);
    }

    [Theory]
    [InlineData("", "r")]
    [InlineData("https://x/r.git", "")]
    [InlineData("https://x/r.git", "../../etc")]
    public void Invalid_registration_is_a_validation_result(string url, string localPath)
    {
        var result = _manager.RegisterRepository(new RepositoryConfiguration { Id = "New", RepositoryUrl = url, LocalPath = localPath });

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(GitErrors.InvalidConfigurationCode, error.Code);
        Assert.DoesNotContain("New", _manager.GetRepositoryIds());
    }
}

// ── 6. A relative LocalPath may not leave the base directory ──

public sealed class LocalPathValidationTests
{
    [Theory]
    [InlineData("../../etc", true)]
    [InlineData("..", true)]
    [InlineData("a/../..", true)]
    [InlineData("a/../../b", true)]
    [InlineData(@"a\..\..\b", true)]
    [InlineData("./../x", true)]
    [InlineData("repo", false)]
    [InlineData("sub/../x", false)]
    [InlineData("./a/./b", false)]
    [InlineData("a/b/../../c", false)]
    [InlineData("", false)]
    public void Relative_paths_that_climb_above_the_base_are_rejected(string localPath, bool escapes) =>
        Assert.Equal(escapes, RepositoryConfiguration.LocalPathEscapesBaseDirectory(localPath));

    [Fact]
    public void Rooted_paths_are_explicit_and_allowed()
    {
        var rooted = OperatingSystem.IsWindows() ? @"C:\repos\x" : "/srv/repos/x";
        Assert.False(RepositoryConfiguration.LocalPathEscapesBaseDirectory(rooted));
        Assert.False(RepositoryConfiguration.LocalPathEscapesBaseDirectory(Path.Combine(rooted, "..", "y")));
        Assert.True(new RepositoryConfiguration { RepositoryUrl = "https://x/r.git", LocalPath = rooted }.IsValid());
    }

    [Fact]
    public void Validate_and_IsValid_reject_an_escaping_path()
    {
        var escaping = new RepositoryConfiguration { Id = "Esc", RepositoryUrl = "https://x/r.git", LocalPath = "../../etc" };
        Assert.False(escaping.IsValid());

        var result = new GitHelperConfig { Repositories = [escaping] }.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("leaves the base directory", result.ToString());
        Assert.True(new GitHelperConfig
        {
            Repositories = [new RepositoryConfiguration { Id = "Ok", RepositoryUrl = "https://x/r.git", LocalPath = "sub/../ok" }]
        }.Validate().IsValid);
    }
}
