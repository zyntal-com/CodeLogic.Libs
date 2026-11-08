# Libraries

The CodeLogic Libraries suite is a set of independent NuGet packages. Each is a CodeLogic
`ILibrary` — load the ones you need, configure them through their JSON files, and use them from
your application or from other libraries after `StartAsync()`.

<div class="cl-group">Databases</div>
<div class="lib-grid">
  <a class="lib-card" href="data/index.md">
    <div class="lib-name">CL.Data</div>
    <div class="lib-pkg">CodeLogic.Data · .MySql · .PostgreSql · .SqlServer · .Sqlite</div>
    <p class="lib-desc">One database framework for MySQL, PostgreSQL, SQL Server and SQLite: typed LINQ, repositories, joins, schema sync and migrations, unified caching, read replicas, resilience, OpenTelemetry and bulk copy.</p>
  </a>
</div>

<div class="cl-group">Communication</div>
<div class="lib-grid">
  <a class="lib-card" href="mail/index.md">
    <div class="lib-name">CL.Mail</div>
    <div class="lib-pkg">CodeLogic.Mail</div>
    <p class="lib-desc">SMTP send, IMAP read + IDLE, and a template engine.</p>
  </a>
  <a class="lib-card" href="mqtt.md">
    <div class="lib-name">CL.Mqtt</div>
    <div class="lib-pkg">CodeLogic.Mqtt</div>
    <p class="lib-desc">MQTT clients with automatic reconnect, and an embedded MQTT broker.</p>
  </a>
  <a class="lib-card" href="socialconnect.md">
    <div class="lib-name">CL.SocialConnect</div>
    <div class="lib-pkg">CodeLogic.SocialConnect</div>
    <p class="lib-desc">Discord webhooks and Steam Web API.</p>
  </a>
</div>

<div class="cl-group">Storage</div>
<div class="lib-grid">
  <a class="lib-card" href="storage/index.md">
    <div class="lib-name">CL.Storage</div>
    <div class="lib-pkg">CodeLogic.Storage</div>
    <p class="lib-desc">Safe mounted local, S3, FTP/SFTP, WebDAV, Azure, GCS, and Swift storage, with verified and resumable transfers, a durable queue, three-way sync, and change watching.</p>
  </a>
</div>

<div class="cl-group">Security</div>
<div class="lib-grid">
  <a class="lib-card" href="twofactorauth.md">
    <div class="lib-name">CL.TwoFactorAuth</div>
    <div class="lib-pkg">CodeLogic.TwoFactorAuth</div>
    <p class="lib-desc">TOTP 2FA with QR-code generation.</p>
  </a>
</div>

<div class="cl-group">Networking &amp; Game</div>
<div class="lib-grid">
  <a class="lib-card" href="netutils.md">
    <div class="lib-name">CL.NetUtils</div>
    <div class="lib-pkg">CodeLogic.NetUtils</div>
    <p class="lib-desc">DNSBL checks and GeoIP2 lookups.</p>
  </a>
  <a class="lib-card" href="gamenetquery.md">
    <div class="lib-name">CL.GameNetQuery</div>
    <div class="lib-pkg">CodeLogic.GameNetQuery</div>
    <p class="lib-desc">Valve A2S/RCON and Minecraft queries.</p>
  </a>
</div>

<div class="cl-group">Ops &amp; Tooling</div>
<div class="lib-grid">
  <a class="lib-card" href="systemstats.md">
    <div class="lib-name">CL.SystemStats</div>
    <div class="lib-pkg">CodeLogic.SystemStats</div>
    <p class="lib-desc">CPU, memory, process, and uptime stats (cross-platform).</p>
  </a>
  <a class="lib-card" href="githelper.md">
    <div class="lib-name">CL.GitHelper</div>
    <div class="lib-pkg">CodeLogic.GitHelper</div>
    <p class="lib-desc">Git repository automation via LibGit2Sharp.</p>
  </a>
</div>

<div class="cl-group">Media</div>
<div class="lib-grid">
  <a class="lib-card" href="imaging/index.md">
    <div class="lib-name">CL.Imaging</div>
    <div class="lib-pkg">CodeLogic.Imaging</div>
    <p class="lib-desc">Safe image loading, a fluent editing pipeline, EXIF metadata, palettes, BlurHash, perceptual hashes, and responsive variants.</p>
  </a>
</div>

<div class="cl-group">Utilities</div>
<div class="lib-grid">
  <a class="lib-card" href="common/index.md">
    <div class="lib-name">CL.Common</div>
    <div class="lib-pkg">CodeLogic.Common</div>
    <p class="lib-desc">Encryption, hashing, ID/password generation, JSON, cron, networking.</p>
  </a>
</div>

---

## Shared conventions

The libraries follow a handful of consistent patterns, so once you've used one the rest feel familiar.

### Loading & access

```csharp
await Libraries.LoadAsync<CL.Mail.MailLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mail = Libraries.Get<CL.Mail.MailLibrary>()!;  // resolve after StartAsync()
```

### Result-based APIs

Operations return CodeLogic `Result` / `Result<T>` rather than throwing for expected failures. They are
C# 15 unions, so the compiler checks that a `switch` covers every case:

```csharp
var found = await db.Repository<User>().GetByIdAsync(42);
var text = found switch
{
    User user => user.Name,
    Error e   => $"Lookup failed: {e.Code}",   // NotFoundError, TimeoutError, ... are Error kinds
    null      => "No such user",               // a success without a row
};
```

`IsSuccess` / `Value` / `Error` still work. `ValueOrThrow()` and `ThrowIfFailure()` throw a
`ResultFailedException` that carries the `Error`. Every library works this way, CL.NetUtils included.

### Configuration files

Each library auto-generates its JSON config on first run under its own config directory
(e.g. `…/Libraries/CL.Mail/config.mail.json`). Defaults are written if the file is missing,
so first boot always succeeds and you tune from there.

### Health checks

Every library implements `HealthCheckAsync()`:

```csharp
var status = await mail.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
// status.Message, status.Data (structured metrics)
```

### Databases

Databases use **[CL.Data](data/index.md)**: `CodeLogic.Data` (typed LINQ, repositories, joins, schema sync and
migrations, unified caching, read replicas, resilience, OpenTelemetry, bulk copy) plus one provider package per database: `CodeLogic.Data.MySql`,
`CodeLogic.Data.PostgreSql`, `CodeLogic.Data.SqlServer` and `CodeLogic.Data.Sqlite`. It replaces the 4.x libraries
CL.MySQL2, CL.MSSQL, CL.PostgreSQL and CL.SQLite, which stay on the `support/4.x` line.

See the [API Reference](../api/index.md) for the full generated type and member listing.
