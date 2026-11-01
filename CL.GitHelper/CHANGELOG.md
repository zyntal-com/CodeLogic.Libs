# CL.GitHelper — Changelog

All notable changes to **CodeLogic.GitHelper** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). This library now returns those types
  directly (see below), so its results can be matched exhaustively with `switch`.
- **`GitResult<T>` is removed**; every operation returns CodeLogic's `Result<T>` / `Result`
  (`CodeLogic.Core.Results`). `ErrorMessage` → `Error.Message`; `Exception` is gone (the library
  logs it, and `Error.Details` holds the exception type name); `Diagnostics` is gone (see below).
- Signature changes on `GitRepository`:
  - `FetchAsync`, `PullAsync`, `PushAsync`: `Task<GitResult<bool>>` → `Task<Result<GitDiagnostics>>`.
    The diagnostics are now the success value. A pull's diagnostics include its fetch step's
    messages, and a failed fetch inside a pull returns the fetch's own error.
  - `CheckoutBranchAsync`, `ResetHardAsync`: `Task<GitResult<bool>>` → `Task<Result>`.
  - `CloneAsync`, `EnsureUpToDateAsync`, `GetRepositoryInfoAsync`: `Task<GitResult<RepositoryInfo>>`
    → `Task<Result<RepositoryInfo>>`. `CommitAsync` → `Task<Result<CommitInfo>>`, `GetStatusAsync`
    → `Task<Result<RepositoryStatus>>`, `ListBranchesAsync` → `Task<Result<List<BranchInfo>>>`,
    `GetCommitLogAsync` → `Task<Result<List<CommitInfo>>>`. Clone, commit and status no longer
    return diagnostics; clone logs its duration and messages instead.
- `GitHelperLibrary` / `GitManager`: `FetchAllAsync` → `Task<Dictionary<string, Result<GitDiagnostics>>>`,
  `GetAllStatusAsync` → `Task<Dictionary<string, Result<RepositoryStatus>>>`.
- Failures have a kind and a stable code, all listed as constants on the new
  `CL.GitHelper.Errors.GitErrors`:
  - `NotFoundError`: `git.repository_not_found` (not cloned), `git.remote_not_found`,
    `git.branch_not_found` (checkout or reset target), `git.not_found`.
  - `ValidationError`: `git.empty_commit_message`, `git.nothing_to_commit`, `git.ssh_not_supported`.
  - `UnauthorizedError`: `git.authentication_failed`.
  - `ConflictError`: `git.directory_not_empty`, `git.uncommitted_changes`, `git.checkout_conflict`,
    `git.non_fast_forward`.
  - `UnavailableError`: `git.cancelled`.
  - `InternalError` (anything else): `git.clone_failed`, `git.fetch_failed`, `git.pull_failed`,
    `git.push_failed`, `git.commit_failed`, `git.status_failed`, `git.info_failed`,
    `git.list_branches_failed`, `git.checkout_failed`, `git.reset_failed`, `git.log_failed`.

### Changed

- Fetch now records `ObjectCount` and `BytesTransferred` in its diagnostics.
- Failures of read operations (status, info, branches, log) are logged at debug level with the
  full exception; before, the exception was only on the result.
- LibGit2Sharp is pinned to 0.32.0 instead of floating (`0.32.*`).

### Documentation

- Configuration examples in the README and the docs use the camelCase keys the CodeLogic loader reads.
  The PascalCase keys shown before are matched case-sensitively and were ignored, leaving defaults active.

### Fixed

- **Cache eviction no longer disposes a repository under a running operation.** The idle-expiry timer,
  `GetRepositoryAsync` replacing a stale entry, `EvictFromCacheAsync` and `ClearCacheAsync` disposed the
  evicted `GitRepository` at once. Idle time only counts `GetRepositoryAsync` calls, so a clone or fetch that ran
  longer than `CacheTimeoutMinutes` had its LibGit2 handle and lock disposed underneath it (the operation's
  lock release then threw `ObjectDisposedException`). Evicted instances are now disposed once their current
  operation finishes, as config changes and disabling the library already did.
- **Push and pull use the configured credentials.** `PushAsync` passed no credentials provider, and
  `PullAsync` called `Commands.Pull` without fetch options, so every push, and every pull of a private
  repository, failed with `git.authentication_failed` even with a PAT configured.
- **Documented options are honoured.** `PullOptions.Strategy` (merge, rebase or fast-forward only),
  `PullOptions.RemoteName`, `PushOptions.PushTags`, the `OnProgress` callbacks of clone, fetch and push,
  `RepositoryConfiguration.TimeoutSeconds` / `GitHelperConfig.DefaultTimeoutSeconds` and
  `RepositoryConfiguration.AutoFetchIntervalMinutes` were never read. See the pull, push, timeout and
  auto-fetch notes below.
- **Cancellation aborts a running transfer.** The token only stopped an operation that had not started
  yet; libgit2's progress callbacks now return `false` once it is cancelled, so a clone, fetch or push
  stops mid-transfer with `git.cancelled`.
- **Bare repositories count as cloned.** `EnsureUpToDateAsync` and every operation looked for a `.git`
  directory, so a bare repository (or a worktree with a `.git` file) was reported as not cloned and
  `EnsureUpToDateAsync` tried to clone into it. `EnsureUpToDateAsync` on a bare repository now only
  fetches, and `RepositoryInfo` for it no longer throws.
- **The health check reports a disabled library as `Healthy`** ("disabled") instead of `Unhealthy`.
- **Live config changes are validated.** A change that fails `GitHelperConfig.Validate()` is logged and
  ignored, keeping the current settings, just as an invalid file stops initialization.
- A clone that fails or is cancelled removes the directory it created, so the next attempt does not fail
  with `git.directory_not_empty`.

### Security

- **The PAT is sent only to the configured host.** The credentials callback answered every request,
  so a submodule, second remote or redirect on another host received the token. It now hands it out only
  when the request's scheme, host and port match `RepositoryUrl` (no `https` → `http` downgrade); other
  hosts get empty credentials and fail with `git.authentication_failed`, and a warning is logged.
- **Credentials are redacted.** URLs with embedded credentials (`https://user:token@host/…`) were logged
  verbatim ("Cloning …") and could appear in error messages. User info in URLs and the configured token
  are now replaced by `***` in log lines, `Error.Message` and `RepositoryInfo.RemoteUrl`.

### Added

- Error codes `git.timeout` (`TimeoutError`), `git.merge_conflict` and `git.push_rejected` (`ConflictError`)
  on `GitErrors`.
- **Pull strategies.** `Merge` fast-forwards or creates a merge commit; on conflicts it returns
  `git.merge_conflict` and leaves the conflicted files in place, as `git pull` does. `Rebase` replays
  local commits on the fetched tip and aborts on conflicts. `FastForwardOnly` returns
  `git.non_fast_forward` when the branches diverged. The upstream is the branch's tracking branch on
  `RemoteName`, else the remote branch of the same name (`git.branch_not_found` when neither exists).
  `PullOptions.RemoteName` selects the remote; when left at `"origin"`, `FetchOptions.RemoteName` is
  used as before.
- **Push.** `PushTags` pushes every local tag (tags are never forced). References the remote refuses
  return `git.push_rejected` with its reason; before, such a push reported success.
- **Timeouts.** Clone, fetch, the fetch step of pull, and push stop after the repository's
  `TimeoutSeconds` and return `git.timeout`. `TimeoutSeconds` now accepts `0`, meaning
  `DefaultTimeoutSeconds`. The timeout is checked at progress callbacks, so it cannot interrupt a
  connection that hangs before any data arrives.
- **Background fetch.** `AutoFetchIntervalMinutes > 0` starts a fetch timer per repository when the
  library starts. Config changes and `RegisterRepository` add, retime or remove timers; a fetch still
  running at the next tick is skipped; stopping or disabling the library cancels running fetches.
- `GitHelperConfig.Validate()` also rejects duplicate repository ids (case-insensitive), a negative
  `AutoFetchIntervalMinutes` and a `TimeoutSeconds` outside 0–3600. A config file with duplicate ids
  used to load (the first entry won); it now fails validation.
- Push and pull diagnostics carry `ObjectCount` and `BytesTransferred`; pull adds a `MergeStatus` or
  `RebaseStatus` metric.

## 2026-10-24

### Added

- **Live config changes** (requires CodeLogic 4.9+). `GitHelperLibrary` implements `IConfigReloadable`:
  a change to `config.githelper.json` made through `Libraries.UpdateConfigAsync`,
  `Libraries.ReloadConfigAsync` or the config file watcher is applied without a restart, touching only
  the repositories that changed. Changed or removed repositories are evicted from the cache and their
  old `GitRepository` is disposed once its running git operation finishes; untouched repositories keep
  their instances; library-wide settings and `baseDirectory` apply at once; added or changed
  repositories with `autoFetch` are fetched in the background. `Enabled` can be switched either way.
  The `RequiresRestart` hints on `id`, `repositoryUrl`, `localPath` and `baseDirectory` were removed.

### Changed

- `GitManager` keeps its own copy of the configuration. Repositories added with `RegisterRepository`
  no longer appear in `Configuration.Get<GitHelperConfig>()` (and so can no longer be written to
  `config.githelper.json` by a later save); they are kept across config changes.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- SSH is not supported by the bundled LibGit2Sharp transport; configuring an SSH
  key now logs a clear warning and cloning an SSH URL (`git@…` / `ssh://…`)
  fails fast with an explanatory message instead of silently doing nothing.
- `EnsureUpToDateAsync` now refuses to hard-reset (and discard) a dirty working
  tree unless called with `discardLocalChanges: true`, preventing accidental
  data loss.
- The repository cache now expires entries by idle time rather than absolute
  age, so a repository in active use is no longer disposed out from under a
  caller; batch concurrency is clamped to at least 1; `CommitOptions` gained a
  `CancellationToken`.

### Documentation

- Full house-style rewrite of the README and the `docs/libs/githelper.md` deep guide.
- Documented the repository workflow around `GetRepositoryAsync` and highlighted
  `EnsureUpToDateAsync` as the idempotent clone-or-update easy path.
- Documented the per-operation options models (`GitCloneOptions`, `GitFetchOptions`,
  `GitPullOptions`, `GitPushOptions`, `GitCommitOptions`) and the `MergeStrategy` enum.
- Documented the `GitResult<T>` / `GitDiagnostics` contract and the result-bearing models
  (`RepositoryInfo`, `CommitInfo`, `BranchInfo`, `RepositoryStatus`, `FileStatusEntry`).
- Documented manager batch operations (`FetchAllAsync`, `GetAllStatusAsync`), runtime
  registration, repository caching (`CacheStats` / `CacheEntryStats`), and the health check.
- Clarified authentication: HTTPS only, PAT-only auth sends `x-access-token`, and the SSH
  key fields are present in config but not currently wired.

## [4.5.2] — 2026-06-20

### Documentation

- Documented the full `GitRepository` workflow in the README: `CloneAsync`,
  `FetchAsync`, `PullAsync`, `PushAsync`, `ListBranchesAsync`, `CheckoutBranchAsync`,
  `CommitAsync` (with `FilesToStage`), and `GetCommitLogAsync`.
- Documented the `ResetHardAsync` and `EnsureUpToDateAsync` sync helpers with examples.
- Documented the `GitResult<T>` return contract (`IsSuccess` / `Value` / `ErrorMessage`
  / `Diagnostics`).
- Documented `GitManager` access via `GetManager()`, including `ExecuteOnAllAsync`,
  `HealthCheckAsync`, runtime `RegisterRepository` / `UnregisterRepositoryAsync`, and
  cache control (`GetCacheStats`, `ClearCacheAsync`).
- Clarified authentication: PAT-only auth sends `x-access-token`, and the SSH key
  configuration fields are reserved but not currently wired (use HTTPS URLs).

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.3] — 2026-04-15

### Added

- Wired credentials through to `Clone` / `Fetch` / `Pull` and added
  `ResetHard` + `EnsureUpToDate` helpers.

## [4.0.2] — 2026-04-09

### Changed

- Annotated GitHelper configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Thin libgit2sharp wrapper for programmatic clone /
pull / fetch / reset.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.GitHelper).
