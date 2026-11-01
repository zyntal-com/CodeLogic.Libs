using System.Text.Json.Nodes;
using CL.GitHelper;
using CL.GitHelper.Models;
using CodeLogic.Core.Configuration;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace GitHelper.Tests;

/// <summary>
/// Live config changes (IConfigReloadable) through a real <see cref="LibraryManager"/>. Offline:
/// repository instances are created lazily and cached, so instance identity shows what was replaced.
/// </summary>
public sealed class LiveConfigReloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_git_reload_" + Guid.NewGuid().ToString("N"));
    private LibraryManager _manager = null!;
    private GitHelperLibrary _git = null!;
    private string _configPath = null!;

    public async Task InitializeAsync()
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.GitHelper");
        Directory.CreateDirectory(libDir);
        _configPath = Path.Combine(libDir, "config.githelper.json");
        var config = new JsonObject
        {
            ["enabled"] = true,
            ["repositories"] = new JsonArray(Repository("App"), Repository("Docs"))
        };
        await File.WriteAllTextAsync(_configPath, config.ToJsonString());

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<GitHelperLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _git = _manager.GetLibrary<GitHelperLibrary>()!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* logger may still hold its file */ }
    }

    private static JsonObject Repository(string id) => new()
    {
        ["id"] = id,
        ["name"] = id,
        ["repositoryUrl"] = $"https://example.invalid/{id}.git",
        ["localPath"] = id.ToLowerInvariant(),
        ["defaultBranch"] = "main"
    };

    private JsonNode Config() => JsonNode.Parse(File.ReadAllText(_configPath))!;

    private static JsonNode Find(JsonNode config, string id) =>
        config["repositories"]!.AsArray().First(r => (string?)r!["id"] == id)!;

    private Task<ConfigApplyResult> UpdateAsync(JsonNode config) =>
        _manager.UpdateConfigAsync("CL.GitHelper", "githelper", config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Changing_one_repository_keeps_the_other_instance()
    {
        var app = await _git.GetRepositoryAsync("App");
        var docs = await _git.GetRepositoryAsync("Docs");

        var config = Config();
        Find(config, "Docs")["defaultBranch"] = "develop";
        var result = await UpdateAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.Same(app, await _git.GetRepositoryAsync("App"));
        Assert.NotSame(docs, await _git.GetRepositoryAsync("Docs"));
        Assert.Equal("develop", _git.GetManager().GetConfiguration("Docs")!.DefaultBranch);
    }

    [Fact]
    public async Task Repositories_can_be_added_and_removed()
    {
        var app = await _git.GetRepositoryAsync("App");

        var config = Config();
        var repositories = config["repositories"]!.AsArray();
        repositories.Remove(Find(config, "Docs"));
        repositories.Add(Repository("Site"));
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.Equal(["App", "Site"], _git.GetManager().GetRepositoryIds().Order());
        Assert.Same(app, await _git.GetRepositoryAsync("App"));
        await Assert.ThrowsAsync<ArgumentException>(() => _git.GetRepositoryAsync("Docs"));
    }

    [Fact]
    public async Task Runtime_registered_repositories_survive_a_change_and_stay_out_of_the_file()
    {
        _git.RegisterRepository(new RepositoryConfiguration
        {
            Id = "Runtime",
            RepositoryUrl = "https://example.invalid/runtime.git",
            LocalPath = "runtime"
        });

        var config = Config();
        config["maxConcurrentOperations"] = 5;
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.Contains("Runtime", _git.GetManager().GetRepositoryIds());
        Assert.DoesNotContain("Runtime", File.ReadAllText(_configPath));
    }

    [Fact]
    public async Task Base_directory_change_replaces_every_cached_instance()
    {
        var app = await _git.GetRepositoryAsync("App");

        var config = Config();
        config["baseDirectory"] = Path.Combine(_root, "elsewhere");
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.NotSame(app, await _git.GetRepositoryAsync("App"));
        Assert.True(Directory.Exists(Path.Combine(_root, "elsewhere")));
    }

    [Fact]
    public async Task Library_can_be_disabled_and_enabled_live()
    {
        var config = Config();
        config["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.Throws<InvalidOperationException>(() => _git.GetManager());

        config["enabled"] = true;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.Equal(["App", "Docs"], _git.GetManager().GetRepositoryIds().Order());
    }

    [Fact]
    public async Task A_disabled_library_reports_healthy_disabled_not_unhealthy()
    {
        var config = Config();
        config["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);

        var health = await _git.HealthCheckAsync();

        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.Contains("disabled", health.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_invalid_config_change_is_rejected_and_the_current_settings_stay()
    {
        var context = new LibraryContext
        {
            LibraryId = "CL.GitHelper",
            LibraryDirectory = _root,
            ConfigDirectory = _root,
            LocalizationDirectory = _root,
            LogsDirectory = _root,
            DataDirectory = _root,
            Logger = CodeLogic.Core.Logging.NullLogger.Instance,
            Configuration = new ConfigurationManager(_root),
            Localization = new CodeLogic.Core.Localization.LocalizationManager(_root),
            Events = new EventBus()
        };
        var invalid = new GitHelperConfig
        {
            Enabled = false,    // would disable the library if the change were applied
            Repositories =
            [
                new RepositoryConfiguration { Id = "Dup", RepositoryUrl = "https://example.invalid/a.git", LocalPath = "a" },
                new RepositoryConfiguration { Id = "Dup", RepositoryUrl = "https://example.invalid/b.git", LocalPath = "b" }
            ]
        };

        await _git.OnConfigChangedAsync(context, new ConfigChangedEventArgs
        {
            SectionName = "githelper",
            Kind = ConfigChangeKind.Updated,
            NewValue = invalid
        }, CancellationToken.None);

        Assert.Equal(["App", "Docs"], _git.GetManager().GetRepositoryIds().Order());
    }
}
