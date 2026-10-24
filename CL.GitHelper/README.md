# CodeLogic.GitHelper

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper)](https://www.nuget.org/packages/CodeLogic.GitHelper)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> Programmatic Git repository management for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — clone, fetch, pull, push, commit, branch, and reset, all from configured repositories.

A thin, async wrapper over [LibGit2Sharp](https://www.nuget.org/packages/LibGit2Sharp) that manages a pool of named repositories. Every operation returns a `GitResult<T>` instead of throwing, carries rich `GitDiagnostics` (timing and transfer counters), and the standout `EnsureUpToDateAsync` gives you one idempotent call that clones a repository if it's missing or hard-resets it to the remote tip if it already exists.

## Install

```bash
dotnet add package CodeLogic.GitHelper
```

## Quick start

```csharp
using CL.GitHelper;

await Libraries.LoadAsync<GitHelperLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var git  = Libraries.Get<GitHelperLibrary>();
var repo = await git.GetRepositoryAsync("Default");

// One idempotent call: clones if missing, else fetches + hard-resets to the remote tip.
GitResult<RepositoryInfo> sync = await repo.EnsureUpToDateAsync();
if (sync.IsSuccess)
    Console.WriteLine($"On {sync.Value!.CurrentBranch} @ {sync.Value.HeadCommitSha[..7]}");
else
    Console.WriteLine($"Sync failed: {sync.ErrorMessage}");
```

## Features

- **Idempotent sync** — `EnsureUpToDateAsync` clones-or-updates in a single call; the easiest way to keep a working copy at the remote tip.
- **Full remote workflow** — `CloneAsync`, `FetchAsync`, `PullAsync`, `PushAsync`, each with an options model and `CancellationToken`.
- **Commit & status** — stage and commit with author metadata (`CommitAsync`), and inspect the working tree (`GetStatusAsync`).
- **Branches** — list local/remote branches and check them out (`ListBranchesAsync`, `CheckoutBranchAsync`).
- **Reset & history** — `ResetHardAsync` and `GetCommitLogAsync`.
- **Batch operations** — `FetchAllAsync` and `GetAllStatusAsync` run across every configured repository with bounded concurrency.
- **Repository caching** — configurable in-memory pool with automatic eviction.
- **No exceptions** — every operation returns a `GitResult<T>` with `IsSuccess`, `Value`, `ErrorMessage`, `Exception`, and `Diagnostics`.

## Configuration

Auto-generated on first run as `config.githelper.json`:

```json
{
  "Enabled": true,
  "BaseDirectory": "",
  "DefaultTimeoutSeconds": 300,
  "MaxConcurrentOperations": 3,
  "EnableRepositoryCaching": true,
  "CacheTimeoutMinutes": 30,
  "Repositories": [
    {
      "Id": "Default",
      "Name": "My Repository",
      "RepositoryUrl": "https://github.com/username/repository.git",
      "LocalPath": "my-repo",
      "DefaultBranch": "main",
      "Username": null,
      "Password": null,
      "AutoFetch": false,
      "AutoFetchIntervalMinutes": 0,
      "TimeoutSeconds": 300
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

## Documentation

Full guide: **[CL.GitHelper documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/githelper.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- LibGit2Sharp 0.30.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
