# CL.Mail — Changelog

All notable changes to **CodeLogic.Mail** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...).
- **`MailResult`, `MailResult<T>` and the `MailError` enum are removed.** Every operation now returns
  CodeLogic's `Result` / `Result<T>` and fails with an `Error` whose `Code` is a `MailErrorCodes`
  constant:

  | Old | New |
  |---|---|
  | `Task<MailResult> SmtpService.SendAsync(...)` | `Task<Result<MailSendReceipt>>` |
  | `Task<MailResult> MailLibrary.SendTemplatedAsync(...)` | `Task<Result<MailSendReceipt>>` |
  | `MailResult` (IMAP connect/disconnect/folder/message ops, `SaveTemplateAsync`) | `Result` |
  | `MailResult<T>` (fetch, search, list, load, `RenderAsync`) | `Result<T>` |
  | `MailResult.MessageId` | `MailSendReceipt.MessageId` (via `result.Value`) |
  | `MailResult.Timestamp` | `MailSendReceipt.SentAtUtc` on sends; dropped elsewhere |
  | `result.Error` (`MailError` enum) | `result.Error?.Code` (`MailErrorCodes.*` string) or the `Error` kind |
  | `result.ErrorMessage` | `result.Error?.Message` |
  | `MailError.None` | `result.IsSuccess` / `result.Error is null` |

  `IMailTemplateProvider` and `IMailTemplateEngine` implementations must change their return types
  the same way.

  Codes and kinds (each code always has the same kind):

  | `MailErrorCodes` constant | Code | `Error` kind |
  |---|---|---|
  | `InvalidRecipient` | `mail.invalid_recipient` | `ValidationError` |
  | `InvalidSender` | `mail.invalid_sender` | `ValidationError` |
  | `InvalidSubject` | `mail.invalid_subject` | `ValidationError` |
  | `InvalidBody` (new) | `mail.invalid_body` | `ValidationError` |
  | `InvalidHeader` (new) | `mail.invalid_header` | `ValidationError` |
  | `TemplateNotFound` | `mail.template_not_found` | `NotFoundError` |
  | `InvalidTemplateId` (new) | `mail.invalid_template_id` | `ValidationError` |
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

  `SmtpRejected` is a `ValidationError` because the server refused something about this message
  (sender, recipient, size, content), not because the service is broken; `Details` carries the
  SMTP status code. Cancellation previously reported `MailError.Unknown` (or
  `TemplateRenderingFailed` while rendering) and is now `mail.cancelled`.

  Checking a send result:

  ```csharp
  // 4.x
  MailResult r = await mail.Smtp.SendAsync(message);
  if (!r.IsSuccess) log.Warn($"Mail failed ({r.Error}): {r.ErrorMessage}");
  else              log.Info($"Mail sent {r.MessageId}");

  // 5.0
  Result<MailSendReceipt> r = await mail.Smtp.SendAsync(message);
  if (r.IsFailure) log.Warn($"Mail failed ({r.Error!.Code}): {r.Error.Message}");
  else             log.Info($"Mail sent {r.Value!.MessageId}");

  // or by kind
  if (r.Error is TimeoutError or UnavailableError) { /* retry later */ }
  ```

- **`SendAsync` validates more before connecting.** Malformed sender or recipient (To/Cc/Bcc)
  addresses fail with `mail.invalid_sender` / `mail.invalid_recipient` instead of an unexpected
  exception, an empty body is `mail.invalid_body` (was `MailError.InvalidRecipient`), and an
  attachment path that does not exist fails with `mail.attachment_not_found` (was silently dropped).
- **Custom headers, the subject and the sender name are validated.** A header name that is empty, not
  printable US-ASCII, or contains a space or `:` used to make MimeKit throw, which surfaced as
  `mail.unknown` (`InternalError`); it is now `mail.invalid_header` (`ValidationError`). The same code
  rejects a header value with CR, LF or NUL, and a custom header the service writes itself (From, To, Cc,
  Bcc, Subject, MIME-Version, Importance, Content-*). A `Subject` or `FromName` containing CR or LF fails
  with `mail.invalid_subject` / `mail.invalid_sender` instead of being sent.
- An unreachable SMTP/IMAP server (`SocketException`) is now `mail.smtp_connection_failed` /
  `mail.imap_connection_failed` (`UnavailableError`) instead of `Unknown` / `ImapError`.
- A template file that deserializes to nothing is `mail.template_rendering_failed` (was
  `TemplateNotFound`).

### Security

- **Template IDs can no longer escape the template directory.** `FileMailTemplateProvider` combined the
  directory with the raw ID, so `../x`, `sub/x` or a rooted path could load, or overwrite on save, a JSON
  file outside `TemplateDirectory`. IDs must now be file-name-safe (ASCII letters, digits, `-`, `_`, `.`;
  starting with a letter or digit; at most 128 characters), and the resolved path is checked to sit
  directly in the template directory. Anything else fails with the new code
  `MailErrorCodes.InvalidTemplateId` (`mail.invalid_template_id`, `ValidationError`) for load, save and
  layout lookup. `FileMailTemplateProvider.IsValidTemplateId` lets callers check an ID up front.

### Documentation

- Configuration examples in the README and the docs use the camelCase keys the CodeLogic loader reads.
  The PascalCase keys shown before are matched case-sensitively and were ignored, leaving defaults active.

### Changed

- Package versions are pinned (MailKit and MimeKit 4.18.2) instead of floating (`4.*`), so a build restores the same dependencies every time.
- `SimpleTemplateEngine` uses source-generated regexes (`[GeneratedRegex]`) for its block, section and
  placeholder patterns instead of parsing them through the static `Regex` cache on each render. Output is
  unchanged.

## 2026-10-27

### Security

- **HTML bodies encode substituted values.** `SimpleTemplateEngine` inserted every value into
  `HtmlBody` as-is, so a user-supplied value (a user name, a browser description) could inject
  markup or links into an e-mail. In `HtmlBody` and HTML layouts, `{{var}}`, `${var}` and `{var}`
  are now HTML-encoded. Subjects and text bodies are unchanged.
- **Values are inserted once.** Substitution ran the three placeholder syntaxes one after another,
  and loop output was substituted again by the outer pass, so a value containing `${other}` or
  `{other}` was expanded: user text could pull other variables (links, tokens) into the message.
  All syntaxes are now replaced in a single pass and nothing inside a value is expanded.

### Added

- **`{{{var}}}`** inserts a value without encoding, for values that are trusted HTML.

### Fixed

- **Text layouts get the text sections.** With a layout, the text layout was rendered with the
  child's *HTML* sections (including `body`). Text and HTML layouts now each receive their own
  sections, and a child's section wins over a variable of the same name.

### Upgrade notes

- Templates that pass ready-made HTML in a variable must switch that placeholder to `{{{var}}}`.

## 2026-10-24

### Added

- **Live config changes** (requires CodeLogic 4.9+). `MailLibrary` implements `IConfigReloadable`: a
  change to `config.mail.json` made through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`
  or the config file watcher is applied without a restart, rebuilding only what changed. Changed SMTP
  settings replace `Smtp` (sends in flight finish on the old settings); a changed template directory
  replaces `TemplateProvider`/`TemplateEngine`; changed IMAP settings replace `Imap` and close the old
  connection (re-read `Imap` and restart IDLE). `Enabled` can be switched either way.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Documentation

- Full rewrite of the README and the multi-page docs set to the CodeLogic.Libs
  house style. The README is now concise, Markdown-only, and NuGet-safe; the
  guide is split into **Overview & Sending** (`docs/libs/mail/index.md`) and
  **Receiving & Templates** (`docs/libs/mail/receiving-templates.md`), with the
  old `docs/libs/mail.md` reduced to a redirect stub and the docs navigation
  updated accordingly. No API changes.

## [4.5.1] — 2026-06-20

### Documentation

- Rewrote the README and the Mail & Templates guide to match the current public
  API. The previous docs described members that no longer exist (`EmailMessage`,
  `EmailAttachment`, `ReplyTo`, `RenderTemplateAsync`, `ReadInboxAsync`,
  `ImapFilter`, `MarkAsReadAsync`, the `SendAsync(from:, to:, …)` /
  `SendTemplateAsync` overloads, and the `UseSsl` / `FromAddress` config keys).
- Documented the real surface: the fluent `MailBuilder` (`CreateMessage()`),
  `SmtpService.SendAsync(MailMessage)`, `MailLibrary.SendTemplatedAsync`, the
  `MailResult` / `MailError` result model, and the JSON-backed template system
  (`MailTemplate`, `IMailTemplateProvider`, `IMailTemplateEngine`).
- Documented previously undocumented features: template conditionals
  (`{{#if}}`/`{{#else}}`), loops (`{{#each}}`), sections and layouts; the
  `${var}` / `{var}` placeholder syntaxes; the full IMAP API (fetch/page, fetch
  by UID, search via `ImapSearchCriteria`, move/copy/delete, flag operations,
  folder management); and RFC 2177 IMAP IDLE push (`StartIdleAsync` /
  `NewMailReceived`) with its `EnableIdle` / `IdleRefreshMinutes` config keys.
- Corrected the documented config schema and `SecurityMode` values
  (`None` / `StartTls` / `SslTls`).

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated mail configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

### Fixed

- Resolved null-reference warnings in `ImapService`.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. SMTP / IMAP services + a CodeLogic-native template
provider.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.Mail).
