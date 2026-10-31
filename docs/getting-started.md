# Getting Started

This guide shows how to add a `CL.*` library to a CodeLogic application, configure it, and use it.
Every library follows the same shape, so the steps are identical regardless of which one you pick.

## Prerequisites

- .NET 11 SDK
- A [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) application (`CodeLogic` 5.0+; on .NET 10 use the 4.9.x packages)

## 1. Install

Each library ships as its own NuGet package, named `CodeLogic.<Name>`:

```bash
dotnet add package CodeLogic.MySQL2
dotnet add package CodeLogic.Mail
```

The `CodeLogic` framework package comes in transitively — you don't reference it separately.

## 2. Load & start

Register each library **before** `ConfigureAsync()`, then start the runtime:

```csharp
using CodeLogic;
using static CodeLogic.CodeLogic;

var init = await InitializeAsync(o => { o.AppVersion = "1.0.0"; });
if (!init.Success) return 1;

await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();
await Libraries.LoadAsync<CL.Mail.MailLibrary>();

await ConfigureAsync();   // generates/loads each library's config
await StartAsync();       // runs the Initialize → Start lifecycle
```

Each library runs through the four-phase lifecycle automatically:
**Configure** (register config) → **Initialize** (open connections, build services) →
**Start** (health check, background workers) → **Stop** (graceful shutdown).

## 3. Configure

On first run each library writes a JSON config file with defaults into its own directory under
`Libraries/<LibraryId>/`, for example:

```
Libraries/
  CL.MySQL2/
    config.mysql.json        ← edit, then restart
    config.mysql.cache.json
    logs/
    data/
```

Libraries never share config files or directories. The exact fields for each library are documented
on its page under [Libraries](libs/index.md).

## 4. Use it

Resolve the library after `StartAsync()` and call into it. Most operations return
`Result` / `Result<T>`:

```csharp
var mysql = Libraries.Get<CL.MySQL2.MySQL2Library>();

var repo = mysql.GetRepository<User>();
var insert = await repo.InsertAsync(new User { Name = "Alice", Email = "alice@example.com" });

var found = await mysql.Query<User>()
    .Where(u => u.Email == "alice@example.com")
    .FirstOrDefaultAsync();

if (found.IsSuccess && found.Value is not null)
    Console.WriteLine(found.Value.Name);
```

## Using a library from another library

A library can depend on another. Declare the dependency in your `LibraryManifest` so CodeLogic
orders initialization correctly, then resolve it after the dependency has started:

```csharp
public LibraryManifest Manifest { get; } = new()
{
    Id = "MyApp.Reports",
    Name = "Reports Library",
    Version = "1.0.0",
    Dependencies = [ new LibraryDependency("CL.MySQL2", "4.0.0") ],
};

public Task OnStartAsync(LibraryContext context)
{
    var mysql = Libraries.Get<CL.MySQL2.MySQL2Library>();
    _reports = mysql.GetRepository<Report>();
    return Task.CompletedTask;
}
```

## Health checks

Every library implements `HealthCheckAsync()`:

```csharp
var status = await mysql.HealthCheckAsync();
// status.Status  : Healthy | Degraded | Unhealthy
// status.Message : human-readable summary
// status.Data    : structured metrics (connection counts, etc.)
```

## Next steps

- **[Libraries](libs/index.md)** — the full catalog with a guide for each package.
- **[API Reference](api/index.md)** — generated type/member documentation.
