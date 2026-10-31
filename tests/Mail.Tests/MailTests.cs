using CL.Mail;
using CL.Mail.Models;
using CL.Mail.Services;
using CodeLogic;                     // Libraries, CodeLogicOptions
using CodeLogic.Core.Results;
using Xunit;

namespace Mail.Tests;

// ── CL.Mail tests ───────────────────────────────────────────────────────────────
// HYBRID strategy:
//   • The template engine (SimpleTemplateEngine) and the fluent MailBuilder are pure,
//     offline, in-memory components — they are exercised directly with no runtime boot.
//   • Actually sending mail requires a live SMTP server, so that single test is
//     env-gated and SKIPS unless CL_MAIL_TEST_SMTP_* variables are present.

// ── SimpleTemplateEngine (offline, direct instantiation) ─────────────────────────

public sealed class SimpleTemplateEngineTests
{
    private static readonly SimpleTemplateEngine Engine = new();

    private static async Task<RenderedTemplate> Render(
        MailTemplate template,
        Dictionary<string, object?>? variables = null)
    {
        var result = await Engine.RenderAsync(template, variables ?? [], CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Null(result.Error);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    [Fact]
    public async Task Variable_substitution_replaces_placeholder()
    {
        var template = MailTemplate.Create(
            id: "welcome",
            subject: "Hi {{name}}",
            textBody: "Hello {{name}}, welcome to {{site}}!");

        var rendered = await Render(template, new() { ["name"] = "Alice", ["site"] = "CodeLogic" });

        Assert.Equal("Hi Alice", rendered.Subject);
        Assert.Equal("Hello Alice, welcome to CodeLogic!", rendered.TextBody);
    }

    [Fact]
    public async Task Subject_and_both_bodies_are_rendered()
    {
        var template = MailTemplate.Create(
            id: "all",
            subject: "Order {{id}}",
            textBody: "Text for {{id}}",
            htmlBody: "<p>HTML for {{id}}</p>");

        var rendered = await Render(template, new() { ["id"] = "42" });

        Assert.Equal("Order 42", rendered.Subject);
        Assert.Equal("Text for 42", rendered.TextBody);
        Assert.Equal("<p>HTML for 42</p>", rendered.HtmlBody);
    }

    [Fact]
    public async Task Layout_receives_named_sections_and_the_remaining_body()
    {
        var layout = MailTemplate.Create("layout", "ignored",
            textBody: "<{{header}}>|{{body}}|<{{footer}}>",
            htmlBody: "<h1>{{header}}</h1>{{body}}");
        var child = MailTemplate.Create("child", "Hi {{name}}",
            textBody: "{{#section header}}\nTop {{name}}\n{{/section}}Main {{#if vip}}VIP{{#else}}std{{/if}}{{#each items}}[{{n}}]{{/each}}{{#section footer}}Bye{{/section}}",
            htmlBody: "{{#section header}}<b>{{name}}</b>{{/section}}<p>{{name}}</p>") with { Layout = "layout" };
        var engine = new SimpleTemplateEngine(new SingleLayoutProvider(layout));

        var result = await engine.RenderAsync(child, new()
        {
            ["name"] = "A&B",
            ["vip"] = true,
            ["items"] = new[] { new Dictionary<string, object?> { ["n"] = 1 }, new Dictionary<string, object?> { ["n"] = 2 } }
        });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal("Hi A&B", result.Value!.Subject);
        Assert.Equal("<Top A&B>|Main VIP[1][2]|<Bye>", result.Value.TextBody);
        Assert.Equal("<h1><b>A&amp;B</b></h1><p>A&amp;B</p>", result.Value.HtmlBody);
    }

    private sealed class SingleLayoutProvider(MailTemplate layout) : IMailTemplateProvider
    {
        public Task<Result<MailTemplate>> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(templateId == layout.Id
                ? Result<MailTemplate>.Success(layout)
                : Result<MailTemplate>.Failure(Error.NotFound(MailErrorCodes.TemplateNotFound, templateId)));
        public Task<Result<IReadOnlyList<string>>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Success([layout.Id]));
        public Task<Result> SaveTemplateAsync(MailTemplate template, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task If_block_includes_content_when_flag_is_truthy()
    {
        var template = MailTemplate.Create("if-true", "s", textBody: "[{{#if flag}}YES{{/if}}]");

        var rendered = await Render(template, new() { ["flag"] = true });

        Assert.Equal("[YES]", rendered.TextBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData("")]
    [InlineData("false")]
    public async Task If_block_excludes_content_when_flag_is_falsy(object? falsy)
    {
        var template = MailTemplate.Create("if-false", "s", textBody: "[{{#if flag}}YES{{/if}}]");

        var rendered = await Render(template, new() { ["flag"] = falsy });

        Assert.Equal("[]", rendered.TextBody);
    }

    [Fact]
    public async Task If_else_picks_the_true_branch()
    {
        var template = MailTemplate.Create("ifelse", "s", textBody: "{{#if flag}}A{{#else}}B{{/if}}");

        var rendered = await Render(template, new() { ["flag"] = true });

        Assert.Equal("A", rendered.TextBody);
    }

    [Fact]
    public async Task If_else_picks_the_false_branch()
    {
        var template = MailTemplate.Create("ifelse", "s", textBody: "{{#if flag}}A{{#else}}B{{/if}}");

        var rendered = await Render(template, new() { ["flag"] = false });

        Assert.Equal("B", rendered.TextBody);
    }

    [Fact]
    public async Task Each_block_repeats_per_item()
    {
        var template = MailTemplate.Create("each", "s", textBody: "{{#each items}}[{{name}}]{{/each}}");

        var items = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "one" },
            new() { ["name"] = "two" },
            new() { ["name"] = "three" },
        };

        var rendered = await Render(template, new() { ["items"] = items });

        Assert.Equal("[one][two][three]", rendered.TextBody);
    }

    [Fact]
    public async Task Each_block_over_simple_values_renders_nothing_for_empty_list()
    {
        var template = MailTemplate.Create("each-empty", "s", textBody: "start{{#each items}}X{{/each}}end");

        var rendered = await Render(template, new() { ["items"] = new List<string>() });

        Assert.Equal("startend", rendered.TextBody);
    }

    [Fact]
    public void Engine_reports_its_name()
    {
        Assert.Equal("Simple", new SimpleTemplateEngine().Name);
    }
}

// ── MailBuilder (offline, direct instantiation) ──────────────────────────────────

// ── SimpleTemplateEngine: HTML encoding, raw values, single-pass substitution, layouts ──
public sealed class SimpleTemplateEngineEncodingTests
{
    private static async Task<RenderedTemplate> Render(SimpleTemplateEngine engine, MailTemplate template, Dictionary<string, object?> variables)
    {
        var result = await engine.RenderAsync(template, variables, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value!;
    }

    private static Task<RenderedTemplate> Render(MailTemplate template, Dictionary<string, object?> variables) =>
        Render(new SimpleTemplateEngine(), template, variables);

    [Fact]
    public async Task Html_body_encodes_values_in_every_syntax()
    {
        var template = MailTemplate.Create("enc", "s", htmlBody: "<p>{{a}}|${a}|{a}</p>");
        var rendered = await Render(template, new() { ["a"] = "<script>x</script>&\"" });
        Assert.Equal("<p>&lt;script&gt;x&lt;/script&gt;&amp;&quot;|&lt;script&gt;x&lt;/script&gt;&amp;&quot;|&lt;script&gt;x&lt;/script&gt;&amp;&quot;</p>", rendered.HtmlBody);
    }

    [Fact]
    public async Task Subject_and_text_body_are_not_encoded()
    {
        var template = MailTemplate.Create("plain", "Re: {{a}}", textBody: "Hi {{a}}");
        var rendered = await Render(template, new() { ["a"] = "Tom & <Jerry>" });
        Assert.Equal("Re: Tom & <Jerry>", rendered.Subject);
        Assert.Equal("Hi Tom & <Jerry>", rendered.TextBody);
    }

    [Fact]
    public async Task Triple_braces_insert_trusted_html_raw()
    {
        var template = MailTemplate.Create("raw", "s", htmlBody: "<div>{{{block}}}</div>");
        var rendered = await Render(template, new() { ["block"] = "<b>bold</b>" });
        Assert.Equal("<div><b>bold</b></div>", rendered.HtmlBody);
    }

    [Fact]
    public async Task Placeholders_inside_values_are_not_expanded()
    {
        var template = MailTemplate.Create("once", "{{a}}", textBody: "{{a}} / {{b}}", htmlBody: "{{a}}");
        var rendered = await Render(template, new() { ["a"] = "${b} {b} {{b}} {{{b}}}", ["b"] = "SECRET" });
        Assert.Equal("${b} {b} {{b}} {{{b}}}", rendered.Subject);
        Assert.Equal("${b} {b} {{b}} {{{b}}} / SECRET", rendered.TextBody);
        Assert.DoesNotContain("SECRET", rendered.HtmlBody);
    }

    [Fact]
    public async Task Loop_values_are_encoded_and_not_expanded_again()
    {
        var template = MailTemplate.Create("loop", "s", htmlBody: "{{#each items}}<li>{{name}}</li>{{/each}}");
        var items = new List<Dictionary<string, object?>> { new() { ["name"] = "<i>x</i>" }, new() { ["name"] = "{token}" } };
        var rendered = await Render(template, new() { ["items"] = items, ["token"] = "SECRET" });
        Assert.Equal("<li>&lt;i&gt;x&lt;/i&gt;</li><li>{token}</li>", rendered.HtmlBody);
    }

    [Fact]
    public async Task Unknown_placeholders_are_left_as_they_are()
    {
        var template = MailTemplate.Create("unknown", "s", textBody: "{{missing}} {{{missing}}} {nope}");
        var rendered = await Render(template, new());
        Assert.Equal("{{missing}} {{{missing}}} {nope}", rendered.TextBody);
    }

    [Fact]
    public async Task Layout_gets_the_child_sections_raw_and_its_own_values_encoded()
    {
        var layout = new MailTemplate
        {
            Id = "shell",
            Subject = "",
            TextBody = "TEXT[{{body}}] {{site}}",
            HtmlBody = "<html><h1>{{site}}</h1>{{body}}<footer>{{footer}}</footer></html>"
        };
        var engine = new SimpleTemplateEngine(new MemoryTemplates(layout));
        var child = new MailTemplate
        {
            Id = "child",
            Subject = "s",
            Layout = "shell",
            TextBody = "Hello {{name}}",
            HtmlBody = "<p>Hello {{name}}</p>{{#section footer}}<small>bye</small>{{/section}}"
        };

        var rendered = await Render(engine, child, new() { ["name"] = "<Ann>", ["site"] = "A&B" });

        Assert.Equal("<html><h1>A&amp;B</h1><p>Hello &lt;Ann&gt;</p><footer><small>bye</small></footer></html>", rendered.HtmlBody);
        Assert.Equal("TEXT[Hello <Ann>] A&B", rendered.TextBody);
    }

    private sealed class MemoryTemplates(params MailTemplate[] templates) : IMailTemplateProvider
    {
        public Task<Result<MailTemplate>> LoadTemplateAsync(string templateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(templates.FirstOrDefault(t => t.Id == templateId) is { } template
                ? Result<MailTemplate>.Success(template)
                : Result<MailTemplate>.Failure(Error.NotFound(MailErrorCodes.TemplateNotFound, $"Template '{templateId}' not found")));

        public Task<Result<IReadOnlyList<string>>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<string>>.Success(templates.Select(t => t.Id).ToList()));

        public Task<Result> SaveTemplateAsync(MailTemplate template, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(Error.Validation(MailErrorCodes.Unknown, "read-only")));
    }
}

public sealed class MailBuilderTests
{
    [Fact]
    public void Build_sets_all_supplied_fields()
    {
        var message = new MailBuilder()
            .From("sender@example.com")
            .To("recipient@example.com")
            .Subject("Hello")
            .TextBody("Body text")
            .Build();

        Assert.Equal("sender@example.com", message.From);
        Assert.Equal("Hello", message.Subject);
        Assert.Equal("Body text", message.TextBody);
        Assert.Single(message.To);
        Assert.Equal("recipient@example.com", message.To[0]);
    }

    [Fact]
    public void To_accumulates_into_a_list()
    {
        var message = new MailBuilder()
            .From("s@example.com")
            .To("a@example.com")
            .To("b@example.com")
            .Subject("multi")
            .TextBody("x")
            .Build();

        Assert.Equal(2, message.To.Count);
        Assert.Contains("a@example.com", message.To);
        Assert.Contains("b@example.com", message.To);
    }

    [Fact]
    public void Build_throws_when_from_missing()
    {
        var builder = new MailBuilder()
            .To("r@example.com")
            .Subject("s")
            .TextBody("b");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_throws_when_no_recipient()
    {
        var builder = new MailBuilder()
            .From("s@example.com")
            .Subject("s")
            .TextBody("b");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_throws_when_subject_missing()
    {
        var builder = new MailBuilder()
            .From("s@example.com")
            .To("r@example.com")
            .TextBody("b");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_throws_when_no_body()
    {
        var builder = new MailBuilder()
            .From("s@example.com")
            .To("r@example.com")
            .Subject("s");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void HtmlBody_alone_satisfies_the_body_requirement()
    {
        var message = new MailBuilder()
            .From("s@example.com")
            .To("r@example.com")
            .Subject("s")
            .HtmlBody("<p>hi</p>")
            .Build();

        Assert.Equal("<p>hi</p>", message.HtmlBody);
        Assert.Null(message.TextBody);
    }
}

// ── Error codes and kinds (offline: validation runs before any connection) ───────

public sealed class MailErrorTests
{
    private static SmtpService Smtp(string host = "127.0.0.1", int port = 1) =>
        new(new SmtpConfig { Host = host, Port = port, SecurityMode = MailSecurityMode.None, TimeoutSeconds = 5 });

    private static MailMessage Message(
        string from = "sender@example.com",
        string[]? to = null,
        string subject = "Hi",
        string? text = "Body",
        string[]? attachments = null) => new()
    {
        From = from,
        To = to ?? ["to@example.com"],
        Subject = subject,
        TextBody = text,
        Attachments = attachments ?? []
    };

    private static async Task<Error> SendFailure(MailMessage message, SmtpService? smtp = null)
    {
        var result = await (smtp ?? Smtp()).SendAsync(message, CancellationToken.None);
        Assert.True(result.IsFailure);
        return result.Error!;
    }

    [Fact]
    public async Task Empty_recipient_list_is_a_validation_error()
    {
        var error = await SendFailure(Message(to: []));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidRecipient, error.Code);
    }

    [Fact]
    public async Task Malformed_recipient_is_a_validation_error()
    {
        var error = await SendFailure(Message(to: ["not-an-address"]));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidRecipient, error.Code);
    }

    [Fact]
    public async Task Missing_sender_is_a_validation_error()
    {
        var error = await SendFailure(Message(from: " "));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidSender, error.Code);
    }

    [Fact]
    public async Task Missing_subject_is_a_validation_error()
    {
        var error = await SendFailure(Message(subject: ""));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidSubject, error.Code);
    }

    [Theory]
    [InlineData("Subject line\r\nBcc: victim@example.com")]
    [InlineData("Subject\nline")]
    [InlineData("Subject\rline")]
    public async Task Subject_with_a_line_break_is_a_validation_error(string subject)
    {
        var error = await SendFailure(Message(subject: subject));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidSubject, error.Code);
    }

    [Theory]
    [InlineData("Alice\r\nBcc: victim@example.com")]
    [InlineData("Alice\nSmith")]
    public async Task FromName_with_a_line_break_is_a_validation_error(string fromName)
    {
        var error = await SendFailure(Message() with { FromName = fromName });
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidSender, error.Code);
    }

    [Theory]
    [InlineData("X-Bad:Name", "v")]
    [InlineData("X Bad", "v")]
    [InlineData("", "v")]
    [InlineData("X-Ünicode", "v")]
    [InlineData("X-Tab\t", "v")]
    [InlineData("X-Fine", "line\r\nBcc: victim@example.com")]
    [InlineData("X-Fine", "line\nbreak")]
    [InlineData("X-Fine", "nul\0")]
    [InlineData("From", "attacker@example.com")]
    [InlineData("bcc", "hidden@example.com")]
    [InlineData("Content-Type", "text/plain")]
    public async Task Invalid_custom_header_is_a_validation_error(string name, string value)
    {
        var error = await SendFailure(Message() with { Headers = new Dictionary<string, string> { [name] = value } });
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidHeader, error.Code);
    }

    [Fact]
    public async Task Valid_custom_headers_pass_validation_and_fail_only_at_the_connection()
    {
        var error = await SendFailure(Message() with
        {
            FromName = "Alice Ünicode",
            Subject = "Grüße",
            Headers = new Dictionary<string, string>
            {
                ["X-Campaign"] = "autumn",
                ["Reply-To"] = "reply@example.com",
                ["List-Unsubscribe"] = "<mailto:u@example.com>",
                ["X-Note"] = "tabs\tand ünicode are fine"
            }
        });

        Assert.Equal(MailErrorCodes.SmtpConnectionFailed, error.Code);
    }

    [Fact]
    public async Task Missing_body_is_a_validation_error()
    {
        var error = await SendFailure(Message(text: null));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.InvalidBody, error.Code);
    }

    [Fact]
    public async Task Missing_attachment_file_is_not_found()
    {
        var path = Path.Combine(Path.GetTempPath(), "cl_mail_missing_" + Guid.NewGuid().ToString("N") + ".pdf");
        var error = await SendFailure(Message(attachments: [path]));
        Assert.IsType<NotFoundError>(error);
        Assert.Equal(MailErrorCodes.AttachmentNotFound, error.Code);
    }

    [Fact]
    public async Task Missing_smtp_host_is_a_config_validation_error()
    {
        var error = await SendFailure(Message(), Smtp(host: ""));
        Assert.IsType<ValidationError>(error);
        Assert.Equal(MailErrorCodes.SmtpConfigInvalid, error.Code);
    }

    [Fact]
    public async Task Unreachable_smtp_server_is_unavailable()
    {
        // Port 1 on loopback: nothing listens there, so the connect is refused.
        var error = await SendFailure(Message(), Smtp("127.0.0.1", 1));
        Assert.IsType<UnavailableError>(error);
        Assert.Equal(MailErrorCodes.SmtpConnectionFailed, error.Code);
    }

    [Fact]
    public async Task Cancelled_send_reports_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await Smtp().SendAsync(Message(), cts.Token);
        Assert.Equal(MailErrorCodes.Cancelled, result.Error?.Code);
    }

    [Fact]
    public async Task Missing_template_is_not_found()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cl_mail_tpl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileMailTemplateProvider(dir);
            var result = await provider.LoadTemplateAsync("nope");
            Assert.True(result.IsFailure);
            Assert.IsType<NotFoundError>(result.Error);
            Assert.Equal(MailErrorCodes.TemplateNotFound, result.Error!.Code);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Saved_template_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cl_mail_tpl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new FileMailTemplateProvider(dir);
            var save = await provider.SaveTemplateAsync(MailTemplate.Create("welcome", "Hi {{name}}", textBody: "Hello"));
            Assert.True(save.IsSuccess, save.Error?.ToString());

            var list = await provider.ListTemplatesAsync();
            Assert.Equal(["welcome"], list.Value!);

            var load = await provider.LoadTemplateAsync("welcome");
            Assert.Equal("Hi {{name}}", load.ValueOrThrow().Subject);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public static TheoryData<string> UnsafeTemplateIds()
    {
        var rooted = OperatingSystem.IsWindows() ? @"C:\Windows\win" : "/etc/passwd";
        return
        [
            "../outside", @"..\outside", "sub/inner", @"sub\inner", ".hidden", "..", "", " ", "a b",
            "a:b", rooted, new string('a', FileMailTemplateProvider.MaxTemplateIdLength + 1)
        ];
    }

    [Theory]
    [MemberData(nameof(UnsafeTemplateIds))]
    public async Task Unsafe_template_ids_are_rejected_for_load_and_save(string id)
    {
        var root = Path.Combine(Path.GetTempPath(), "cl_mail_tpl_" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "templates");
        try
        {
            var provider = new FileMailTemplateProvider(dir);
            // A JSON file just outside the template directory must stay unreachable.
            await File.WriteAllTextAsync(Path.Combine(root, "outside.json"), """{"Id":"outside","Subject":"secret"}""");

            var load = await provider.LoadTemplateAsync(id);
            Assert.True(load.IsFailure);
            Assert.IsType<ValidationError>(load.Error);
            Assert.Equal(MailErrorCodes.InvalidTemplateId, load.Error!.Code);

            var save = await provider.SaveTemplateAsync(MailTemplate.Create(id, "x", textBody: "y"));
            Assert.True(save.IsFailure);
            Assert.Equal(MailErrorCodes.InvalidTemplateId, save.Error!.Code);

            Assert.Equal("""{"Id":"outside","Subject":"secret"}""", await File.ReadAllTextAsync(Path.Combine(root, "outside.json")));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("welcome")]
    [InlineData("order-confirmation_v2.en")]
    [InlineData("A1")]
    public void Safe_template_ids_are_accepted(string id) =>
        Assert.True(FileMailTemplateProvider.IsValidTemplateId(id));
}

// ── Gated live SMTP test ─────────────────────────────────────────────────────────
// Boots the real CodeLogic runtime (process-wide singleton), so it lives in its own
// class behind the shared "codelogic" collection. It SKIPS unless the full set of
// CL_MAIL_TEST_SMTP_* environment variables is provided.

public sealed class MailRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_mail_test_" + Guid.NewGuid().ToString("N"));

    public MailLibrary? Library { get; private set; }

    public bool Booted { get; private set; }

    public async Task InitializeAsync()
    {
        // Only boot the runtime when the live SMTP test is actually configured to run.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_HOST")))
            return;

        Directory.CreateDirectory(TempDir);

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var host = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_HOST")!;
        var port = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_PORT") ?? "587";
        var user = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_USER") ?? "";
        var pass = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_PASS") ?? "";
        var security = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_SECURITY") ?? "StartTls";

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.Mail");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.mail.json"), $$"""
        {
          "enabled": true,
          "smtp": {
            "host": "{{host}}",
            "port": {{port}},
            "username": "{{user}}",
            "password": "{{pass}}",
            "securityMode": "{{security}}",
            "timeoutSeconds": 30
          }
        }
        """);

        await Libraries.LoadAsync<MailLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<MailLibrary>()
            ?? throw new InvalidOperationException("MailLibrary not available after start.");
        Booted = true;
    }

    public async Task DisposeAsync()
    {
        if (Booted)
        {
            try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* ignore lingering files on Windows */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<MailRuntimeFixture> { }

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

[Collection("codelogic")]
public sealed class LiveSmtpTests
{
    private readonly MailRuntimeFixture _fx;

    public LiveSmtpTests(MailRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv("CL_MAIL_TEST_SMTP_HOST", "set CL_MAIL_TEST_SMTP_* to run live SMTP test")]
    public async Task Send_real_email_via_smtp()
    {
        var from = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_FROM")
                   ?? throw new InvalidOperationException("CL_MAIL_TEST_SMTP_FROM is required");
        var to = Environment.GetEnvironmentVariable("CL_MAIL_TEST_SMTP_TO")
                 ?? throw new InvalidOperationException("CL_MAIL_TEST_SMTP_TO is required");

        var lib = _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

        var message = lib.CreateMessage()
            .From(from)
            .To(to)
            .Subject("CL.Mail integration test")
            .TextBody("Sent by the CL.Mail.Tests live SMTP test.")
            .Build();

        var result = await lib.Smtp.SendAsync(message, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(string.IsNullOrEmpty(result.Value!.MessageId));
    }
}
