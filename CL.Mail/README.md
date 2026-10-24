# CodeLogic.Mail

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mail)](https://www.nuget.org/packages/CodeLogic.Mail)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> SMTP sending and IMAP reading for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — with a built-in template engine, attachments, and RFC 2177 IDLE push notifications.

Compose messages with a fluent `MailBuilder`, send them over SMTP, render variable-substitution templates (conditionals, loops, and layouts), and read mailboxes over IMAP — including live IDLE push. Built on [MailKit](https://www.nuget.org/packages/MailKit) and [MimeKit](https://www.nuget.org/packages/MimeKit). Every fallible operation returns a `MailResult` / `MailResult<T>` carrying a categorised `MailError` instead of throwing on protocol, auth, or timeout errors.

## Install

```bash
dotnet add package CodeLogic.Mail
```

## Quick start

```csharp
using CL.Mail;

await Libraries.LoadAsync<MailLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mail = Libraries.Get<MailLibrary>();

// Compose with the fluent builder and send via SMTP.
var message = mail.CreateMessage()
    .From("noreply@example.com", "My App")
    .To("user@example.com")
    .Subject("Welcome!")
    .HtmlBody("<h1>Hello</h1><p>Welcome aboard.</p>")
    .TextBody("Hello — welcome aboard.")
    .Build();

MailResult result = await mail.Smtp.SendAsync(message);
if (!result.IsSuccess)
    Console.WriteLine($"Send failed ({result.Error}): {result.ErrorMessage}");
```

## Features

- **SMTP sending** — plain-text and/or HTML bodies, CC/BCC, attachments, custom headers, and message priority. A fresh connection is opened per send (no pooling).
- **Fluent `MailBuilder`** — `mail.CreateMessage()` composes an immutable `MailMessage`; `Build()` throws if sender, recipients, subject, or a body are missing.
- **Template engine** — JSON-backed templates with `{{var}}` / `${var}` / `{var}` substitution, `{{#if}}…{{#else}}…{{/if}}` conditionals, `{{#each}}…{{/each}}` loops, and `{{#section}}` / layout composition.
- **Templated send** — `SendTemplatedAsync` renders a template by id and sends it in one call.
- **IMAP reading** — list folders, fetch and page messages, fetch by UID with attachments, search, move/copy/delete, and add/remove flags.
- **IMAP IDLE** — RFC 2177 push notifications via `StartIdleAsync` and the `NewMailReceived` event.
- **Result-based errors** — `MailResult` / `MailResult<T>` carry `IsSuccess`, a categorised `MailError`, an `ErrorMessage`, and a `MessageId`.

## Configuration

Auto-generated on first run as `config.mail.json`. The `Imap` section is optional — omit it to disable IMAP entirely.

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
  "Imap": {
    "Host": "imap.example.com",
    "Port": 993,
    "Username": "inbox@example.com",
    "Password": "your-password-or-app-password",
    "SecurityMode": "SslTls",
    "TimeoutSeconds": 30,
    "EnableIdle": false,
    "IdleRefreshMinutes": 25
  },
  "DefaultFromEmail": "noreply@example.com",
  "DefaultFromName": "My App",
  "TemplateDirectory": "templates"
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch; when `false` the services aren't created and health reports *disabled*. |
| `Smtp.Host` / `Smtp.Port` | `smtp.example.com` / `587` | SMTP server and port. |
| `Smtp.Username` / `Smtp.Password` | `""` | SMTP credentials (use an app password where required). |
| `Smtp.SecurityMode` | `StartTls` | `None`, `StartTls` (STARTTLS, port 587), or `SslTls` (implicit TLS, port 465). |
| `Smtp.TimeoutSeconds` | `30` | SMTP connect/operation timeout. |
| `Imap` | `null` | Optional; omit the whole section to disable IMAP. |
| `Imap.SecurityMode` | `SslTls` | `None`, `StartTls`, or `SslTls` (implicit TLS, port 993). |
| `Imap.EnableIdle` | `false` | Enable RFC 2177 IDLE push monitoring. |
| `Imap.IdleRefreshMinutes` | `25` | IDLE connection refresh interval (1–28; keep under 29). |
| `DefaultFromEmail` / `DefaultFromName` | `null` | Fallback sender when a message omits `From`. |
| `TemplateDirectory` | `templates` | Template folder; relative to the library data dir unless absolute. |

### Changing config while running

With CodeLogic 4.9+, `config.mail.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only what changed is rebuilt:

- **SMTP settings**: `mail.Smtp` is replaced. Every send opens its own connection, so sends already running finish on the old settings.
- **Template directory**: `mail.TemplateProvider` and `mail.TemplateEngine` are replaced.
- **IMAP settings** (added, changed or removed): `mail.Imap` is replaced and the old connection is closed. Re-read `mail.Imap` — don't keep the old instance — and call `StartIdleAsync` again if you were idling.
- **`enabled`**: can be switched either way.

Other settings (default sender, ...) are read when used and need nothing rebuilt.

## Documentation

Full guide: **[CL.Mail documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/mail/index.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- MailKit 4.x · MimeKit 4.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
