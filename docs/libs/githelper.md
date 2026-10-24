# CL.GitHelper

> Programmatic Git repository management — clone, fetch, pull, push, commit, branch, and reset across a pool of configured repositories.

`CL.GitHelper` brings Git operations into a CodeLogic 4 application as a managed, async service. It wraps [LibGit2Sharp](https://www.nuget.org/packages/LibGit2Sharp) and exposes a `GitHelperLibrary` surface that hands out `GitRepository` objects keyed by id. Every operation returns a `GitResult<T>` instead of throwing, and each result carries `GitDiagnostics` — timing, object counts, and bytes transferred — so you can log or measure what happened.

| | |
|---|---|
| **Package** | [`CodeLogic.GitHelper`](https://www.nuget.org/packages/CodeLogic.GitHelper) |
| **Library class** | `CL.GitHelper.GitHelperLibrary` |
| **Config file** | `config.githelper.json` |
| **Dependencies** | LibGit2Sharp 0.30.x |

## Install & load

```bash
dotnet add package CodeLogic.GitHelper
```

```csharp
using CL.GitHelper;

await Libraries.LoadAsync<GitHelperLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var git = Libraries.Get<GitHelperLibrary>();
```

## The repository workflow

You don't construct repositories directly. Ask the library for one by id — `GetRepositoryAsync` looks it up in configuration (or the runtime registry), opens or caches the underlying LibGit2Sharp handle, and returns a `GitRepository`.

```csharp
GitRepository repo = await git.GetRepositoryAsync("Default");
```

`GetRepositoryAsync` defaults to the `"Default"` id, so `await git.GetRepositoryAsync()` resolves the first configured repository. The returned object exposes the entire single-repo workflow below. Write operations (clone, pull, push, commit, checkout, reset) are serialized through an internal semaphore so two writers never collide; reads can overlap.

### EnsureUpToDateAsync — the easy path

The single most useful method on a repository. `EnsureUpToDateAsync` is **idempotent**: if the local directory has no repository yet it clones it, and if it does it fetches and hard-resets the working tree to the remote tip. Call it on every startup and you always end up with a clean working copy matching the remote — no branching logic of your own required. To protect uncommitted work it will not hard-reset a dirty tree unless you pass `discardLocalChanges: true` (see the note below).

```csharp
GitResult<RepositoryInfo> result = await repo.EnsureUpToDateAsync();
if (result.IsSuccess)
{
    RepositoryInfo info = result.Value!;
    Console.WriteLine($"{info.Name} on {info.CurrentBranch} @ {info.HeadCommitSha[..7]}");
}
else
{
    Console.WriteLine($"Sync failed: {result.ErrorMessage}");
}
```

The branch defaults to the repository's configured `DefaultBranch`; pass an explicit branch and remote name to override:

```csharp
await repo.EnsureUpToDateAsync(branchName: "develop", remoteName: "origin");
```

> **`EnsureUpToDateAsync` hard-resets the working tree.** To prevent accidental data loss it **refuses to run against a dirty working tree** unless you call it with `discardLocalChanges: true`; only then does it discard local uncommitted changes and reset to the remote tip. Use it for read-only/deploy working copies, not for trees you edit by hand.

## Clone, fetch, pull, push

When you need finer control than `EnsureUpToDateAsync`, the individual remote operations are available, each taking an optional `Git*Options` model (omit it for defaults).

```csharp
// Clone (fails if the local directory already contains a repository)
await repo.CloneAsync(new GitCloneOptions { BranchName = "main" });

// Fetch — bring down remote refs without touching the working tree
await repo.FetchAsync(new GitFetchOptions { Prune = true, FetchTags = true });

// Pull — fetch + integrate using the chosen strategy
await repo.PullAsync(new GitPullOptions { Strategy = MergeStrategy.Merge });

// Push — defaults push the current branch to origin
await repo.PushAsync(new GitPushOptions { Force = false, PushTags = true });
```

### Options models

| Model | Fields |
|-------|--------|
| `GitCloneOptions` | `BranchName?`, `RecurseSubmodules` (`false`), `Bare` (`false`), `OnProgress?`, `CancellationToken` |
| `GitFetchOptions` | `RemoteName` (`"origin"`), `Prune` (`false`), `FetchTags` (`true`), `OnProgress?`, `CancellationToken` |
| `GitPullOptions` | `RemoteName` (`"origin"`), `Strategy` (`MergeStrategy.Merge`), `FetchOptions` (`new()`), `CancellationToken` |
| `GitPushOptions` | `RemoteName` (`"origin"`), `BranchName?`, `Force` (`false`), `PushTags` (`false`), `OnProgress?`, `CancellationToken` |

`MergeStrategy` is `Merge`, `Rebase`, or `FastForwardOnly`. The `OnProgress?` callbacks receive a `GitProgress` (stage, percentage, objects/bytes received) so you can surface transfer progress.

## Commit

`CommitAsync` takes a `GitCommitOptions`. Supply `FilesToStage` to stage specific paths first, or leave it null to commit what is already staged.

```csharp
GitResult<CommitInfo> commit = await repo.CommitAsync(new GitCommitOptions
{
    Message      = "Update configuration",
    AuthorName   = "Build Bot",
    AuthorEmail  = "bot@example.com",
    FilesToStage = ["config.json", "appsettings.json"]
});

if (commit.IsSuccess)
    Console.WriteLine($"Committed {commit.Value!.ShortSha} — {commit.Value.ShortMessage}");
```

`GitCommitOptions` fields: `Message` (`""`), `AuthorName?`, `AuthorEmail?`, `FilesToStage?`. When the author fields are omitted, the repository's configured identity is used.

## Status

`GetStatusAsync` returns a `RepositoryStatus` describing the working tree.

```csharp
GitResult<RepositoryStatus> status = await repo.GetStatusAsync();
if (status.IsSuccess && status.Value!.IsDirty)
{
    foreach (FileStatusEntry entry in status.Value.ModifiedFiles)
        Console.WriteLine($"{entry.Status}  {entry.FilePath}");
}
```

**`RepositoryStatus`** — `IsDirty`, `ModifiedFiles`, `StagedFiles`, `UntrackedFiles`, `IgnoredFiles`, `ConflictedFiles` (all `List<FileStatusEntry>`), `TotalChangedFiles`.

**`FileStatusEntry`** — `FilePath`, `Status`, `IsStaged`, `OldFilePath?` (set for renames).

## Branches

```csharp
GitResult<List<BranchInfo>> branches = await repo.ListBranchesAsync(includeRemote: true);
foreach (BranchInfo b in branches.Value!)
    Console.WriteLine($"{(b.IsCurrent ? "*" : " ")} {b.FriendlyName}  (ahead {b.AheadBy}, behind {b.BehindBy})");

await repo.CheckoutBranchAsync("develop");
```

**`BranchInfo`** — `Name`, `FriendlyName`, `IsCurrent`, `IsRemote`, `IsTracking`, `TrackedBranchName?`, `TipSha`, `AheadBy?`, `BehindBy?`.

## Reset

`ResetHardAsync` discards working-tree and index changes and moves `HEAD` to the tip of the given branch on the remote. With no arguments it targets the currently tracked upstream.

```csharp
await repo.ResetHardAsync();                       // tracked upstream
await repo.ResetHardAsync("main", "origin");       // explicit branch + remote
```

## Commit log

```csharp
GitResult<List<CommitInfo>> log = await repo.GetCommitLogAsync(maxCount: 10);
foreach (CommitInfo c in log.Value!)
    Console.WriteLine($"{c.ShortSha} {c.AuthorName}  {c.ShortMessage}");
```

**`CommitInfo`** — `Sha`, `ShortSha`, `Message`, `ShortMessage`, `AuthorName`, `AuthorEmail`, `AuthorDate`, `CommitterName`, `CommitterEmail`, `CommitDate`, `ParentShas` (`List<string>`).

## Authentication

Credentials are configured **per repository** on the repository entry, and only **HTTPS** is supported.

- **Username + Password** — standard HTTPS basic auth; the `Password` is your token or password.
- **PAT only** — set just `Password` to a Personal Access Token and leave `Username` null. The library sends the username as `x-access-token`, the placeholder GitHub accepts for PAT-only authentication.
- **Anonymous** — leave both fields null for public, read-only access.

```json
{
  "Id": "Private",
  "RepositoryUrl": "https://github.com/org/private.git",
  "LocalPath": "private",
  "DefaultBranch": "main",
  "Username": null,
  "Password": "ghp_yourPersonalAccessToken"
}
```

> **SSH is not supported — use HTTPS + a PAT.** The bundled LibGit2Sharp managed binary has no SSH transport, so SSH cannot work. The `SshKeyPath` and `SshPassphrase` fields exist in `RepositoryConfiguration` but configuring an SSH key only logs a warning, and an SSH clone URL (`git@…` / `ssh://…`) **fails fast** with an explanatory error rather than silently doing nothing. Always use an `https://` URL with a Personal Access Token.

## GitResult&lt;T&gt; and GitDiagnostics

No public method on `GitRepository` or the library throws for an operational failure — failures are returned in the result. Always check `IsSuccess` before reading `Value`.

```csharp
public sealed class GitResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }                  // non-null only on success
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }
    public GitDiagnostics Diagnostics { get; }
}
```

Results are produced with the `GitResult<T>.Ok(value, diagnostics?)` / `GitResult<T>.Fail(error, ex?, diagnostics?)` factories.

**`GitDiagnostics`** — `StartedAt`, `CompletedAt?`, `Duration?`, `ObjectCount`, `BytesTransferred`, `FilesChanged`, `CommitsProcessed`, `Messages` (`List<string>`), `Metrics` (dictionary).

```csharp
var pull = await repo.PullAsync();
if (pull.IsSuccess)
    Console.WriteLine($"Pulled in {pull.Diagnostics.Duration?.TotalSeconds:F2}s, " +
                      $"{pull.Diagnostics.BytesTransferred} bytes");
else
    Console.WriteLine($"Pull failed: {pull.ErrorMessage} ({pull.Exception?.GetType().Name})");
```

**`RepositoryInfo`** (returned by `CloneAsync`, `EnsureUpToDateAsync`, `GetRepositoryInfoAsync`) — `Id`, `Name`, `LocalPath`, `RemoteUrl`, `CurrentBranch`, `HeadCommitSha`, `IsBare`, `State`, `LocalBranchCount`, `RemoteBranchCount`, `TagCount`, `StashCount`, `IsDirty`, `ModifiedFiles`, `StagedFiles`, `UntrackedFiles`.

## Manager and batch operations

`GetManager()` returns the `GitManager` that owns the repository pool, runtime registration, the cache, and cross-repository batch calls. The most common batch helpers are mirrored directly on the library.

```csharp
GitManager manager = git.GetManager();

// Fetch every configured repository (bounded concurrency; 0 = use MaxConcurrentOperations)
Dictionary<string, GitResult<bool>> fetched = await git.FetchAllAsync();

// Status of every configured repository
Dictionary<string, GitResult<RepositoryStatus>> statuses = await git.GetAllStatusAsync();

// Per-repository health check (id -> healthy)
Dictionary<string, bool> health = await manager.HealthCheckAsync();
```

Both `FetchAllAsync` and `GetAllStatusAsync` accept a `maxConcurrency` argument (`0` falls back to the configured `MaxConcurrentOperations`); `FetchAllAsync` also accepts a `FetchOptions`.

### Runtime registration

Add or remove repositories after startup. The manager and the library both expose `RegisterRepository`; removal is on the manager.

```csharp
git.RegisterRepository(new RepositoryConfiguration
{
    Id            = "Docs",
    Name          = "Docs",
    RepositoryUrl = "https://github.com/org/docs.git",
    LocalPath     = "docs",
    DefaultBranch = "main"
});

IReadOnlyList<string> ids   = manager.GetRepositoryIds();
RepositoryConfiguration? cfg = manager.GetConfiguration("Docs");

await manager.UnregisterRepositoryAsync("Docs");
```

### Caching

When `EnableRepositoryCaching` is on, opened repositories are pooled and reused, then evicted after `CacheTimeoutMinutes` of inactivity (`0` = never expire). Inspect or clear the cache via the library or manager.

```csharp
CacheStats? stats = git.GetCacheStats();
if (stats is not null)
{
    Console.WriteLine($"Cached: {stats.TotalCached}, enabled: {stats.CacheEnabled}");
    foreach (CacheEntryStats e in stats.Entries)
        Console.WriteLine($"  {e.RepositoryId}: age {e.Age}, expired {e.IsExpired}");
}

await manager.EvictFromCacheAsync("Docs");   // drop one
await git.ClearCacheAsync();                  // drop all
```

**`CacheStats`** — `CacheEnabled`, `CacheTimeoutMinutes`, `Entries` (`List<CacheEntryStats>`), `TotalCached`. **`CacheEntryStats`** — `RepositoryId`, `Age`, `TimeSinceLastAccess`, `IsExpired`.

## Configuration

The library writes `config.githelper.json` (section `githelper`) with defaults on first run.

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
      "SshKeyPath": null,
      "SshPassphrase": null,
      "AutoFetch": false,
      "AutoFetchIntervalMinutes": 0,
      "TimeoutSeconds": 300
    }
  ]
}
```

### Top-level settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the library is inactive and the health check reports *disabled*. |
| `BaseDirectory` | `string` | `""` | Root directory for relative `LocalPath` values. Empty = the library data directory. |
| `DefaultTimeoutSeconds` | `int` | `300` | Default per-operation timeout (1–3600). |
| `MaxConcurrentOperations` | `int` | `3` | Maximum concurrent operations for batch calls (1–20). |
| `EnableRepositoryCaching` | `bool` | `true` | Keep opened repositories in an in-memory pool. |
| `CacheTimeoutMinutes` | `int` | `30` | Idle eviction window (0–1440). `0` = never expire. |
| `Repositories` | `list` | one entry | The configured repositories (see below). |

### Per-repository settings (`RepositoryConfiguration`)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Id` | `string` | `"Default"` | Lookup key passed to `GetRepositoryAsync`. |
| `Name` | `string` | `"Default Repository"` | Display name. |
| `RepositoryUrl` | `string` | `""` | **Required.** HTTPS clone URL. |
| `LocalPath` | `string` | `""` | **Required.** Absolute, or relative to `BaseDirectory`. |
| `DefaultBranch` | `string` | `"main"` | Branch used by `EnsureUpToDateAsync` / `ResetHardAsync` when none is given. |
| `Username` | `string?` | `null` | HTTPS username (optional with a PAT). |
| `Password` | `string?` | `null` | Password or Personal Access Token (secret). |
| `SshKeyPath` | `string?` | `null` | Reserved — **not currently wired**. |
| `SshPassphrase` | `string?` | `null` | Reserved — **not currently wired** (secret). |
| `AutoFetch` | `bool` | `false` | Enable periodic background fetch. |
| `AutoFetchIntervalMinutes` | `int` | `0` | Interval for auto-fetch; `0` disables it. |
| `TimeoutSeconds` | `int` | `300` | Per-repository operation timeout (1–3600). |

`RepositoryConfiguration.IsValid()` returns `true` when both `RepositoryUrl` and `LocalPath` are non-empty.

### Changing config while running

With CodeLogic 4.9+, `config.githelper.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the repositories that changed are touched:

- **Changed or removed repository**: dropped from the cache. Its old `GitRepository` is disposed once the git operation running on it finishes, and the next `GetRepositoryAsync` opens it with the new settings. Call `GetRepositoryAsync` again rather than keeping an old instance.
- **Added repository**: available at once.
- **Untouched repositories**: keep their open instances.
- **Library-wide settings** (cache, timeouts, concurrency) apply at once. A changed `baseDirectory` re-resolves every relative `localPath`.
- Added or changed repositories with `autoFetch` are fetched in the background.
- Repositories added with `RegisterRepository` are kept across changes and never written to the file.
- **`enabled`** can be switched either way.

## Health check

`HealthCheckAsync()` reports whether the library is operational. When `Enabled` is `false` it reports *disabled* rather than failing. For per-repository status, use the manager's `HealthCheckAsync()`, which returns an id → healthy map.

```csharp
var status = await git.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy

Dictionary<string, bool> perRepo = await git.GetManager().HealthCheckAsync();
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.GitHelper)
