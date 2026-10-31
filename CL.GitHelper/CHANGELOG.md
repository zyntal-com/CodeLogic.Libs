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
