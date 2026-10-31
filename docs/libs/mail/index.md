# CL.Mail

> SMTP sending and IMAP reading with a JSON template engine, attachments, and RFC 2177 IDLE push notifications — built on MailKit.

`CL.Mail` adds e-mail to a CodeLogic application. Compose messages with a fluent `MailBuilder`, send them through `mail.Smtp`, render variable-substitution templates, and read mailboxes through `mail.Imap` — including live IDLE push. It builds on [MailKit](https://www.nuget.org/packages/MailKit) and [MimeKit](https://www.nuget.org/packages/MimeKit). Operations do **not** use the framework `Result`; they return a mail-specific `MailResult` / `MailResult<T>` carrying a categorised `MailError` instead of throwing on protocol, auth, or timeout failures.

| | |
|---|---|
| **Package** | [`CodeLogic.Mail`](https://www.nuget.org/packages/CodeLogic.Mail) |
| **Library class** | `CL.Mail.MailLibrary` |
| **Config file** | `config.mail.json` |
| **Dependencies** | MailKit 4.x · MimeKit 4.x |

This page covers loading, sending, and SMTP configuration. The deep IMAP and template material lives on the sub-page:

- **[Receiving & Templates](receiving-templates.md)** — IMAP connect / folders / fetch / search / manipulate / flags / IDLE + `NewMailReceived`; the template engine syntax; `IMailTemplateProvider`; and `SendTemplatedAsync`.

## Install & load

```bash
dotnet add package CodeLogic.Mail
```

```csharp
using CL.Mail;

await Libraries.LoadAsync<MailLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mail = Libraries.Get<MailLibrary>();
```

Set your SMTP (and optional IMAP) endpoints in `config.mail.json` (auto-generated on first run) before `ConfigureAsync()`.

## Library surface

| Member | Returns | Purpose |
|--------|---------|---------|
| `CreateMessage()` | `MailBuilder` | New fluent message builder. |
| `Smtp` | `SmtpService` | Sending. |
| `Imap` | `ImapService` | Reading — **throws** if IMAP is not configured. |
| `HasImap` | `bool` | `true` when an `Imap` config section is present. |
| `SendTemplatedAsync(...)` | `MailResult` | Render a template by id and send it. |
| `TemplateProvider` | `IMailTemplateProvider` | Load / list / save templates. |
| `TemplateEngine` | `IMailTemplateEngine` | Render a `MailTemplate` to text. |

Guard IMAP access with `HasImap` — reading `Imap` when no `Imap` section is configured throws.

## Sending with MailBuilder

`mail.CreateMessage()` returns a `MailBuilder`. Chain sender, recipients, subject, and bodies, then `Build()` and hand the result to `mail.Smtp.SendAsync`. At least one of `HtmlBody` / `TextBody` is required.

```csharp
var message = mail.CreateMessage()
    .From("noreply@example.com", "My App")
    .To("alice@example.com", "bob@example.com")   // params: multiple recipients
    .Cc("manager@example.com")
    .Bcc("audit@example.com")
    .Subject("Order Confirmation #12345")
    .Body(textBody: "Your order has been confirmed.",
          htmlBody: "<p>Your order has been confirmed.</p>")
    .Header("X-Mailer", "MyApp")
    .Priority(MailPriority.High)
    .Build();

MailResult result = await mail.Smtp.SendAsync(message);
if (result.IsSuccess)
    Console.WriteLine($"Sent, message id: {result.MessageId}");
```

`Build()` throws `InvalidOperationException` when a required field (sender, recipients, subject, or a body) is missing — so call it inside the application flow, not from untrusted input without a try/catch.

### The MailBuilder methods

| Method | Effect |
|--------|--------|
| `From(email)` / `From(email, displayName)` | Sender address (and optional display name). |
| `To(email)` / `To(params string[])` | One or more `To` recipients. |
| `Cc(...)` / `Bcc(...)` | One or more `Cc` / `Bcc` recipients. |
| `Subject(string)` | Message subject. |
| `TextBody(string)` / `HtmlBody(string)` | Plain-text and/or HTML body. |
| `Body(textBody, htmlBody)` | Both bodies in one call. |
| `Attach(filePath)` / `Attach(params string[])` | Attach files by path (see below). |
| `Header(name, value)` | Add a custom MIME header. |
| `Priority(MailPriority)` | `Low`, `Normal` (default), or `High`. |
| `Build()` | Produce the immutable `MailMessage`. |

### Attachments

Attach files by absolute path. Paths that don't exist on disk are silently skipped when the MIME message is built — if a missing attachment must be an error, validate the path yourself first.

```csharp
var message = mail.CreateMessage()
    .From("noreply@example.com")
    .To("alice@example.com")
    .Subject("Your Invoice")
    .HtmlBody("<p>Please find your invoice attached.</p>")
    .Attach(@"C:\invoices\inv-2026-06.pdf")
    .Build();

await mail.Smtp.SendAsync(message);
```

## SmtpService.SendAsync

`mail.Smtp.SendAsync(MailMessage, ct)` opens a fresh connection per call (there is no pooling), authenticates, sends, and returns a `MailResult`. On success the result carries the generated `MessageId`.

```csharp
MailResult result = await mail.Smtp.SendAsync(message, ct);

if (!result.IsSuccess)
{
    switch (result.Error)
    {
        case MailError.SmtpAuthenticationFailed: /* bad credentials */ break;
        case MailError.SmtpTimeout:              /* server slow / unreachable */ break;
        case MailError.SmtpRejected:             /* recipient/content refused */ break;
        case MailError.InvalidRecipient:         /* no valid To address */ break;
        default:                                 /* result.ErrorMessage has detail */ break;
    }
}
```

## The MailMessage model

`MailMessage` is the immutable record produced by `Build()`.

```csharp
public sealed record MailMessage
{
    public string From { get; init; }                       // required
    public string? FromName { get; init; }
    public IReadOnlyList<string> To { get; init; }          // required, >= 1
    public IReadOnlyList<string> Cc { get; init; }
    public IReadOnlyList<string> Bcc { get; init; }
    public string Subject { get; init; }                    // required
    public string? TextBody { get; init; }                  // >= 1 body required
    public string? HtmlBody { get; init; }
    public IReadOnlyList<string> Attachments { get; init; } // file paths
    public IReadOnlyDictionary<string, string> Headers { get; init; }
    public MailPriority Priority { get; init; }             // default Normal
    public DateTimeOffset CreatedAt { get; init; }
}
```

`MailPriority` is `Low` / `Normal` / `High`; it maps to the standard `X-Priority` / `Importance` headers.

## Configuration

`config.mail.json` (section `mail`) is written with defaults on first run. The `Imap` section is optional — omit it to disable IMAP entirely (see [Receiving & Templates](receiving-templates.md) for the IMAP keys).

```json
{
  "Enabled": true,
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "noreply@example.com",
    "Password": "your-password-or-app-password",
    "SecurityMode": "StartTls",
    "TimeoutSeconds": 30
  },
  "DefaultFromEmail": "noreply@example.com",
  "DefaultFromName": "My App",
  "TemplateDirectory": "templates"
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the services aren't created and the health check reports *disabled*. |
| `Smtp.Host` | `string` | `smtp.example.com` | SMTP server hostname. |
| `Smtp.Port` | `int` | `587` | SMTP server port. |
| `Smtp.Username` | `string` | `""` | SMTP username. |
| `Smtp.Password` | `string` | `""` | SMTP password (use an app password where the provider requires it). |
| `Smtp.SecurityMode` | `MailSecurityMode` | `StartTls` | `None`, `StartTls` (STARTTLS, port 587), or `SslTls` (implicit TLS, port 465). |
| `Smtp.TimeoutSeconds` | `int` | `30` | SMTP connect/operation timeout in seconds. |
| `DefaultFromEmail` | `string?` | `null` | Fallback sender address when a message omits `From`. |
| `DefaultFromName` | `string?` | `null` | Fallback sender display name. |
| `TemplateDirectory` | `string` | `templates` | Template folder; relative to the library data dir unless an absolute path is given. |

### Changing config while running

With CodeLogic 4.9+, `config.mail.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only what changed is rebuilt:

- **SMTP settings**: `mail.Smtp` is replaced. Every send opens its own connection, so sends already running finish on the old settings.
- **Template directory**: `mail.TemplateProvider` and `mail.TemplateEngine` are replaced.
- **IMAP settings** (added, changed or removed): `mail.Imap` is replaced and the old connection is closed. Re-read `mail.Imap` — don't keep the old instance — and call `StartIdleAsync` again if you were idling.
- **`enabled`**: can be switched either way.

Other settings (default sender, ...) are read when used and need nothing rebuilt.

## Health check

`HealthCheckAsync()` reports the configured endpoints.

```csharp
HealthStatus status = await mail.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

- *Healthy* — the SMTP host is configured (and IMAP, when present).
- *Degraded* — the SMTP host is missing.
- *Unhealthy* — the library failed to initialise.
- *Healthy ("disabled")* — `Enabled` is `false`.

## MailResult & MailError reference

Sending and the void-returning IMAP operations return `MailResult`; data-returning operations return `MailResult<T>`. Neither throws for the expected failure paths — branch on `IsSuccess` and `Error`.

```csharp
public sealed class MailResult
{
    public bool IsSuccess { get; }
    public MailError Error { get; }       // None on success
    public string? ErrorMessage { get; }
    public string? MessageId { get; }     // set by SmtpService on a successful send
    public DateTimeOffset Timestamp { get; }

    public static MailResult Success(string? messageId = null);
    public static MailResult Failure(MailError error, string? message = null);
}

public sealed class MailResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public MailError Error { get; }
    public string? ErrorMessage { get; }
    public DateTimeOffset Timestamp { get; }

    public static MailResult<T> Success(T value);
    public static MailResult<T> Failure(MailError error, string? message = null);
}
```

`MailError` categorises every failure:

| Group | Members |
|-------|---------|
| Success | `None` |
| Validation | `InvalidRecipient`, `InvalidSender`, `InvalidSubject`, `AttachmentNotFound` |
| Templates | `TemplateNotFound`, `TemplateRenderingFailed` |
| SMTP | `SmtpConfigInvalid`, `SmtpAuthenticationFailed`, `SmtpTimeout`, `SmtpRejected`, `SmtpError` |
| IMAP | `ImapConfigInvalid`, `ImapAuthenticationFailed`, `ImapTimeout`, `ImapError`, `FolderNotFound`, `MessageNotFound` |
| Other | `Unknown` |

## See also

- [Receiving & Templates](receiving-templates.md) — IMAP, IDLE, and the template engine.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Mail)
