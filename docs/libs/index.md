# Libraries

The CodeLogic Libraries suite is thirteen independent NuGet packages. Each is a CodeLogic
`ILibrary` — load the ones you need, configure them through their JSON files, and use them from
your application or from other libraries after `StartAsync()`.

<div class="cl-group">Databases</div>
<div class="lib-grid">
  <a class="lib-card" href="mysql2/index.md">
    <div class="lib-name">CL.MySQL2</div>
    <div class="lib-pkg">CodeLogic.MySQL2</div>
    <p class="lib-desc">MySQL / MariaDB / Percona — typed LINQ → SQL, result cache, schema-sync modes, migrations.</p>
  </a>
  <a class="lib-card" href="mssql/index.md">
    <div class="lib-name">CL.MSSQL</div>
    <div class="lib-pkg">CodeLogic.MSSQL</div>
    <p class="lib-desc">SQL Server 2019+ / Azure SQL — typed LINQ, result cache, schema sync, migrations.</p>
  </a>
  <a class="lib-card" href="postgresql/index.md">
    <div class="lib-name">CL.PostgreSQL</div>
    <div class="lib-pkg">CodeLogic.PostgreSQL</div>
    <p class="lib-desc">PostgreSQL — multi-database, repository + query builder, table sync, backups.</p>
  </a>
  <a class="lib-card" href="sqlite/index.md">
    <div class="lib-name">CL.SQLite</div>
    <div class="lib-pkg">CodeLogic.SQLite</div>
    <p class="lib-desc">SQLite — custom pool, WAL, repository + query builder, migration runner.</p>
  </a>
</div>

<div class="cl-group">Communication</div>
<div class="lib-grid">
  <a class="lib-card" href="mail/index.md">
    <div class="lib-name">CL.Mail</div>
    <div class="lib-pkg">CodeLogic.Mail</div>
    <p class="lib-desc">SMTP send, IMAP read + IDLE, and a template engine.</p>
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

<div class="cl-group">Utilities</div>
<div class="lib-grid">
  <a class="lib-card" href="common/index.md">
    <div class="lib-name">CL.Common</div>
    <div class="lib-pkg">CodeLogic.Common</div>
    <p class="lib-desc">Encryption, hashing, ID/password generation, JSON, cron, networking, imaging.</p>
  </a>
</div>

---

## Shared conventions

The libraries follow a handful of consistent patterns, so once you've used one the rest feel familiar.

### Loading & access

```csharp
await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mysql = Libraries.Get<CL.MySQL2.MySQL2Library>();   // resolve after StartAsync()
```

### Result-based APIs

Most operations return `Result` / `Result<T>` rather than throwing for expected failures:

```csharp
var result = await repo.GetByIdAsync(42);
if (result.IsSuccess)
    Use(result.Value);
else
    log.Warn(result.Error?.Message);
```

### Configuration files

Each library auto-generates its JSON config on first run under its own config directory
(e.g. `…/Libraries/CL.MySQL2/config.mysql.json`). Defaults are written if the file is missing,
so first boot always succeeds and you tune from there.

### Health checks

Every library implements `HealthCheckAsync()`:

```csharp
var status = await mysql.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
// status.Message, status.Data (structured metrics)
```

### The database libraries

`CL.MySQL2`, `CL.MSSQL`, `CL.PostgreSQL`, and `CL.SQLite` share the same shape —
`GetRepository<T>()` for CRUD and a fluent query builder (`Query<T>()` on MySQL2, MSSQL and
PostgreSQL, `GetQueryBuilder<T>()` on SQLite) that translates LINQ-style expressions to SQL,
plus attribute-driven table sync.

See the [API Reference](../api/index.md) for the full generated type and member listing.
