# CL.Mail

> SMTP sending and IMAP reading with a JSON template engine, attachments, and RFC 2177 IDLE push notifications — built on MailKit.

`CL.Mail` adds e-mail to a CodeLogic application. Compose messages with a fluent `MailBuilder`, send them through `mail.Smtp`, render variable-substitution templates, and read mailboxes through `mail.Imap` — including live IDLE push. It builds on [MailKit](https://www.nuget.org/packages/MailKit) and [MimeKit](https://www.nuget.org/packages/MimeKit). Since 5.0, operations return the framework `Result` / `Result<T>` (namespace `CodeLogic.Core.Results`); a failure is an `Error` whose kind says what went wrong and whose `Code` is one of `MailErrorCodes`. Expected protocol, auth, and timeout failures are returned, not thrown. (4.x had its own `MailResult` / `MailError`; see [Upgrading from 4.x](#upgrading-from-4x).)

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
| `SendTemplatedAsync(...)` | `Result<MailSendReceipt>` | Render a template by id and send it. |
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

Result<MailSendReceipt> result = await mail.Smtp.SendAsync(message);
if (result.IsSuccess)
    Console.WriteLine($"Sent, message id: {result.Value!.MessageId}");
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

Attach files by absolute path. Since 5.0 every attachment must exist when `SendAsync` runs: a missing file fails the send with a `NotFoundError` (`MailErrorCodes.AttachmentNotFound`) before anything is sent. (4.x silently dropped missing attachments.)

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

`mail.Smtp.SendAsync(MailMessage, ct)` validates the message (sender, recipients, subject, body, attachments), opens a fresh connection per call (there is no pooling), authenticates, sends, and returns a `Result<MailSendReceipt>`. On success the receipt carries the `MessageId` and `SentAtUtc`.

Branch on the error **kind** for coarse handling, or on `Error.Code` for a specific failure:

```csharp
Result<MailSendReceipt> result = await mail.Smtp.SendAsync(message, ct);

switch (result.Error)
{
    case null:
        logger.Info($"Sent {result.Value!.MessageId}");
        break;
    case UnauthorizedError:                      /* bad SMTP credentials */ break;
    case TimeoutError or UnavailableError:       /* server slow / down: retry later */ break;
    case ValidationError e when e.Code == MailErrorCodes.SmtpRejected:
        /* server refused a recipient/content; e.Details has the SMTP status */ break;
    case ValidationError e:                      /* bad input: e.Code says which field */ break;
    default:                                     /* InternalError etc.: result.Error.Message */ break;
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
    public IReadOnlyDictionary<string, string> Headers { get; init; } // no CR/LF; not From/To/Subject/Content-*...
    public MailPriority Priority { get; init; }             // default Normal
    public DateTimeOffset CreatedAt { get; init; }
}
```

`MailPriority` is `Low` / `Normal` / `High`; it maps to the standard `X-Priority` / `Importance` headers.

## Configuration

`config.mail.json` (section `mail`) is written with defaults on first run. The `Imap` section is optional — omit it to disable IMAP entirely (see [Receiving & Templates](receiving-templates.md) for the IMAP keys).

```json
{
  "enabled": true,
  "smtp": {
    "host": "smtp.example.com",
    "port": 587,
    "username": "noreply@example.com",
    "password": "your-password-or-app-password",
    "securityMode": "StartTls",
    "timeoutSeconds": 30
  },
  "defaultFromEmail": "noreply@example.com",
  "defaultFromName": "My App",
  "templateDirectory": "templates"
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

Config changes apply live: `config.mail.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only what changed is rebuilt:

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

## Results & error codes reference

| Operation | Returns |
|-----------|---------|
| `SmtpService.SendAsync`, `MailLibrary.SendTemplatedAsync` | `Result<MailSendReceipt>` |
| IMAP operations without data, `SaveTemplateAsync` | `Result` |
| Data-returning operations (fetch, search, list, load, render) | `Result<T>` |

```csharp
public sealed record MailSendReceipt(string? MessageId, DateTime SentAtUtc);
```

`Result` / `Result<T>` are the CodeLogic 5 unions: `IsSuccess`, `IsFailure`, `Value`, `Error`, plus `Map`, `Bind`, `Match`, `OnSuccess`, `OnFailure`, `ValueOrDefault`, `ValueOrThrow`. None of the mail operations throw for the expected failure paths.

Every failure is an `Error` whose `Code` is a `MailErrorCodes` constant, and each code always comes with the same kind:

| `MailErrorCodes` constant | Code | `Error` kind |
|---|---|---|
| `InvalidRecipient` | `mail.invalid_recipient` | `ValidationError` |
| `InvalidSender` | `mail.invalid_sender` | `ValidationError` |
| `InvalidSubject` | `mail.invalid_subject` | `ValidationError` |
| `InvalidBody` (new) | `mail.invalid_body` | `ValidationError` |
| `InvalidHeader` (new) | `mail.invalid_header` | `ValidationError` |
| `TemplateNotFound` | `mail.template_not_found` | `NotFoundError` |
| `InvalidTemplateId` | `mail.invalid_template_id` | `ValidationError` |
| `TemplateRenderingFailed` | `mail.template_rendering_failed` | `InternalError` |
| `SmtpConfigInvalid` | `mail.smtp_config_invalid` | `ValidationError` |
| `SmtpAuthenticationFailed` | `mail.smtp_authentication_failed` | `UnauthorizedError` |
| `SmtpTimeout` | `mail.smtp_timeout` | `TimeoutError` |
| `SmtpConnectionFailed` (new) | `mail.smtp_connection_failed` | `UnavailableError` |
| `SmtpRejected` | `mail.smtp_rejected` | `ValidationError` (`Details` has the SMTP status) |
| `SmtpError` | `mail.smtp_error` | `InternalError` |
| `AttachmentNotFound` | `mail.attachment_not_found` | `NotFoundError` |
| `ImapConfigInvalid` | `mail.imap_config_invalid` | `ValidationError` |
| `ImapAuthenticationFailed` | `mail.imap_authentication_failed` | `UnauthorizedError` |
| `ImapTimeout` | `mail.imap_timeout` | `TimeoutError` |
| `ImapConnectionFailed` (new) | `mail.imap_connection_failed` | `UnavailableError` |
| `ImapError` | `mail.imap_error` | `InternalError` |
| `FolderNotFound` | `mail.folder_not_found` | `NotFoundError` |
| `MessageNotFound` | `mail.message_not_found` | `NotFoundError` |
| `Cancelled` (new) | `mail.cancelled` | `UnavailableError` |
| `Unknown` | `mail.unknown` | `InternalError` |

## Upgrading from 4.x

`MailResult`, `MailResult<T>` and the `MailError` enum are gone. Replace `result.Error == MailError.X` with `result.Error?.Code == MailErrorCodes.X` (or match on the kind), `result.ErrorMessage` with `result.Error?.Message`, and `result.MessageId` with `result.Value?.MessageId`:

```csharp
// 4.x
MailResult r = await mail.Smtp.SendAsync(message);
if (!r.IsSuccess) log.Warn($"{r.Error}: {r.ErrorMessage}");
else              log.Info($"sent {r.MessageId}");

// 5.0
Result<MailSendReceipt> r = await mail.Smtp.SendAsync(message);
if (r.IsFailure) log.Warn($"{r.Error!.Code}: {r.Error.Message}");
else             log.Info($"sent {r.Value!.MessageId}");
```

The full old → new mapping, with every code, is in `CL.Mail/CHANGELOG.md`.

## See also

- [Receiving & Templates](receiving-templates.md) — IMAP, IDLE, and the template engine.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Mail)
