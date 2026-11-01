# CL.GitHelper

> Programmatic Git repository management — clone, fetch, pull, push, commit, branch, and reset across a pool of configured repositories.

`CL.GitHelper` brings Git operations into a CodeLogic application as a managed, async service. It wraps [LibGit2Sharp](https://www.nuget.org/packages/LibGit2Sharp) and exposes a `GitHelperLibrary` surface that hands out `GitRepository` objects keyed by id. Every operation returns a CodeLogic `Result<T>` (or plain `Result`) instead of throwing; a failure carries a typed `Error` with a stable `git.*` code. Fetch, pull and push return `GitDiagnostics` — timing, object counts, and bytes transferred — as their success value, so you can log or measure what happened.

| | |
|---|---|
| **Package** | [`CodeLogic.GitHelper`](https://www.nuget.org/packages/CodeLogic.GitHelper) |
| **Library class** | `CL.GitHelper.GitHelperLibrary` |
| **Config file** | `config.githelper.json` |
| **Dependencies** | LibGit2Sharp 0.32.x |

## Install & load

```bash
dotnet add package CodeLogic.GitHelper
```

```csharp
using CL.GitHelper;
using CL.GitHelper.Errors;       // GitErrors code constants
using CodeLogic.Core.Results;    // Result<T>, Result, Error kinds

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
Result<RepositoryInfo> result = await repo.EnsureUpToDateAsync();
if (result.IsSuccess)
{
    RepositoryInfo info = result.Value!;
    Console.WriteLine($"{info.Name} on {info.CurrentBranch} @ {info.HeadCommitSha[..7]}");
}
else
{
    Console.WriteLine($"Sync failed: {result.Error!.Code}: {result.Error.Message}");
}
```

The branch defaults to the repository's configured `DefaultBranch`; pass an explicit branch and remote name to override:

```csharp
await repo.EnsureUpToDateAsync(branchName: "develop", remoteName: "origin");
```

> **`EnsureUpToDateAsync` hard-resets the working tree.** To prevent accidental data loss it **refuses to run against a dirty working tree** (returning a `ConflictError` with code `git.uncommitted_changes`) unless you call it with `discardLocalChanges: true`; only then does it discard local uncommitted changes and reset to the remote tip. Use it for read-only/deploy working copies, not for trees you edit by hand.

## Clone, fetch, pull, push

When you need finer control than `EnsureUpToDateAsync`, the individual remote operations are available, each taking an optional `Git*Options` model (omit it for defaults).

```csharp
// Clone (fails if the local directory already contains a repository)
await repo.CloneAsync(new GitCloneOptions { BranchName = "main" });

// Fetch — bring down remote refs without touching the working tree
Result<GitDiagnostics> fetch = await repo.FetchAsync(new GitFetchOptions { Prune = true, FetchTags = true });

// Pull — fetch + integrate using the chosen strategy
Result<GitDiagnostics> pull = await repo.PullAsync(new GitPullOptions { Strategy = MergeStrategy.Merge });

// Push — defaults push the current branch to origin
Result<GitDiagnostics> push = await repo.PushAsync(new GitPushOptions { Force = false, PushTags = true });
```

`CloneAsync` returns `Result<RepositoryInfo>`; fetch, pull and push return `Result<GitDiagnostics>` — see [Results and errors](#results-and-errors).

### Options models

| Model | Fields |
|-------|--------|
| `GitCloneOptions` | `BranchName?`, `RecurseSubmodules` (`false`), `Bare` (`false`), `OnProgress?`, `CancellationToken` |
| `GitFetchOptions` | `RemoteName` (`"origin"`), `Prune` (`false`), `FetchTags` (`true`), `OnProgress?`, `CancellationToken` |
| `GitPullOptions` | `RemoteName` (`"origin"`), `Strategy` (`MergeStrategy.Merge`), `FetchOptions` (`new()`), `CancellationToken` |
| `GitPushOptions` | `RemoteName` (`"origin"`), `BranchName?`, `Force` (`false`), `PushTags` (`false`), `OnProgress?`, `CancellationToken` |

`MergeStrategy` is `Merge`, `Rebase`, or `FastForwardOnly`. The `OnProgress?` callbacks receive a `GitProgress` (stage, percentage, objects/bytes received) so you can surface transfer progress. They run on a libgit2 worker thread; an exception they throw is logged and ignored.

**Pull.** `PullAsync` fetches from `RemoteName`, then integrates the current branch's upstream on that remote (or the remote branch with the same name when the branch has no upstream there):

- `Merge` fast-forwards when it can and otherwise creates a merge commit. When the merge stops on conflicts it returns a `ConflictError` (`git.merge_conflict`) and leaves the conflicted files in the working tree, as `git pull` does; `ResetHardAsync` discards them.
- `Rebase` replays local commits on top of the fetched tip. A rebase that stops on conflicts is aborted, leaving the branch as it was, and returns `git.merge_conflict`.
- `FastForwardOnly` returns `git.non_fast_forward` when the branches have diverged and changes nothing.

When `RemoteName` is left at `"origin"`, `FetchOptions.RemoteName` is used instead. `FetchOptions` also supplies `Prune`, `FetchTags`, `OnProgress` and, when set, the fetch step's cancellation token. A branch with no upstream on the remote returns `git.branch_not_found`.

**Push.** `PushAsync` sends the configured credentials. `PushTags` also pushes every local tag; tags are never force-pushed, even with `Force`. A reference the remote refuses (for example a protected branch) returns a `ConflictError` with `git.push_rejected` and the remote's reason.

### Cancellation and timeouts

The `CancellationToken` on each options model is checked by libgit2 at every progress callback, so cancelling stops a clone, fetch or push that is already transferring data and returns `git.cancelled`. Each repository's network operations (clone, fetch, the fetch step of pull, push) are also bounded by `TimeoutSeconds` (or `DefaultTimeoutSeconds` when it is `0`); a transfer that runs longer returns a `TimeoutError` with `git.timeout`. Both take effect at the next progress callback: a connection that hangs before any data arrives is bounded only by the operating system's network timeouts. A clone that fails or is cancelled removes the directory it created.

## Commit

`CommitAsync` takes a `GitCommitOptions`. Supply `FilesToStage` to stage specific paths first, or leave it null to commit what is already staged.

```csharp
Result<CommitInfo> commit = await repo.CommitAsync(new GitCommitOptions
{
    Message      = "Update configuration",
    AuthorName   = "Build Bot",
    AuthorEmail  = "bot@example.com",
    FilesToStage = ["config.json", "appsettings.json"]
});

if (commit.IsSuccess)
    Console.WriteLine($"Committed {commit.Value!.ShortSha} — {commit.Value.ShortMessage}");
```

`GitCommitOptions` fields: `Message` (`""`), `AuthorName?`, `AuthorEmail?`, `FilesToStage?`. When the author fields are omitted, the repository's configured identity is used. An empty `Message` returns a `ValidationError` (`git.empty_commit_message`); a commit with no changes returns `git.nothing_to_commit`.

## Status

`GetStatusAsync` returns a `RepositoryStatus` describing the working tree.

```csharp
Result<RepositoryStatus> status = await repo.GetStatusAsync();
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
Result<List<BranchInfo>> branches = await repo.ListBranchesAsync(includeRemote: true);
foreach (BranchInfo b in branches.Value!)
    Console.WriteLine($"{(b.IsCurrent ? "*" : " ")} {b.FriendlyName}  (ahead {b.AheadBy}, behind {b.BehindBy})");

Result checkout = await repo.CheckoutBranchAsync("develop");
if (checkout.Error is NotFoundError)
    Console.WriteLine("No such branch");   // code git.branch_not_found
```

**`BranchInfo`** — `Name`, `FriendlyName`, `IsCurrent`, `IsRemote`, `IsTracking`, `TrackedBranchName?`, `TipSha`, `AheadBy?`, `BehindBy?`.

## Reset

`ResetHardAsync` discards working-tree and index changes and moves `HEAD` to the tip of the given branch on the remote. With no arguments it targets the currently tracked upstream.

```csharp
Result reset = await repo.ResetHardAsync();        // tracked upstream
await repo.ResetHardAsync("main", "origin");       // explicit branch + remote
```

An unresolvable target returns a `NotFoundError` with code `git.branch_not_found`.

## Commit log

```csharp
Result<List<CommitInfo>> log = await repo.GetCommitLogAsync(maxCount: 10);
foreach (CommitInfo c in log.Value!)
    Console.WriteLine($"{c.ShortSha} {c.AuthorName}  {c.ShortMessage}");
```

**`CommitInfo`** — `Sha`, `ShortSha`, `Message`, `ShortMessage`, `AuthorName`, `AuthorEmail`, `AuthorDate`, `CommitterName`, `CommitterEmail`, `CommitDate`, `ParentShas` (`List<string>`).

## Authentication

Credentials are configured **per repository** on the repository entry, and only **HTTPS** is supported.

- **Username + Password** — standard HTTPS basic auth; the `Password` is your token or password.
- **PAT only** — set just `Password` to a Personal Access Token and leave `Username` null. The library sends the username as `x-access-token`, the placeholder GitHub accepts for PAT-only authentication.
- **Anonymous** — leave both fields null for public, read-only access.

Clone, fetch, pull and push all use these credentials. They are sent **only to the scheme, host and port of `RepositoryUrl`**: a submodule or second remote on another host, a redirect to another host, or a plain-`http` URL on the same host gets no token, and that request fails with `git.authentication_failed` (a warning is logged). URLs with embedded credentials (`https://user:token@host/...`) and the configured token are redacted from log lines, error messages and `RepositoryInfo.RemoteUrl`.

```json
{
  "id": "Private",
  "repositoryUrl": "https://github.com/org/private.git",
  "localPath": "private",
  "defaultBranch": "main",
  "username": null,
  "password": "ghp_yourPersonalAccessToken"
}
```

> **SSH is not supported — use HTTPS + a PAT.** The bundled LibGit2Sharp managed binary has no SSH transport, so SSH cannot work. The `SshKeyPath` and `SshPassphrase` fields exist in `RepositoryConfiguration` but configuring an SSH key only logs a warning, and an SSH clone URL (`git@…` / `ssh://…`) **fails fast** with an explanatory error rather than silently doing nothing. Always use an `https://` URL with a Personal Access Token.

## Results and errors

No public method on `GitRepository` or the library throws for an operational failure — failures are returned as CodeLogic results (`CodeLogic.Core.Results`). Check `IsSuccess` before reading `Value`, or `switch` on the result.

| Method | Returns |
|--------|---------|
| `CloneAsync`, `EnsureUpToDateAsync`, `GetRepositoryInfoAsync` | `Result<RepositoryInfo>` |
| `FetchAsync`, `PullAsync`, `PushAsync` | `Result<GitDiagnostics>` |
| `CommitAsync` | `Result<CommitInfo>` |
| `GetStatusAsync` | `Result<RepositoryStatus>` |
| `ListBranchesAsync` | `Result<List<BranchInfo>>` |
| `GetCommitLogAsync` | `Result<List<CommitInfo>>` |
| `CheckoutBranchAsync`, `ResetHardAsync` | `Result` |
| `FetchAllAsync` | `Dictionary<string, Result<GitDiagnostics>>` |
| `GetAllStatusAsync` | `Dictionary<string, Result<RepositoryStatus>>` |

A failure's `Error` has a kind (its subtype) and a stable `Code`; the codes are constants on `CL.GitHelper.Errors.GitErrors`. `Message` is the human-readable text. When the failure came from a caught exception, the library logs the exception and `Details` holds its type name (e.g. `NotFoundException`).

| Kind | Codes |
|------|-------|
| `NotFoundError` | `git.repository_not_found` (not cloned yet), `git.remote_not_found`, `git.branch_not_found` (checkout or reset target), `git.not_found` (e.g. a file to stage) |
| `ValidationError` | `git.empty_commit_message`, `git.nothing_to_commit`, `git.ssh_not_supported` |
| `UnauthorizedError` | `git.authentication_failed` |
| `ConflictError` | `git.directory_not_empty` (clone target), `git.uncommitted_changes` (`EnsureUpToDateAsync`), `git.checkout_conflict`, `git.non_fast_forward` |
| `ConflictError` (pull, push) | `git.merge_conflict` (pull stopped on conflicts), `git.push_rejected` (the remote refused a reference) |
| `TimeoutError` | `git.timeout` (a network operation ran past `TimeoutSeconds`) |
| `UnavailableError` | `git.cancelled` |
| `InternalError` | `git.clone_failed`, `git.fetch_failed`, `git.pull_failed`, `git.push_failed`, `git.commit_failed`, `git.status_failed`, `git.info_failed`, `git.list_branches_failed`, `git.checkout_failed`, `git.reset_failed`, `git.log_failed` |

```csharp
Result<GitDiagnostics> pull = await repo.PullAsync();
var line = pull.Error switch
{
    null                => $"Pulled in {pull.Value!.Duration?.TotalSeconds:F2}s",
    UnauthorizedError   => "Check the repository's PAT",
    NotFoundError e     => $"Missing: {e.Message} ({e.Code})",
    var e               => $"Pull failed: {e.Message} ({e.Details})"
};
```

**`GitDiagnostics`** (the value of fetch, pull and push) — `StartedAt`, `CompletedAt?`, `Duration?`, `ObjectCount`, `BytesTransferred` (filled by fetch, pull and push), `FilesChanged`, `CommitsProcessed`, `Messages` (`List<string>`; a pull includes its fetch step's messages), `Metrics` (dictionary).

**`RepositoryInfo`** (returned by `CloneAsync`, `EnsureUpToDateAsync`, `GetRepositoryInfoAsync`) — `Id`, `Name`, `LocalPath`, `RemoteUrl`, `CurrentBranch`, `HeadCommitSha`, `IsBare`, `State`, `LocalBranchCount`, `RemoteBranchCount`, `TagCount`, `StashCount`, `IsDirty`, `ModifiedFiles`, `StagedFiles`, `UntrackedFiles`. `RemoteUrl` has any credentials in the URL replaced by `***`. A bare repository counts as cloned; its `IsBare` is `true` and the working-tree counters are 0, and `EnsureUpToDateAsync` on it only fetches.

## Manager and batch operations

`GetManager()` returns the `GitManager` that owns the repository pool, runtime registration, the cache, and cross-repository batch calls. The most common batch helpers are mirrored directly on the library.

```csharp
GitManager manager = git.GetManager();

// Fetch every configured repository (bounded concurrency; 0 = use MaxConcurrentOperations)
Dictionary<string, Result<GitDiagnostics>> fetched = await git.FetchAllAsync();

// Status of every configured repository
Dictionary<string, Result<RepositoryStatus>> statuses = await git.GetAllStatusAsync();

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

When `EnableRepositoryCaching` is on, opened repositories are pooled and reused, then evicted after `CacheTimeoutMinutes` of inactivity (`0` = never expire). Inspect or clear the cache via the library or manager. An evicted repository is disposed once the operation running on it (if any) has finished.

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
      "sshKeyPath": null,
      "sshPassphrase": null,
      "autoFetch": false,
      "autoFetchIntervalMinutes": 0,
      "timeoutSeconds": 300
    }
  ]
}
```

### Top-level settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the library is inactive and the health check reports *disabled*. |
| `BaseDirectory` | `string` | `""` | Root directory for relative `LocalPath` values. Empty = the library data directory. |
| `DefaultTimeoutSeconds` | `int` | `300` | Network operation timeout for repositories whose `TimeoutSeconds` is `0` (1–3600). |
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
| `AutoFetch` | `bool` | `false` | Fetch once when the library starts (and when the repository is added or changed live). |
| `AutoFetchIntervalMinutes` | `int` | `0` | Fetch in the background every *n* minutes while the library runs; `0` disables it. A fetch still running at the next tick is not started twice, and stopping the library cancels it. |
| `TimeoutSeconds` | `int` | `300` | Timeout for this repository's network operations (0–3600; `0` = `DefaultTimeoutSeconds`). |

`RepositoryConfiguration.IsValid()` returns `true` when both `RepositoryUrl` and `LocalPath` are non-empty. `GitHelperConfig.Validate()` rejects an empty repository list, duplicate repository ids (case-insensitive), a negative `AutoFetchIntervalMinutes` and out-of-range timeouts.

### Changing config while running

Config changes apply live: `config.githelper.json` applies live when changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only the repositories that changed are touched:

- **Changed or removed repository**: dropped from the cache. Its old `GitRepository` is disposed once the git operation running on it finishes, and the next `GetRepositoryAsync` opens it with the new settings. Call `GetRepositoryAsync` again rather than keeping an old instance.
- **Added repository**: available at once.
- **Untouched repositories**: keep their open instances.
- **Library-wide settings** (cache, timeouts, concurrency) apply at once. A changed `baseDirectory` re-resolves every relative `localPath`.
- Added or changed repositories with `autoFetch` are fetched in the background, and `autoFetchIntervalMinutes` timers are added, retimed or stopped.
- Repositories added with `RegisterRepository` are kept across changes and never written to the file.
- **`enabled`** can be switched either way.
- A configuration that fails `Validate()` is rejected, as an invalid file stops initialization: the library logs the errors and keeps its current settings.

## Health check

`HealthCheckAsync()` reports whether the library is operational. When `Enabled` is `false` it reports `Healthy` with a message saying the library is disabled, rather than `Unhealthy`. For per-repository status, use the manager's `HealthCheckAsync()`, which returns an id → healthy map.

```csharp
var status = await git.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy

Dictionary<string, bool> perRepo = await git.GetManager().HealthCheckAsync();
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.GitHelper)
