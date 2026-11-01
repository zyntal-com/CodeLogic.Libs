using CL.GitHelper.Errors;
using CL.GitHelper.Models;
using CL.GitHelper.Services;
using CodeLogic.Core.Results;
using LibGit2Sharp;
using Xunit;
using CloneOptions = LibGit2Sharp.CloneOptions;
using FetchOptions = CL.GitHelper.Models.FetchOptions;
using PullOptions = CL.GitHelper.Models.PullOptions;
using PushOptions = CL.GitHelper.Models.PushOptions;

namespace GitHelper.Tests;

// ── Credential hygiene (pure) ────────────────────────────────────────────────────

public sealed class GitSecurityTests
{
    [Theory]
    [InlineData("https://user:s3cr3t@github.com/org/repo.git", "https://***@github.com/org/repo.git")]
    [InlineData("https://ghp_token@github.com/org/repo.git", "https://***@github.com/org/repo.git")]
    [InlineData("Cloning http://a:b@host:8080/x → /tmp/y", "Cloning http://***@host:8080/x → /tmp/y")]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    [InlineData("two: https://u:p@a.example/x and https://q@b.example/y",
        "two: https://***@a.example/x and https://***@b.example/y")]
    public void Redact_strips_user_info_from_urls(string input, string expected) =>
        Assert.Equal(expected, GitSecurity.Redact(input));

    [Fact]
    public void Redact_masks_the_configured_secret_wherever_it_appears()
    {
        var redacted = GitSecurity.Redact("remote said: bad token ghp_abc123 for user", "ghp_abc123");

        Assert.DoesNotContain("ghp_abc123", redacted);
        Assert.Contains(GitSecurity.Mask, redacted);
    }

    [Fact]
    public void Redact_handles_null_and_empty() =>
        Assert.Equal("", GitSecurity.Redact(null, "x"));

    [Theory]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/other.git", true)]
    [InlineData("https://github.com/org/repo.git", "https://GITHUB.com/org/repo.git", true)]
    [InlineData("https://github.com/org/repo.git", "https://gitlab.com/org/repo.git", false)]
    [InlineData("https://github.com/org/repo.git", "https://evil.github.com.attacker.net/x", false)]
    [InlineData("https://github.com/org/repo.git", "http://github.com/org/repo.git", false)]       // no downgrade
    [InlineData("https://git.example.com/r.git", "https://git.example.com:8443/r.git", false)]      // other port
    [InlineData("https://user:pat@github.com/org/repo.git", "https://github.com/org/repo.git", true)]
    [InlineData("git@github.com:org/repo.git", "https://github.com/org/repo.git", false)]
    [InlineData("C:/repos/local", "https://github.com/org/repo.git", false)]
    [InlineData("https://github.com/org/repo.git", null, false)]
    public void CredentialsAllowedFor_requires_the_same_scheme_host_and_port(string configured, string? request, bool allowed) =>
        Assert.Equal(allowed, GitSecurity.CredentialsAllowedFor(configured, request));

    private static RepositoryConfiguration WithToken(string url = "https://github.com/org/repo.git") => new()
    {
        Id = "Repo",
        RepositoryUrl = url,
        LocalPath = "repo",
        Password = "ghp_secret"
    };

    [Fact]
    public void Credentials_handler_gives_the_token_to_the_configured_host()
    {
        var handler = GitRepository.CreateCredentialsHandler(WithToken())!;

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            handler("https://github.com/org/submodule.git", null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("x-access-token", credentials.Username);
        Assert.Equal("ghp_secret", credentials.Password);
    }

    [Theory]
    [InlineData("https://gitlab.com/other/submodule.git")]
    [InlineData("http://github.com/org/repo.git")]
    public void Credentials_handler_withholds_the_token_from_other_hosts(string requestUrl)
    {
        var handler = GitRepository.CreateCredentialsHandler(WithToken())!;

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            handler(requestUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("", credentials.Password);
        Assert.Equal("", credentials.Username);
    }

    [Fact]
    public void No_token_means_no_credentials_handler() =>
        Assert.Null(GitRepository.CreateCredentialsHandler(new RepositoryConfiguration { RepositoryUrl = "https://x.example/r.git" }));

    [Fact]
    public void Configured_username_is_sent_with_the_token()
    {
        var config = WithToken();
        config.Username = "deploy";
        var credentials = (UsernamePasswordCredentials)GitRepository.CreateCredentialsHandler(config)!(
            "https://github.com/org/repo.git", null, SupportedCredentialTypes.UsernamePassword);

        Assert.Equal("deploy", credentials.Username);
    }
}

// ── Option plumbing (pure) ───────────────────────────────────────────────────────

public sealed class GitOptionPlumbingTests
{
    [Fact]
    public void Pull_remote_defaults_to_origin() =>
        Assert.Equal("origin", GitRepository.ResolvePullRemote(new PullOptions()));

    [Fact]
    public void Pull_RemoteName_is_used() =>
        Assert.Equal("upstream", GitRepository.ResolvePullRemote(new PullOptions { RemoteName = "upstream" }));

    [Fact]
    public void Pull_RemoteName_wins_over_the_fetch_remote() =>
        Assert.Equal("upstream", GitRepository.ResolvePullRemote(new PullOptions
        {
            RemoteName = "upstream",
            FetchOptions = new FetchOptions { RemoteName = "mirror" }
        }));

    [Fact]
    public void Fetch_remote_is_used_when_RemoteName_is_left_at_origin() =>
        Assert.Equal("mirror", GitRepository.ResolvePullRemote(new PullOptions
        {
            FetchOptions = new FetchOptions { RemoteName = "mirror" }
        }));

    [Fact]
    public void Push_refspecs_force_the_branch_but_never_tags()
    {
        var specs = GitRepository.BuildPushRefSpecs("main", force: true, ["refs/tags/v1"]);

        Assert.Equal(["+refs/heads/main:refs/heads/main", "refs/tags/v1:refs/tags/v1"], specs);
    }

    [Fact]
    public void Push_refspecs_without_tags_hold_only_the_branch() =>
        Assert.Equal(["refs/heads/feature:refs/heads/feature"], GitRepository.BuildPushRefSpecs("feature", false, []));

    [Fact]
    public void Validate_rejects_duplicate_ids_and_bad_ranges()
    {
        var config = new GitHelperConfig
        {
            Repositories =
            [
                new RepositoryConfiguration { Id = "A", RepositoryUrl = "https://x/a.git", LocalPath = "a" },
                new RepositoryConfiguration { Id = "a", RepositoryUrl = "https://x/b.git", LocalPath = "b", TimeoutSeconds = -1, AutoFetchIntervalMinutes = -5 }
            ]
        };

        var result = config.Validate();

        Assert.False(result.IsValid);
        var text = result.ToString();
        Assert.Contains("more than once", text);
        Assert.Contains("TimeoutSeconds", text);
        Assert.Contains("AutoFetchIntervalMinutes", text);
    }

    [Fact]
    public void Validate_accepts_zero_timeout_meaning_library_default()
    {
        var config = new GitHelperConfig
        {
            Repositories = [new RepositoryConfiguration { Id = "A", RepositoryUrl = "https://x/a.git", LocalPath = "a", TimeoutSeconds = 0 }]
        };

        Assert.True(config.Validate().IsValid);
    }
}

// ── Network operations against local file remotes (offline) ──────────────────────

/// <summary>
/// A bare "remote" seeded with one commit, plus helpers to clone it and to publish commits to it
/// from a second clone, so fetch/pull/push run for real without a network.
/// </summary>
public sealed class LocalRemoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl-git-net-" + Guid.NewGuid().ToString("N"));
    private readonly string _remote;
    private readonly string _branch;
    private readonly List<GitRepository> _repositories = [];
    private static readonly Signature Sig = new("Test", "test@example.com", DateTimeOffset.Now);

    public LocalRemoteTests()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        Repository.Init(seed);
        using (var repo = new Repository(seed))
        {
            File.WriteAllText(Path.Combine(seed, "a.txt"), "one\n");
            Commands.Stage(repo, "a.txt");
            repo.Commit("seed", Sig, Sig);
            _branch = repo.Head.FriendlyName;
        }

        _remote = Path.Combine(_root, "remote.git");
        Repository.Clone(seed, _remote, new CloneOptions { IsBare = true });
    }

    public void Dispose()
    {
        foreach (var repository in _repositories) repository.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception) { /* best effort */ }
    }

    private string CloneRemote(string name, string? remote = null)
    {
        var path = Path.Combine(_root, name);
        Repository.Clone(remote ?? _remote, path);
        return path;
    }

    private GitRepository Open(string localPath, int timeoutSeconds = 300, string? url = null)
    {
        var repository = new GitRepository(new RepositoryConfiguration
        {
            Id = Path.GetFileName(localPath),
            RepositoryUrl = url ?? _remote,
            LocalPath = localPath,
            DefaultBranch = _branch,
            TimeoutSeconds = timeoutSeconds
        }, localPath);
        _repositories.Add(repository);
        return repository;
    }

    private static string CommitFile(string repoPath, string file, string content, string message)
    {
        using var repo = new Repository(repoPath);
        File.WriteAllText(Path.Combine(repoPath, file), content);
        Commands.Stage(repo, file);
        return repo.Commit(message, Sig, Sig).Sha;
    }

    /// <summary>Commits in a helper clone and pushes it to <paramref name="remote"/>. Returns the new tip.</summary>
    private string Publish(string file, string content, int commits = 1, string? remote = null)
    {
        var helper = CloneRemote("helper-" + Guid.NewGuid().ToString("N")[..8], remote);
        var tip = "";
        for (var i = 0; i < commits; i++)
            tip = CommitFile(helper, $"{i}-{file}", content + i + new string('x', 4096 * (i + 1)), $"remote {file} {i}");
        using var repo = new Repository(helper);
        repo.Network.Push(repo.Network.Remotes["origin"], $"refs/heads/{_branch}:refs/heads/{_branch}");
        return tip;
    }

    private string RemoteTip()
    {
        using var repo = new Repository(_remote);
        return repo.Branches[_branch].Tip.Sha;
    }

    // ── Push ──

    [Fact]
    public async Task Push_sends_the_branch_and_reports_progress()
    {
        var work = CloneRemote("work");
        var local = CommitFile(work, "b.txt", "two\n", "local");
        var progress = 0;

        var result = await Open(work).PushAsync(new PushOptions { OnProgress = _ => Interlocked.Increment(ref progress) });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(local, RemoteTip());
        Assert.True(progress > 0);
    }

    [Fact]
    public async Task PushTags_sends_tags_only_when_asked()
    {
        var work = CloneRemote("work");
        CommitFile(work, "b.txt", "two\n", "local");
        using (var repo = new Repository(work)) repo.ApplyTag("v1.0");
        var repository = Open(work);

        Assert.True((await repository.PushAsync()).IsSuccess);
        using (var remote = new Repository(_remote)) Assert.Null(remote.Tags["v1.0"]);

        Assert.True((await repository.PushAsync(new PushOptions { PushTags = true })).IsSuccess);
        using (var remote = new Repository(_remote)) Assert.NotNull(remote.Tags["v1.0"]);
    }

    [Fact]
    public async Task Push_of_a_diverged_branch_returns_a_conflict()
    {
        var work = CloneRemote("work");
        Publish("r.txt", "remote");
        CommitFile(work, "b.txt", "local\n", "local");

        var result = await Open(work).PushAsync();

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Contains(error.Code, new[] { GitErrors.NonFastForwardCode, GitErrors.PushRejectedCode });
    }

    // ── Pull strategies ──

    [Fact]
    public async Task Pull_merge_fast_forwards_when_there_are_no_local_commits()
    {
        var work = CloneRemote("work");
        var tip = Publish("r.txt", "remote");

        var result = await Open(work).PullAsync();

        Assert.True(result.IsSuccess, result.Error?.Message);
        using var repo = new Repository(work);
        Assert.Equal(tip, repo.Head.Tip.Sha);
    }

    [Fact]
    public async Task Pull_merge_creates_a_merge_commit_for_diverged_branches()
    {
        var work = CloneRemote("work");
        var remoteTip = Publish("r.txt", "remote");
        CommitFile(work, "b.txt", "local\n", "local");

        var result = await Open(work).PullAsync(new PullOptions { Strategy = MergeStrategy.Merge });

        Assert.True(result.IsSuccess, result.Error?.Message);
        using var repo = new Repository(work);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
        Assert.Contains(repo.Head.Tip.Parents, p => p.Sha == remoteTip);
    }

    [Fact]
    public async Task Pull_fast_forward_only_refuses_diverged_branches()
    {
        var work = CloneRemote("work");
        Publish("r.txt", "remote");
        var local = CommitFile(work, "b.txt", "local\n", "local");

        var result = await Open(work).PullAsync(new PullOptions { Strategy = MergeStrategy.FastForwardOnly });

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(GitErrors.NonFastForwardCode, error.Code);
        using var repo = new Repository(work);
        Assert.Equal(local, repo.Head.Tip.Sha);
    }

    [Fact]
    public async Task Pull_rebase_replays_local_commits_on_the_remote_tip()
    {
        var work = CloneRemote("work");
        var remoteTip = Publish("r.txt", "remote");
        CommitFile(work, "b.txt", "local\n", "local change");

        var result = await Open(work).PullAsync(new PullOptions { Strategy = MergeStrategy.Rebase });

        Assert.True(result.IsSuccess, result.Error?.Message);
        using var repo = new Repository(work);
        var head = repo.Head.Tip;
        Assert.Equal("local change", head.MessageShort);
        Assert.Equal(remoteTip, Assert.Single(head.Parents).Sha);
    }

    [Fact]
    public async Task Pull_merge_conflict_returns_merge_conflict()
    {
        var work = CloneRemote("work");
        var helper = CloneRemote("helper");
        CommitFile(helper, "a.txt", "remote edit\n", "remote");
        using (var repo = new Repository(helper))
            repo.Network.Push(repo.Network.Remotes["origin"], $"refs/heads/{_branch}:refs/heads/{_branch}");
        CommitFile(work, "a.txt", "local edit\n", "local");

        var result = await Open(work).PullAsync();

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(GitErrors.MergeConflictCode, error.Code);
    }

    [Fact]
    public async Task Pull_uses_the_named_remote()
    {
        var work = CloneRemote("work");
        var upstream = Path.Combine(_root, "upstream.git");
        Repository.Clone(_remote, upstream, new CloneOptions { IsBare = true });
        using (var repo = new Repository(work)) repo.Network.Remotes.Add("upstream", upstream);
        var tip = Publish("u.txt", "upstream", remote: upstream);

        var result = await Open(work).PullAsync(new PullOptions { RemoteName = "upstream" });

        Assert.True(result.IsSuccess, result.Error?.Message);
        using var check = new Repository(work);
        Assert.Equal(tip, check.Head.Tip.Sha);
    }

    // ── Cancellation and timeout of a running transfer ──

    [Fact]
    public async Task Cancelling_during_the_transfer_aborts_the_fetch()
    {
        var work = CloneRemote("work");
        Publish("big.txt", "payload", commits: 20);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        var result = await Open(work).FetchAsync(new FetchOptions
        {
            CancellationToken = cts.Token,
            OnProgress = _ => { if (Interlocked.Increment(ref calls) == 1) cts.Cancel(); }
        });

        Assert.True(calls > 0, "no progress callback fired");
        var error = Assert.IsType<UnavailableError>(result.Error);
        Assert.Equal(GitErrors.CancelledCode, error.Code);
        using var repo = new Repository(work);
        Assert.NotEqual(RemoteTip(), repo.Branches[$"origin/{_branch}"].Tip.Sha);
    }

    [Fact]
    public async Task A_transfer_running_past_the_timeout_returns_timeout()
    {
        var work = CloneRemote("work");
        Publish("big.txt", "payload", commits: 20);
        var calls = 0;

        var result = await Open(work, timeoutSeconds: 1).FetchAsync(new FetchOptions
        {
            // A slow transfer: the first progress callback outlasts the 1 s timeout.
            OnProgress = _ => { if (Interlocked.Increment(ref calls) == 1) Thread.Sleep(1500); }
        });

        var error = Assert.IsType<TimeoutError>(result.Error);
        Assert.Equal(GitErrors.TimeoutCode, error.Code);
    }

    // ── Clone and bare repositories ──

    [Fact]
    public async Task Clone_reports_progress()
    {
        var target = Path.Combine(_root, "fresh");
        var stages = new List<string>();

        var result = await Open(target).CloneAsync(new CL.GitHelper.Models.CloneOptions
        {
            OnProgress = p => { lock (stages) stages.Add(p.Stage); }
        });

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEmpty(stages);
    }

    [Fact]
    public async Task A_bare_repository_counts_as_cloned()
    {
        var bare = Path.Combine(_root, "mirror.git");
        Repository.Clone(_remote, bare, new CloneOptions { IsBare = true });
        var repository = Open(bare);

        var info = await repository.GetRepositoryInfoAsync();
        Assert.True(info.IsSuccess, info.Error?.Message);
        Assert.True(info.Value!.IsBare);

        // Used to try a clone into the non-empty directory and fail with directory_not_empty.
        var update = await repository.EnsureUpToDateAsync();
        Assert.True(update.IsSuccess, update.Error?.Message);
        Assert.True(GitRepository.IsCloned(bare));
    }

    [Fact]
    public async Task Repository_info_redacts_credentials_in_the_remote_url()
    {
        var work = CloneRemote("work");
        using (var repo = new Repository(work))
            repo.Network.Remotes.Update("origin", r => r.Url = "https://user:ghp_leak@github.com/org/repo.git");

        var info = await Open(work).GetRepositoryInfoAsync();

        Assert.True(info.IsSuccess, info.Error?.Message);
        Assert.Equal("https://***@github.com/org/repo.git", info.Value!.RemoteUrl);
    }

    // ── Background auto-fetch ──

    [Fact]
    public async Task Auto_fetch_timers_follow_the_configuration()
    {
        var work = CloneRemote("work");
        using var manager = new GitManager(new GitHelperConfig
        {
            Repositories =
            [
                new RepositoryConfiguration { Id = "Work", RepositoryUrl = _remote, LocalPath = work, AutoFetchIntervalMinutes = 5 },
                new RepositoryConfiguration { Id = "Quiet", RepositoryUrl = _remote, LocalPath = work }
            ]
        }, _root);

        Assert.Empty(manager.AutoFetchSchedule);   // nothing runs before start

        manager.StartAutoFetch();
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(manager.AutoFetchSchedule).Value);

        await manager.ApplyConfigurationAsync(new GitHelperConfig
        {
            Repositories = [new RepositoryConfiguration { Id = "Work", RepositoryUrl = _remote, LocalPath = work, AutoFetchIntervalMinutes = 0 }]
        }, _root);
        Assert.Empty(manager.AutoFetchSchedule);
    }

    [Fact]
    public async Task An_auto_fetch_tick_fetches_the_repository()
    {
        var work = CloneRemote("work");
        var tip = Publish("r.txt", "remote");
        using var manager = new GitManager(new GitHelperConfig
        {
            Repositories = [new RepositoryConfiguration { Id = "Work", RepositoryUrl = _remote, LocalPath = work, AutoFetchIntervalMinutes = 1 }]
        }, _root);

        await manager.AutoFetchAsync("Work");

        using var repo = new Repository(work);
        Assert.Equal(tip, repo.Branches[$"origin/{_branch}"].Tip.Sha);
    }
}
