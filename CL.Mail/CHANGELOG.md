# CL.Mail — Changelog

All notable changes to **CodeLogic.Mail** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). This library's API is unchanged, and
  its results can now be matched exhaustively with `switch`.

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
