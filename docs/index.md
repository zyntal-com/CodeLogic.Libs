---
_layout: landing
---

<div class="cl-hero">
  <img src="images/logo.svg" alt="CodeLogic Libraries logo" />
  <h1>CodeLogic Libraries</h1>
  <p class="lead">
    Fourteen production-ready .NET 11 libraries for the
    <a href="https://github.com/zyntal-com/CodeLogic">CodeLogic 5</a> framework.
    Databases, email, storage, security, monitoring, and more — each a self-contained
    <code>ILibrary</code> that manages its own configuration, lifecycle, and health checks.
  </p>
  <div class="cl-badges">
    <a href="https://www.nuget.org/profiles/zyntal.com"><img src="https://img.shields.io/badge/NuGet-CodeLogic.*-2f86ad" alt="NuGet" /></a>
    <img src="https://img.shields.io/badge/.NET-11-512bd4" alt=".NET 11" />
    <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT" />
  </div>
</div>

Every `CL.*` package plugs directly into the CodeLogic boot sequence. You load it with
`Libraries.LoadAsync<T>()`, configure it through an auto-generated JSON file, and the framework
handles initialization order, dependency wiring, and graceful shutdown.

```csharp
await Libraries.LoadAsync<CL.Mail.MailLibrary>();
await Libraries.LoadAsync<CL.TwoFactorAuth.TwoFactorAuthLibrary>();

await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mail = Libraries.Get<CL.Mail.MailLibrary>();
```

[Get started →](getting-started.md) &nbsp;·&nbsp; [Browse all libraries →](libs/index.md)

<div class="cl-group">Databases</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/mssql/index.md">
    <div class="lib-name">CL.MSSQL</div>
    <div class="lib-pkg">CodeLogic.MSSQL</div>
    <p class="lib-desc">SQL Server 2019+ and Azure SQL with typed LINQ, caching, schema synchronization, and migrations.</p>
  </a>
  <a class="lib-card" href="libs/postgresql/index.md">
    <div class="lib-name">CL.PostgreSQL</div>
    <div class="lib-pkg">CodeLogic.PostgreSQL</div>
    <p class="lib-desc">PostgreSQL with multi-database support, the <code>Repository&lt;T&gt;</code> + query-builder pattern, table sync, and backups.</p>
  </a>
</div>

<div class="cl-group">Communication</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/mail/index.md">
    <div class="lib-name">CL.Mail</div>
    <div class="lib-pkg">CodeLogic.Mail</div>
    <p class="lib-desc">SMTP send + IMAP read (with IDLE) and a lightweight template engine for HTML and plain-text mail.</p>
  </a>
  <a class="lib-card" href="libs/socialconnect.md">
    <div class="lib-name">CL.SocialConnect</div>
    <div class="lib-pkg">CodeLogic.SocialConnect</div>
    <p class="lib-desc">Discord webhooks plus Steam Web API profile, ban, and ticket-authentication lookups.</p>
  </a>
</div>

<div class="cl-group">Storage</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/storage/index.md">
    <div class="lib-name">CL.Storage</div>
    <div class="lib-pkg">CodeLogic.Storage</div>
    <p class="lib-desc">Safe mounted storage across local filesystems, S3, FTP/SFTP, WebDAV, Azure Blob, GCS, and Swift, with verified and resumable transfers, a durable transfer queue, three-way sync, and change watching.</p>
  </a>
</div>

<div class="cl-group">Security</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/twofactorauth.md">
    <div class="lib-name">CL.TwoFactorAuth</div>
    <div class="lib-pkg">CodeLogic.TwoFactorAuth</div>
    <p class="lib-desc">TOTP two-factor auth with QR-code generation — compatible with Google Authenticator, Authy, and 1Password.</p>
  </a>
</div>

<div class="cl-group">Networking &amp; Game</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/netutils.md">
    <div class="lib-name">CL.NetUtils</div>
    <div class="lib-pkg">CodeLogic.NetUtils</div>
    <p class="lib-desc">DNSBL blacklist checks and MaxMind GeoIP2 location lookups for IP intelligence.</p>
  </a>
  <a class="lib-card" href="libs/gamenetquery.md">
    <div class="lib-name">CL.GameNetQuery</div>
    <div class="lib-pkg">CodeLogic.GameNetQuery</div>
    <p class="lib-desc">Game-server queries: Valve Source (A2S) UDP, Source RCON (CS2/CSS), and Minecraft UDP/RCON.</p>
  </a>
</div>

<div class="cl-group">Ops &amp; Tooling</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/systemstats.md">
    <div class="lib-name">CL.SystemStats</div>
    <div class="lib-pkg">CodeLogic.SystemStats</div>
    <p class="lib-desc">Cross-platform CPU, memory, process, and uptime stats — Windows and Linux (<code>/proc</code>).</p>
  </a>
  <a class="lib-card" href="libs/githelper.md">
    <div class="lib-name">CL.GitHelper</div>
    <div class="lib-pkg">CodeLogic.GitHelper</div>
    <p class="lib-desc">Git repository automation via LibGit2Sharp: clone, fetch, pull, push, branch, commit, log.</p>
  </a>
</div>

<div class="cl-group">Utilities</div>
<div class="lib-grid">
  <a class="lib-card" href="libs/common/index.md">
    <div class="lib-name">CL.Common</div>
    <div class="lib-pkg">CodeLogic.Common</div>
    <p class="lib-desc">A utility toolkit: encryption, hashing, ID/password generation, JSON, cron, networking, caching, resilience, scheduling, and more.</p>
  </a>
  <a class="lib-card" href="libs/imaging/index.md">
    <div class="lib-name">CL.Imaging</div>
    <div class="lib-pkg">CodeLogic.Imaging</div>
    <p class="lib-desc">Image editing on SkiaSharp: fluent pipeline, safe uploads, EXIF metadata, responsive variants, BlurHash and perceptual hashes.</p>
  </a>
</div>

---

## Why CL.* libraries

- **CodeLogic-native** — each implements `ILibrary` with the full four-phase lifecycle (Configure → Initialize → Start → Stop).
- **Self-configuring** — config files are auto-generated with sane defaults on first run.
- **Health-aware** — every library implements `HealthCheckAsync()` for operational monitoring.
- **Isolated** — each library owns its own config, log, and data directories.
- **Result-based** — operations return `Result` / `Result<T>` instead of throwing for expected failures.

## Next steps

- **[Getting Started](getting-started.md)** — reference, register, configure, and use any `CL.*` library.
- **[Libraries](libs/index.md)** — the full catalog with per-library guides.
- **[API Reference](api/index.md)** — types and members generated from the source XML docs.
