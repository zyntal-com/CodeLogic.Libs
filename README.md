<p align="center">
  <img src="logo.svg" alt="CodeLogic Libraries" width="128" height="128">
</p>

<h1 align="center">CodeLogic Libraries</h1>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green.svg" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/.NET-11-512bd4" alt=".NET 11">
  <a href="https://zyntal-com.github.io/CodeLogic.Libs/"><img src="https://img.shields.io/badge/docs-zyntal-com.github.io-2f86ad" alt="Documentation"></a>
</p>

<p align="center">
  Thirteen production-ready .NET 11 library integrations for
  <a href="https://github.com/zyntal-com/CodeLogic">CodeLogic 5</a>.
  Each is a self-contained <code>ILibrary</code> with auto-generated configuration,
  lifecycle management, and health checks.
</p>

## Libraries

| Package | Version | Description |
|---------|---------|-------------|
| [CodeLogic.Common](CL.Common/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.Common?label=)](https://www.nuget.org/packages/CodeLogic.Common) | Utility toolkit — encryption, hashing, ID/password generation, JSON, cron, imaging, networking |
| [CodeLogic.MySQL2](CL.MySQL2/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2?label=)](https://www.nuget.org/packages/CodeLogic.MySQL2) | MySQL / MariaDB / Percona — typed LINQ → SQL, result cache, schema-sync modes, migrations, row locking, database/table admin |
| [CodeLogic.MSSQL](CL.MSSQL/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.MSSQL?label=)](https://www.nuget.org/packages/CodeLogic.MSSQL) | SQL Server 2019+ / Azure SQL — typed LINQ → SQL, caching, schema sync, migrations, row locking, database/table admin |
| [CodeLogic.PostgreSQL](CL.PostgreSQL/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL?label=)](https://www.nuget.org/packages/CodeLogic.PostgreSQL) | PostgreSQL — multi-database, repository + query builder, joins, cursor paging, caching, migrations, schema sync, row locking, database/table admin |
| [CodeLogic.SQLite](CL.SQLite/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite?label=)](https://www.nuget.org/packages/CodeLogic.SQLite) | SQLite — connection pool, WAL, repository + query builder, migration ledger, database/table admin |
| [CodeLogic.Mail](CL.Mail/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mail?label=)](https://www.nuget.org/packages/CodeLogic.Mail) | SMTP send, IMAP read + IDLE, and a lightweight template engine |
| [CodeLogic.Storage](CL.Storage/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.Storage?label=)](https://www.nuget.org/packages/CodeLogic.Storage) | Safe mounted local, S3, FTP/SFTP, WebDAV, Azure, GCS, and Swift storage, with verified and resumable transfers, a durable transfer queue, three-way sync, and change watching |
| [CodeLogic.SocialConnect](CL.SocialConnect/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect?label=)](https://www.nuget.org/packages/CodeLogic.SocialConnect) | Discord webhooks + Steam Web API (profiles, bans, games, ticket auth) |
| [CodeLogic.NetUtils](CL.NetUtils/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.NetUtils?label=)](https://www.nuget.org/packages/CodeLogic.NetUtils) | DNSBL blacklist checking + MaxMind GeoIP2 location lookups |
| [CodeLogic.GameNetQuery](CL.GameNetQuery/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.GameNetQuery?label=)](https://www.nuget.org/packages/CodeLogic.GameNetQuery) | Game-server queries — Valve A2S/RCON (CS2/CSS) and Minecraft UDP/RCON |
| [CodeLogic.SystemStats](CL.SystemStats/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SystemStats?label=)](https://www.nuget.org/packages/CodeLogic.SystemStats) | Cross-platform CPU, memory, process, and uptime stats |
| [CodeLogic.GitHelper](CL.GitHelper/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper?label=)](https://www.nuget.org/packages/CodeLogic.GitHelper) | Git repository automation via LibGit2Sharp |
| [CodeLogic.TwoFactorAuth](CL.TwoFactorAuth/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth?label=)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth) | TOTP 2FA with QR-code generation |

All packages target **.NET 11** and depend on **CodeLogic 5.0** (range `[5.0.0, 6.0.0)`). On .NET 10, use the 4.9.x packages (CodeLogic `[4.9.0, 5.0.0)`), maintained on the `support/4.x` branch.

Every library and the CodeLogic framework itself share one version line: **`4.9.<patch>`**.
The `major.minor` is hand-controlled in the repo-root `version.txt`; the patch is the CI run
number. See [Versioning](docs/versioning.md) for the full scheme.

## Quick start

### 1. Install

```bash
# The CodeLogic framework comes in transitively — install only the libraries you need.
dotnet add package CodeLogic.MySQL2
dotnet add package CodeLogic.Mail
```

### 2. Load & start

Register each library **before** `ConfigureAsync()`, then run the lifecycle:

```csharp
using CodeLogic;

var init = await CodeLogic.InitializeAsync(o => { o.AppVersion = "1.0.0"; });
if (!init.Success) return 1;

await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();
await Libraries.LoadAsync<CL.Mail.MailLibrary>();

await CodeLogic.ConfigureAsync();   // generates/loads each library's config
await CodeLogic.StartAsync();       // runs Initialize → Start
```

Every library runs the same four-phase lifecycle automatically:
**Configure** (register config) → **Initialize** (open connections, build services) →
**Start** (health check, background workers) → **Stop** (graceful shutdown).

### 3. Configure

On first run each library writes a JSON config file with defaults into its own directory under
`Libraries/<LibraryId>/` — for example `config.mysql.json`, `config.mail.json`. Edit and restart.
Validation runs on startup, so invalid config stops the boot with a clear error.

### 4. Use

```csharp
var mysql = Libraries.Get<CL.MySQL2.MySQL2Library>();

var users = await mysql.Query<UserRecord>()
    .Where(u => u.IsActive && u.Role == "admin")
    .OrderBy(u => u.Name)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToPagedListAsync(page: 1, pageSize: 20);
```

## How libraries work

Every CodeLogic library follows the same shape:

1. **`ILibrary` implementation** — the lifecycle hooks.
2. **Config model** (`ConfigModelBase`) — auto-generated JSON, validated on startup.
3. **Service classes** — the actual functionality, reached through the library instance.
4. **Health check** — reports Healthy / Degraded / Unhealthy via `HealthCheckAsync()`.

Libraries load before your application starts and stop after it stops, so your code can always
rely on database connections, mail services, and the rest being available.

```csharp
// Resolve a loaded library anywhere in your code:
var mysql = Libraries.Get<CL.MySQL2.MySQL2Library>();
var repo  = mysql.GetRepository<UserRecord>();
```

Most operations return `Result` / `Result<T>` rather than throwing for expected failures
(a few libraries — Mail, GitHelper, NetUtils — use their own result types; see their docs).

## Documentation

📖 **Full documentation: [zyntal-com.github.io/CodeLogic.Libs](https://zyntal-com.github.io/CodeLogic.Libs/)**

- [Getting Started](https://zyntal-com.github.io/CodeLogic.Libs/getting-started.html)
- [All Libraries](https://zyntal-com.github.io/CodeLogic.Libs/libs/) — per-library guides
- [API Reference](https://zyntal-com.github.io/CodeLogic.Libs/api/) — generated from source XML docs

Each package also ships a focused `README.md` and `CHANGELOG.md` in its folder.

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic)
- .NET 11 SDK or later

## License

MIT — see [LICENSE](LICENSE).
