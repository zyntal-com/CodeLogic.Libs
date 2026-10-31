# CodeLogic.GitHelper

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper)](https://www.nuget.org/packages/CodeLogic.GitHelper)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Programmatic Git repository management for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — clone, fetch, pull, push, commit, branch, and reset, all from configured repositories.

A thin, async wrapper over [LibGit2Sharp](https://www.nuget.org/packages/LibGit2Sharp) that manages a pool of named repositories. Every operation returns a CodeLogic `Result<T>` / `Result` instead of throwing, with a typed `Error` (`NotFoundError`, `ValidationError`, ...) and a stable `git.*` code on failure; fetch, pull and push return `GitDiagnostics` (timing and transfer counters) as their value; and the standout `EnsureUpToDateAsync` gives you one idempotent call that clones a repository if it's missing or hard-resets it to the remote tip if it already exists.

## Install

```bash
dotnet add package CodeLogic.GitHelper
```

## Quick start

```csharp
using CL.GitHelper;
using CL.GitHelper.Errors;
using CodeLogic.Core.Results;

await Libraries.LoadAsync<GitHelperLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var git  = Libraries.Get<GitHelperLibrary>();
var repo = await git.GetRepositoryAsync("Default");

// One idempotent call: clones if missing, else fetches + hard-resets to the remote tip.
Result<RepositoryInfo> sync = await repo.EnsureUpToDateAsync();
if (sync.IsSuccess)
    Console.WriteLine($"On {sync.Value!.CurrentBranch} @ {sync.Value.HeadCommitSha[..7]}");
else if (sync.Error is ConflictError { Code: GitErrors.UncommittedChangesCode })
    Console.WriteLine("Local changes present; not resetting.");
else
    Console.WriteLine($"Sync failed: {sync.Error!.Code}: {sync.Error.Message}");
```

## Features

- **Idempotent sync** — `EnsureUpToDateAsync` clones-or-updates in a single call; the easiest way to keep a working copy at the remote tip.
- **Full remote workflow** — `CloneAsync`, `FetchAsync`, `PullAsync`, `PushAsync`, each with an options model and `CancellationToken`.
- **Commit & status** — stage and commit with author metadata (`CommitAsync`), and inspect the working tree (`GetStatusAsync`).
- **Branches** — list local/remote branches and check them out (`ListBranchesAsync`, `CheckoutBranchAsync`).
- **Reset & history** — `ResetHardAsync` and `GetCommitLogAsync`.
- **Batch operations** — `FetchAllAsync` and `GetAllStatusAsync` run across every configured repository with bounded concurrency.
- **Repository caching** — configurable in-memory pool with automatic eviction.
- **No exceptions** — every operation returns a CodeLogic `Result<T>` (or `Result` for checkout/reset). Failures carry an `Error` of the matching kind (`NotFoundError`, `ValidationError`, `UnauthorizedError`, `ConflictError`, `InternalError`, ...) with a stable code from `GitErrors` (e.g. `git.remote_not_found`); caught exceptions are logged and their type name is in `Error.Details`.

## Configuration

Auto-generated on first run as `config.githelper.json`:

```json
{
  "enabled": true,
  "baseDirectory": "",
  "defaultTimeoutSeconds": 300,
  "maxConcurrentOperations": 3,
  "enableRepositoryCaching": true,
  "cacheTimeoutMinutes": 30,
  "repositories": [
    {
      "id": "Default",
      "name": "My Repository",
      "repositoryUrl": "https://github.com/username/repository.git",
      "localPath": "my-repo",
      "defaultBranch": "main",
      "username": null,
      "password": null,
      "autoFetch": false,
      "autoFetchIntervalMinutes": 0,
      "timeoutSeconds": 300
    }
  ]
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch; when `false` the library is inactive and health reports *disabled*. |
| `BaseDirectory` | `""` | Root for relative `LocalPath` values; empty means the library data directory. |
| `DefaultTimeoutSeconds` | `300` | Default per-operation timeout (1–3600). |
| `MaxConcurrentOperations` | `3` | Concurrency cap for batch operations (1–20). |
| `EnableRepositoryCaching` | `true` | Keep opened repositories in an in-memory pool. |
| `CacheTimeoutMinutes` | `30` | Idle eviction window (0–1440; `0` = never expire). |
| `Repositories` | one entry | Per-repository definitions (URL, local path, branch, credentials). |

Each repository entry: `Id`, `Name`, `RepositoryUrl` (required), `LocalPath` (required; absolute or relative to `BaseDirectory`), `DefaultBranch`, `Username`, `Password` (PAT), `AutoFetch`, `AutoFetchIntervalMinutes`, `TimeoutSeconds`. Authentication is HTTPS only — see the full guide for details. SSH key fields exist in config but are **not currently wired**.

### Changing config while running

With CodeLogic 4.9+, `config.githelper.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the repositories that changed are touched:

- **Changed or removed repository**: dropped from the cache. Its old `GitRepository` is disposed once the git operation running on it finishes, and the next `GetRepositoryAsync` opens it with the new settings. Call `GetRepositoryAsync` again rather than keeping an old instance.
- **Added repository**: available at once.
- **Untouched repositories**: keep their open instances.
- **Library-wide settings** (cache, timeouts, concurrency) apply at once. A changed `baseDirectory` re-resolves every relative `localPath`.
- Added or changed repositories with `autoFetch` are fetched in the background.
- Repositories added with `RegisterRepository` are kept across changes and never written to the file.
- **`enabled`** can be switched either way.

## Documentation

Full guide: **[CL.GitHelper documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/githelper.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- LibGit2Sharp 0.30.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
