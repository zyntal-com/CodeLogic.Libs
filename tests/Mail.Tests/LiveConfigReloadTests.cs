using System.Text.Json.Nodes;
using CL.Mail;
using CodeLogic.Core.Events;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Mail.Tests;

/// <summary>
/// Live config changes (IConfigReloadable) through a real <see cref="LibraryManager"/>. Nothing
/// connects to a server: services are built lazily, so swapping them is observable offline.
/// </summary>
public sealed class LiveConfigReloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cl_mail_reload_" + Guid.NewGuid().ToString("N"));
    private LibraryManager _manager = null!;
    private MailLibrary _mail = null!;
    private string _configPath = null!;

    public async Task InitializeAsync()
    {
        var libDir = Path.Combine(_root, "Libraries", "CL.Mail");
        Directory.CreateDirectory(libDir);
        _configPath = Path.Combine(libDir, "config.mail.json");
        await File.WriteAllTextAsync(_configPath, """
        {
          "enabled": true,
          "smtp": { "host": "smtp.one.test", "port": 587 },
          "templateDirectory": "templates"
        }
        """);

        _manager = new LibraryManager(new EventBus()) { FrameworkRootPath = _root };
        await _manager.LoadLibraryAsync<MailLibrary>();
        await _manager.ConfigureAllAsync();
        await _manager.InitializeAllAsync();
        await _manager.StartAllAsync();
        _mail = _manager.GetLibrary<MailLibrary>()!;
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAllAsync();
        _manager.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* logger may still hold its file */ }
    }

    private JsonNode Config() => JsonNode.Parse(File.ReadAllText(_configPath))!;

    private Task<CodeLogic.Core.Configuration.ConfigApplyResult> UpdateAsync(JsonNode config) =>
        _manager.UpdateConfigAsync("CL.Mail", "mail", config.ToJsonString(), changedBy: "test");

    [Fact]
    public async Task Smtp_change_swaps_only_the_smtp_service()
    {
        var smtp = _mail.Smtp;
        var templates = _mail.TemplateProvider;

        var config = Config();
        config["smtp"]!["host"] = "smtp.two.test";
        var result = await UpdateAsync(config);

        Assert.True(result.Applied, result.ToString());
        Assert.NotSame(smtp, _mail.Smtp);
        Assert.Same(templates, _mail.TemplateProvider);
    }

    [Fact]
    public async Task Unrelated_change_keeps_every_service()
    {
        var smtp = _mail.Smtp;
        var templates = _mail.TemplateProvider;

        var config = Config();
        config["defaultFromName"] = "Support";
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.Same(smtp, _mail.Smtp);
        Assert.Same(templates, _mail.TemplateProvider);
        Assert.False(_mail.HasImap);
    }

    [Fact]
    public async Task Imap_and_template_directory_changes_rebuild_their_services()
    {
        var smtp = _mail.Smtp;
        var templates = _mail.TemplateProvider;

        var config = Config();
        config["imap"] = new JsonObject { ["host"] = "imap.one.test" };
        config["templateDirectory"] = "other-templates";
        Assert.True((await UpdateAsync(config)).Applied);

        Assert.True(_mail.HasImap);
        Assert.NotSame(templates, _mail.TemplateProvider);
        Assert.Same(smtp, _mail.Smtp);

        var imap = _mail.Imap;
        config["imap"]!["host"] = "imap.two.test";
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.NotSame(imap, _mail.Imap);

        config.AsObject().Remove("imap");
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.False(_mail.HasImap);
    }

    [Fact]
    public async Task CreateMessage_uses_the_configured_default_sender()
    {
        var config = Config();
        config["defaultFromEmail"] = "noreply@example.com";
        config["defaultFromName"] = "Example";
        Assert.True((await UpdateAsync(config)).Applied);

        var message = _mail.CreateMessage().To("r@example.com").Subject("s").TextBody("b").Build();
        Assert.Equal("noreply@example.com", message.From);
        Assert.Equal("Example", message.FromName);
    }

    [Fact]
    public async Task Invalid_config_reaching_the_library_is_ignored()
    {
        // The framework validates before applying; the library re-checks so a direct call cannot apply it either.
        var context = _manager.GetLoadedLibraries().Single(l => l.Instance is MailLibrary).Context!;
        var smtp = _mail.Smtp;
        var invalid = new CL.Mail.Models.MailConfig { Smtp = { Host = "" } };

        await _mail.OnConfigChangedAsync(context, new CodeLogic.Core.Configuration.ConfigChangedEventArgs
        {
            SectionName = "mail",
            Kind = CodeLogic.Core.Configuration.ConfigChangeKind.Updated,
            NewValue = invalid
        }, CancellationToken.None);

        Assert.Same(smtp, _mail.Smtp);
    }

    [Fact]
    public async Task Library_can_be_disabled_and_enabled_live()
    {
        var config = Config();
        config["enabled"] = false;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.Throws<InvalidOperationException>(() => _mail.Smtp);

        config["enabled"] = true;
        Assert.True((await UpdateAsync(config)).Applied);
        Assert.NotNull(_mail.Smtp);
        Assert.NotNull(_mail.TemplateEngine);
    }
}
