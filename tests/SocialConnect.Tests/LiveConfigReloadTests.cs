using System.Text.Json.Nodes;
using CL.SocialConnect;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace SocialConnect.Tests;

/// <summary>
/// Live config changes (IConfigReloadable) through a real <see cref="LibraryManager"/>. Offline:
/// the services only create HTTP clients, so swapping them is observable without the network.
/// </summary>
public sealed class LiveConfigReloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_social_reload_" + Guid.NewGuid().ToString("N"));
    private LibraryManager _manager = null!;
    private SocialConnectLibrary _social = null!;
    private string _configPath = null!;

    public async Task InitializeAsync()
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.SocialConnect");
        Directory.CreateDirectory(libDir);
        _configPath = Path.Combine(libDir, "config.socialconnect.json");
        await File.WriteAllTextAsync(_configPath, """
        {
          "enabled": true,
          "discord": { "enabled": true, "timeoutSeconds": 10 },
          "steam": { "enabled": true, "apiKey": "key-one", "authEnabled": false }
        }
        """);

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<SocialConnectLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _social = _manager.GetLibrary<SocialConnectLibrary>()!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* logger may still hold its file */ }
    }

    private JsonNode Config() => JsonNode.Parse(File.ReadAllText(_configPath))!;

    private Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateAsync(JsonNode config) =>
        _manager.UpdateConfigAsync("CL.SocialConnect", "socialconnect", config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Steam_change_rebuilds_steam_and_leaves_discord()
    {
        var discord = _social.Discord;
        var steam = _social.Steam;

        var config = Config();
        config["steam"]!["apiKey"] = "key-two";
        config["steam"]!["authEnabled"] = true;
        config["steam"]!["appId"] = "480";
        var result = await UpdateAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.NotSame(steam, _social.Steam);
        Assert.True(_social.HasSteamAuth);
        Assert.Same(discord, _social.Discord);
    }

    [Fact]
    public async Task Steam_sign_in_is_on_with_steam_and_switches_live()
    {
        Assert.True(_social.HasSteamOpenId);
        var openId = _social.OpenId;

        var config = Config();
        config["steam"]!["openIdEnabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.False(_social.HasSteamOpenId);
        Assert.Throws<InvalidOperationException>(() => _social.OpenId);
        Assert.True(_social.HasSteam);

        config["steam"]!["openIdEnabled"] = true;
        config["steam"]!["timeoutSeconds"] = 20;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.True(_social.HasSteamOpenId);
        Assert.NotSame(openId, _social.OpenId);
    }

    [Fact]
    public async Task Steam_sign_in_works_without_an_api_key()
    {
        var config = Config();
        config["steam"]!["apiKey"] = "";
        var result = await UpdateAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.True(_social.HasSteamOpenId);
        Assert.False(_social.HasSteam);
        Assert.False(_social.HasSteamAuth);
    }

    [Fact]
    public async Task Steam_without_a_key_and_without_sign_in_is_invalid()
    {
        var config = Config();
        config["steam"]!["apiKey"] = "";
        config["steam"]!["openIdEnabled"] = false;
        var result = await UpdateAsync(config);

        Assert.False(result.Applied);
        Assert.True(_social.HasSteam);
    }

    [Fact]
    public async Task Non_discord_default_webhook_url_is_refused()
    {
        var discord = _social.Discord;
        var config = Config();
        config["discord"]!["defaultWebhookUrl"] = "http://169.254.169.254/latest/meta-data";
        var result = await UpdateAsync(config);

        Assert.False(result.Applied);
        Assert.Same(discord, _social.Discord);
    }

    [Fact]
    public async Task Invalid_config_reaching_the_library_is_ignored()
    {
        // The framework validates before applying; the library re-checks so a direct call cannot apply it either.
        var context = _manager.GetLoadedLibraries().Single(l => l.Instance is SocialConnectLibrary).Context!;
        var steam = _social.Steam;
        var invalid = new CL.SocialConnect.Models.SocialConnectConfig();
        invalid.Steam.Enabled = true;
        invalid.Steam.CacheTtlSeconds = 0;

        await _social.OnConfigChangedAsync(context, new CodeLogic.Core.Configuration.ConfigChangedEventArgs
        {
            SectionName = "socialconnect",
            Kind = CodeLogic.Core.Configuration.ConfigChangeKind.Updated,
            NewValue = invalid
        }, CancellationToken.None);

        Assert.Same(steam, _social.Steam);
    }

    [Fact]
    public async Task Discord_change_rebuilds_discord_and_leaves_steam()
    {
        var discord = _social.Discord;
        var steam = _social.Steam;

        var config = Config();
        config["discord"]!["timeoutSeconds"] = 20;
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.NotSame(discord, _social.Discord);
        Assert.Same(steam, _social.Steam);
    }

    [Fact]
    public async Task Services_and_library_switch_on_and_off_live()
    {
        var config = Config();
        config["discord"]!["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.False(_social.HasDiscord);
        Assert.True(_social.HasSteam);

        config["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.False(_social.HasSteam);
        Assert.False(_social.HasSteamOpenId);

        config["enabled"] = true;
        config["discord"]!["enabled"] = true;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.True(_social.HasDiscord);
        Assert.True(_social.HasSteam);
    }

    [Fact]
    public async Task Health_is_healthy_with_every_service_switched_off()
    {
        Assert.Equal("Active services: Discord, Steam, SteamOpenId", (await _social.HealthCheckAsync()).Message);

        var config = Config();
        config["discord"]!["enabled"] = false;
        config["steam"]!["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);

        var health = await _social.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.Equal("No services enabled", health.Message);
    }
}
