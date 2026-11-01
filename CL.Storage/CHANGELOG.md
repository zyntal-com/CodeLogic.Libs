# Changelog

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). This library's API is unchanged, and
  its results can now be matched exhaustively with `switch`.
- **`LocalConnectionConfig.RootPath` must be absolute** for an enabled connection; a relative root is a
  validation error (`RootPath must be an absolute path`). It used to resolve against the process's current
  directory, which a service host does not keep stable, so the same configuration could expose a different tree.
- **Storage paths reject every control character** (U+0000 to U+001F and U+007F, not only NUL) with
  `storage.invalid_path`. An existing object whose name contains one can no longer be addressed through the library.
- `StorageConnectionConfigBase` is a C# 15 `closed` class. The seven provider connection types are its only
  direct subtypes, so a `switch` over a connection is checked for exhaustiveness. Deriving your own
  connection type from it outside CL.Storage is no longer possible; use `RegisterBackend` for custom backends.

### Documentation

- `StorageFeature.ResumableUpload` is reserved: Azure Blob and Google Cloud Storage no longer report it, because
  nothing in the library reads it (staged resume is driven by `Append`). Check `Append` instead.
- Configuration examples in the README and the docs use the camelCase keys the CodeLogic loader reads.
  The PascalCase keys shown before are matched case-sensitively and were ignored, leaving defaults active.
- Local connections: the root-path checks are documented as traversal protection for caller-supplied paths, not a sandbox. A process that can modify the tree under the root at the same time can swap a checked path for a link before it is opened.

### Security

- **FTP command injection through paths.** `StoragePath.Normalize` rejected only NUL and `..`, so a CR/LF in a
  caller-supplied name reached the FTP control connection unescaped (`RMD {path}`, `AVBL {path}` and the commands
  FluentFTP sends): `x\r\nDELE /other/file` ran a second command outside the root. Every control character is now
  rejected (see Breaking), the FTP provider checks again before splicing a path into a raw command, and
  `ExecuteCommandAsync` refuses any control character in a raw command, not only CR/LF.
- **Secret fields.** `StorageProxyConfig.Password` and `SftpJumpHostConfig.Password`, `PrivateKeyContent` and
  `PrivateKeyPassphrase` are now `[ConfigField(Secret = true)]`, so masked configuration schemas no longer show them
  in clear text. A test checks that every credential-like configuration field is marked.
- **Windows reserved device names.** Local connections on Windows reject a path segment that names a device
  (`CON`, `PRN`, `AUX`, `NUL`, `COM0`-`COM9`, `LPT0`-`LPT9`, `CONIN$`, `CONOUT$`, also with an extension or trailing
  dots and spaces, such as `nul.txt`) with `storage.invalid_path`; such a name opened the device instead of a file
  under the root.
- **Resumable part-file lock markers no longer disclose the host.** A `<part>.lock` marker named the machine, the
  process id and its start time in clear text on the destination. It now holds a random owner id and salted SHA-256
  tags of those values (format `cl-storage-part-lock/2`); only the owner's machine can recognise its markers.
  Markers in the old format are still read and taken over as before.
- **Local connections on Windows reject paths containing `:`** with `storage.invalid_path`. A segment such as
  `file.txt:hidden` addresses an NTFS alternate data stream and passed the root containment check, so a caller
  could read, probe and delete hidden streams of files inside the root. A drive-relative path such as `C:name`
  resolved against the process's current directory on that drive instead of the root (contained only by
  chance when that directory lies under the root). Colons stay valid on other operating systems and for the
  remote providers (`StoragePath.Normalize` is unchanged).

### Fixed

- **Google Cloud Storage flat listings** send a `/` delimiter, so the server returns one level (sub-folders as
  prefixes) instead of the client scanning every descendant object of the folder.

### Tests

- `Persistence_failure_does_not_publish_an_healthy_replacement` failed against CodeLogic 5.0.32, whose
  configuration writes use a unique temp-file name, so the blocking `<file>.tmp` directory no longer made the
  save fail. The test now puts a directory in place of the configuration file itself, which fails the rename
  on Windows and Linux whatever the temp name.

## 2026-10-27 (4.9.104)

### Fixed

- **S3 SDK refusals say why.** An `AmazonClientException` (the SDK refusing a request before sending it:
  configuration, signing, checksums) was reported as `storage.provider_error` "S3 provider failed" with only
  the exception type. The message now carries the SDK's reason, sanitized: first line only, URL query strings
  and credential-like values (`AKIA…` keys, `signature=`, `token=`, `password=` …) redacted, at most 300
  characters. Other unclassified exceptions still report only their type.
- **`DisablePayloadSigning` with an `http://` endpoint is rejected by validation.** The AWS SDK refuses
  unsigned payloads over plain HTTP ("the request must be sent over HTTPS"), so such a connection passed its
  health check and served reads but failed every upload.
- **Failed operations are logged.** A failed service operation only raised `StorageOperationFailedEvent`, so
  the library log stayed empty. It is now also logged as a warning with its code, message and details, except
  the routine `not_found`, `conflict`, `cancelled` and `invalid_path`, at most once a minute per connection,
  operation and code.

### Added

- S3 service errors include the request id in `Details` (`requestId=…`).

## 2026-10-25 (4.9.98)

Everything below is relative to the published **4.8.93**. Types and members that 4.8.93 never shipped
are listed under *Added*, even where they changed while this release was being built.

> **A breaking release.** This release breaks source and binary compatibility with 4.8.93 (see
> *Changed (breaking)*). It ships in the 4.9 line (`4.9.<run>`, `AssemblyVersion` 4.9.0.0) with every
> library in this repository and requires CodeLogic 4.9, so a project floating on `4.8.*` does not pick it
> up. When you move to it:
>
> - **rebuild** everything that references CodeLogic.Storage and follow `MIGRATION.md`: an assembly compiled
>   against 4.8.93 (another package, or a plugin) still loads, and fails at runtime with
>   `MissingMethodException` or `MissingFieldException` the first time it calls a changed member, for
>   example `StorageLibrary.CopyAsync`, which now returns `StorageTransferReport` instead of `Result`;
> - **review** CodeLogic.Storage updates instead of letting an updater take them across lines.

### Changed (breaking)

**Copy and move**

- `StorageLibrary.CopyAsync` and `MoveAsync` return `StorageTransferReport` instead of `Result`. It has
  `IsSuccess`, `IsFailure`, `Error`, and `ToResult()`. Cancelling is reported
  (`Outcome = Cancelled`, `storage.cancelled`) instead of thrown, and so are a token cancelled before the
  call, an unknown connection id (`storage.not_found`), and a provider that throws.
  `IStorageService.CopyAsync`/`MoveAsync` on a connection still return `Result` and throw
  `OperationCanceledException` on a cancel, as in 4.8.93.
- A single file's committed destination is never rolled back. A failure after the commit (for example a
  backup that could not be removed) is reported: `Completed` with `BackupLeftBehind` or
  `StagingLeftBehind`, and a provider error carrying `destinationState=complete` is a committed transfer.
  A destination that does not hold the committed length is `NeedsReconciliation` even without `Verify`,
  and the previous version is then kept and named in `BackupLeftBehind`, never deleted.
- A directory transfer that fails part-way still rolls back the files it committed, but each only while it
  is still the version it committed (a conditional delete or restore where the provider enforces one,
  otherwise a comparison just before). A file changed meanwhile, or whose committed version is unknown, is
  left in place, and the transfer is `NeedsReconciliation` (`rollbackError=storage.conflict`).
- A cancel or exception after the commit is no longer reported as `Cancelled`/`Failed`: a copy is
  `Completed`, a move whose source is gone is `Completed`, and one whose source is still (partly) there is
  `NeedsReconciliation` with `destinationState=complete` (and `sourceItemsDeleted=N` for a directory move).
  The copy event is published in every case, with the files actually committed.
- Staged uploads (`Verify`, `ExpectedLength`, `ExpectedSha256`, `Resume`) and `StorageWriteStream.CommitAsync`
  can fail with `storage.partial_failure` carrying `destinationState=complete` and `leftBehind=<path>`
  entries when the content committed but the provider left an internal object behind; treat such a
  result as written.
- A directory move deletes its source file by file, each only while it is still the version listed, then
  removes the emptied folders without recursion. Files added or changed on the source during the move stay,
  and the move is `NeedsReconciliation` (`sourceChanged=N;sourceAdded=N`) where it used to delete the whole
  source. `SourceDeleted` is `true` only when the whole source is gone.
- A directory moved onto an existing directory on the same connection merges through the relay. Before,
  Local refused it and FTP, SFTP, and WebDAV replaced (deleted) the existing directory; those providers'
  own `MoveAsync` now refuses it with `storage.conflict`.
- A move deletes its source only while it is still the version that was copied; a native move on S3, Azure,
  Google Cloud, and Swift copies that version and deletes only it (never recursively).
- A move or resume no longer treats a destination of the same size as already complete: both sides must
  report the same digest.
- `StorageConflictPolicy.Resume` resumes a staging object and promotes it when complete, instead of
  appending to the destination in place; without `Append` it rewrites from the start instead of failing
  with `storage.unsupported`. Uploads need `SourceLastModified`, or a `SourceIdentity` marked
  `SourceIdentityIsContentVersion`, to resume (`UploadFileAsync` sets the time); a `SourceIdentity` alone
  (a path) is refused with `storage.invalid_content`. Copies resume only from sources with an ETag, time,
  or version, and from a source with only a weak ETag (Local) only with `Verify`.
- A resumable staging object is held across processes by a create-only lock marker beside it
  (`<part file>.lock`) until it is promoted; a second transfer stages privately (not resumable). A marker
  whose owner is gone is taken over (same machine: when its process no longer runs; another machine: after
  24 hours). A connection that cannot create the marker create-only never resumes.
- `Rename` names: the first free name is taken, `name (1).txt` goes on to `name (2).txt` (it used to
  become `name (1) (1).txt`), `file.` becomes `file. (1)`, and a folder at a candidate name counts as taken.
- Validation is stricter: an upload `Condition` together with a conflict policy other than `Overwrite`, and
  empty `SourceVersionId`/`ExpectedSourceETag`, are refused.
- FTP and SFTP overwrites no longer download and re-upload a backup of the old file (their replace renames
  it aside). A relay within one FTP or SFTP connection with `Session.MaxSessions = 1` fails at once with
  `storage.unsupported` (`requiredSessions=2;maxSessions=1`) instead of timing out; renames and moves on
  the server (including `ConflictPolicy = Rename`, which picks the free name first) use one session.
- A same-connection file move that cannot be pinned to the version read (WebDAV, or a source without ETag
  or version) compares the source immediately before the server's own move and reports
  `ConditionEnforcement = CheckedBeforeCommit`, instead of relaying the bytes through the client.
- A native directory move reports `Files`, `Directories`, and `Bytes` (counted at the destination), and a
  directory transfer reports the weakest `ConditionEnforcement` of its files and what they left behind.
- `UploadDirectoryAsync`/`DownloadDirectoryAsync`: a cancelled transfer whose rollback failed returns a
  failed result (`storage.partial_failure`) instead of throwing.

**Transfer queue**

- `CreateTransferQueue` is replaced by `OpenTransferQueueAsync`, which returns
  `Result<StorageTransferQueue>`. Every control method is asynchronous and returns a result:
  `EnqueueCopy`, `EnqueueMove`, `EnqueueUpload`, `EnqueueDownload`, `EnqueueUploadDirectory`, and
  `EnqueueDownloadDirectory` became `Enqueue…Async`
  returning `Result<StorageTransferJob>` (with optional `jobId` and `cancellationToken` parameters), and
  `Cancel`, `Retry`, `RetryFailed`, and `ClearFinished` became `CancelAsync`, `RetryAsync`,
  `RetryFailedAsync`, and `ClearAsync`.
- Job ids are strings (they were `Guid`), also in the constructors, `JobId`, and `Deconstruct` of
  `StorageTransferStartedEvent`, `StorageTransferCompletedEvent`, and `StorageTransferFailedEvent`.
  Priorities are integers (default 0), higher first; `StorageTransferPriority` is gone.
- `StorageTransferJob` is no longer a positional record: its constructor and `Deconstruct` are gone, it
  has a `required Record`, its other properties are get-only views of `job.Record`, and
  `EnqueuedAt`/`FinishedAt` are on `job.Record`.
- `RetryDelay` (a fixed 5 s) became `RetryBaseDelay` (2 s) and `RetryMaxDelay` (5 min): exponential
  backoff with jitter. `AutomaticRetries` defaults to 3 (it was 2).
- Disposing the queue leaves queued jobs queued in the store; 4.8.93 cancelled them (a `JobChanged` with
  `Cancelled` each). Jobs removed by `RemoveAsync`, `ClearAsync`, or pruning raise `JobRemoved`
  (`ClearFinished` raised nothing).
- Finished jobs are pruned beyond `MaxFinishedJobs` (1,000 by default).
- Authentication and trust failures end `Blocked` instead of `Failed`; a `storage.partial_failure`, or a
  move cancelled or paused after its copy committed, ends `NeedsReconciliation` (a copy that committed ends
  `Completed`), and the cancel or pause then fails with `storage.conflict`. `FailedJobs` and
  `RetryFailedAsync` cover `Failed` jobs only.
- `StorageTransferState` keeps its 4.8.93 numbers (`Queued` 0, `Running` 1, `Completed` 2, `Failed` 3,
  `Cancelled` 4); `Paused`, `Blocked`, `NeedsReconciliation`, and `Interrupted` follow them. Exhaustive
  switches need the new states. Every public enum now spells out its numbers.
- `EnqueueDownload` became `EnqueueDownloadAsync(sourceConnectionId, sourcePath, localFilePath, options,
  conflictPolicy, priority, jobId, cancellationToken)`: a new `StorageDownloadOptions? options` parameter
  comes before `conflictPolicy`, so a positional conflict policy must be passed by name
  (`conflictPolicy: StorageConflictPolicy.Resume`).
- Pausing, cancelling, or removing a running job waits for its attempt to stop for at most
  `ControlTimeout` (30 s by default) and then fails with `storage.timeout`; the request still takes effect
  when the attempt stops.
- A job whose store saves keep failing is retried with a growing delay, and fails with
  `storage.unavailable` after 8 attempts in a row (it used to be retried for ever).
- `MoveUpAsync`/`MoveDownAsync` among jobs that share an order make room instead of returning
  `storage.conflict`. A null job id is a failed result (`storage.invalid_content`) instead of an exception.
- Once `DisposeAsync` returns, the queue no longer calls its store; an attempt that outlived
  `ShutdownTimeout` records nothing more and is recovered once its lease lapses.
- Store contract: revisions continue across removal and re-adding of an id (as fencing tokens do); a JSON
  store skips rows it cannot read in `LoadAsync`; a store's own revision and lease columns win over the
  copies inside the record's JSON.

**Sync and compare**

- Cancelling `ApplySyncAsync` or `SyncAsync` once it has started applying no longer throws
  `OperationCanceledException`: it returns a success whose `report.Cancelled` is `true` (steps not started
  are `NotRun`, and a two-way baseline is still saved). Cancelling while planning still throws.
- `StorageSyncAction` is no longer positional: its constructor and `Deconstruct` are gone, `RelativePath`
  and `Kind` are `required`, and it has no `Error`. `StorageSyncReport` is no longer positional either;
  it has a `required Plan`, `Actions` and `Unchanged` are get-only, and `Actions` lists the planned steps,
  conflicts included. Outcomes are in `report.Results`, and `report.Failed` is a list of
  `StorageSyncActionResult`.
- `StorageSyncActionKind` keeps 0–3 (`CopyToDestination`, `CopyToSource`, `DeleteFromDestination`,
  `CreateDirectory`) and adds `DeleteFromSource` (4), `CreateDirectoryAtSource` (5), `RenameAtDestination`
  (6), and `Conflict` (7); `StorageDiffReason` adds `Undecidable` (32). Exhaustive switches need them.
- A step that fails transiently is tried again up to `ItemRetries` times (2 by default) before it counts
  as failed.
- `TwoWay` without a baseline reports differing files as conflicts (`Block` by default) instead of letting
  the newer one win; set `ConflictPolicy = NewerWins` for the old behaviour.
- `Update` and `Mirror` never replace a newer destination with an older source, even when the sizes differ.
- `Mirror` with `DeleteExtraneous` deletes an extra folder file by file and then the emptied folders, and
  withholds all deletes after any failed copy. It never deletes a destination path the source left out (a
  link, a hidden item, anything under an excluded folder).
- Everything below a path that is a file on one side and a folder on the other is left alone.
- `StorageCompareOptions.LinkHandling = Recreate` is refused.
- Plans: applying a plan with options for another `SyncId`, for other connections, in an older plan format,
  or with options other than the plan's (only `MaxConcurrency`, `ItemRetries`, `ContinueOnError`,
  `DryRun`, `Progress`, `StateStore`, `ApplyWithConflicts`, and `Compare.HashConcurrency` may change) is
  refused; two applies of one `SyncId` in a process run one after the other. A plan is bound to connection
  ids only (keep them stable); the baseline to `SyncId` only. The options digest changed during this
  release, so plans made by earlier preview builds are refused: plan again.
- What a sync deletes, overwrites, or renames aside must be exactly the planned version: no time tolerance
  at apply. A time known only to a unit keeps that precision: new `StorageItem.ModifiedPrecision` (and
  `StorageSyncIdentity.ModifiedPrecision`), set by FTP for a `LIST` line (minutes, or days for older files),
  so a file listed to the minute matches the same file read to the second, at apply and when comparing.
- `LinkHandling.Follow` lists a followed link to a folder with its target's contents (up to 8 links deep;
  a link leading back into a folder being listed is left out), filters a link as what it leads to, reads
  copies through the link (`StorageSyncAction.ReadPath`), and never deletes or replaces anything through
  a followed link.
- One-way plans leave alone what the destination left out (a hidden item there, a skipped link): nothing
  is written onto or through it, and it no longer withholds deletes.
- Planning returns a failure instead of throwing for a provider whose listing throws or a filter pattern
  that times out (`storage.invalid_content`).
- A tree over `MaxItems` (1,000,000 by default) fails the plan.
- `CompareAsync` fails on names that differ only by case on a case-insensitive side instead of returning
  two entries.
- An `Exclude` pattern that matches a folder excludes its contents, as in `.gitignore`; `IncludeHidden =
  false` leaves out hidden folders with their contents.
- A kept `cl-mtime` without an offset is read as UTC, and a kept time is always used.
- Conflict copies of `a.tar.gz` are named `a (conflict x).tar.gz`.

**Listings, providers, and connections**

- Recursive listings on S3, Azure Blob, and Swift include folders that exist only as key prefixes, so item
  counts change. They are sorted per page, and an inferred folder can repeat on a later page.
- `IncludeHidden = false` also leaves out the contents of hidden folders within one listing.
- Local items have a weak ETag, `W/"<write time>-<creation time>-<length>"` (it was null in 4.8.93).
  Sync conflict-copy names built from it therefore change.
- On Windows only symbolic links and junctions are links; other reparse points (OneDrive placeholders,
  deduplicated files, app execution aliases) are files and folders. Recursive local listings no longer
  descend into links to folders.
- Swift no longer declares `ConditionalUpdate` or `ConditionalDelete` (the server ignores `If-Match` on
  writes); conditions are checked just before instead. WebDAV no longer declares `AtomicMove`.
- S3 under `ConditionalRequests = Auto`: the probe also covers `PutObject` (uploads are no longer trusted
  unprobed), a condition found ignored or rejected is not sent at all, the `ConditionalCreate`/
  `ConditionalUpdate`/`ConditionalDelete` flags are provisional until the probe has run and then name only
  what is enforced (MinIO loses all three), and an inconclusive probe backs off from 1 to 32 minutes. The
  probe's writes leave versions and delete markers on versioned buckets and fire notifications.
- S3: a `CopyObject` or `CompleteMultipartUpload` is not cancelled once sent. Azure: a started copy is
  waited for whatever the caller's token says; a move deletes the source's snapshots.
- A non-recursive object-store listing refuses a continuation token from a recursive one
  (`storage.invalid_path`).
- WebDAV: an upload onto an existing folder is refused (`storage.conflict`); a non-recursive folder delete
  locks the collection and deletes it only while empty, and a server without locks answers
  `storage.unsupported` (the folder stays). FTP: a non-recursive folder delete is a raw `RMD`; a recursive
  one removes hidden files too.
- Local ETags are compared as weak validators: a match never proves an unchanged file (checks fall through
  to size and time). The case probe looks at ASCII letters only.
- On Windows the machine key store is used for a client certificate only when the user key store is
  unavailable; a wrong password is reported as it is.
- `CopyAsync`/`MoveAsync` on a backend return `storage.unsupported` for pins or destination conditions they
  cannot enforce (FTP, SFTP, WebDAV, Local for `SourceVersionId`) instead of ignoring them.
- A PKCS#12 client certificate without its private key fails registration.
- A TLS stream that fails after the handshake is `storage.connection_lost` (transient), no longer
  `storage.tls_failure`; `client_certificate_rejected` needs proof that this connection's certificate was
  refused, and explains only the attempt that sent its request on that connection (a spare connection
  closed unused records nothing). Behind an HTTP proxy tunnel it is not detected.
- FTP and SFTP registrations with identical settings share one session pool, so `MaxSessions` caps them
  together. Listing continuation tokens on Local, FTP, SFTP, and WebDAV are tied to the settings instead of
  the connection id (a listing over 250,000 items is not kept, so its token is walked again).
- `StorageChangeKind` gained `Overflow` (4); exhaustive switches need the new case.

### Added

- **Live config changes** (requires CodeLogic 4.9+). `StorageLibrary` implements `IConfigReloadable`:
  a `storage*` section changed through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync` or
  the config file watcher is applied without a restart and touches only the connections that changed.
  Changed and added connections are built and health-checked before they replace the current backend,
  which drains its in-flight operations first; removed or disabled connections are retired; untouched
  connections keep running. A failed health check keeps the current backend and is reported as not
  applied; the next apply (e.g. `ReloadConfigAsync` once the cause is fixed) retries it even if the
  file did not change. Library-wide speed limits are re-attached live. The default connection cannot be removed or
  disabled this way, and toggling `storage.enabled` requires a restart. The library's own persisted
  changes (`AddOrUpdateConnectionAsync`, `RemoveConnectionAsync`) are not applied a second time.
- **Transfer reports** (`StorageTransferReport`, `StorageTransferOutcome`, `StorageSkipReason`,
  `StorageConditionEnforcement`): outcome, skip reason, written path, digest, destination ETag/version, how
  a condition was enforced, and exactly what an unfinished transfer left (`DestinationCommitted`,
  `SourceDeleted`, `StagingLeftBehind`, `BackupRestored`, `BackupLeftBehind`, `ResumeToken`).
- **Guaranteed single-file transfers**: `DestinationCondition`, `SourceVersionId` (needs `Versioning`),
  `ExpectedSourceETag`, `ExpectedSourceLength`, `Verify`, and `ExpectedSha256` on `StorageTransferOptions`;
  `ExpectedLength`, `Verify`, `ExpectedSha256`, `SourceIdentity`, and `SourceIdentityIsContentVersion` on
  `StorageUploadOptions`. Content is staged, checked, confirmed, and promoted; the destination condition is
  handed to the provider's move.
- **Staged resume** with `StorageResumeToken` (`StorageTransferOptions.ResumeToken`), which survives
  restarts; one writer per staging object.
- **`OpenWriteAsync`** (`StorageWriteExtensions`): a push-style `StorageWriteStream` (`CommitAsync`,
  `AbortAsync`, `BytesWritten`, `DestinationPath`); `StorageWriteException` carries the storage error when
  the destination stops accepting data.
- `StorageItem.Sha256` and `StorageItem.ModifiedPrecision`; `StorageTransferOptions.PreScan`;
  `FilesCompleted`/`FilesTotal` on progress; download progress carries `TotalBytes` even without a `Length`.
- **Durable transfer queue**, opened with `StorageLibrary.OpenTransferQueueAsync`:
  - jobs as data (`StorageTransferJobSpec`, `EnqueueAsync(spec)`), idempotent caller-chosen ids, and
    `StorageTransferJobRecord` (`ToJson`/`FromJson`, `IsReadable`, `StorageTransferCheckpoint`,
    `StorageTransferFailure`) behind `IStorageTransferJobStore` (`InMemoryStorageTransferJobStore` by
    default) with revisions, conditional removal (`RemoveAsync(jobId, expectedRevision, lease)`),
    `ReleaseAsync`, and store-owned leases with fencing (`StorageTransferLease`);
  - restart rules from the recorded `StorageTransferPhase`; the states `Paused`, `Blocked`
    (`StorageTransferBlockReason`), `NeedsReconciliation`, and `Interrupted`; `job.LastReport` and
    `job.BlockReason`;
  - `Get`, `PauseJobAsync`/`ResumeJobAsync`, `SetPriorityAsync`, `MoveUpAsync`/`MoveDownAsync`,
    `RemoveAsync`, `ClearAsync(states)`, `RefreshAsync`, `ConcurrencyLimit`, and `JobRemoved`;
  - options `Store`, `WorkerId`, `LeaseDuration`, `RequeueInterruptedWhenSafe`, `MaxFinishedJobs`,
    `AdaptiveConcurrency`, `ProgressInterval`, `EventContext`, `StoreRefreshInterval`, `ShutdownTimeout`,
    and `ControlTimeout`; exponential backoff honouring `Retry-After`;
  - bus events `StorageTransferCancelledEvent`, `StorageTransferRetryingEvent`,
    `StorageTransferBlockedEvent`, `StorageTransferNeedsReconciliationEvent`, and
    `StorageTransferInterruptedEvent`. Stopping the library disposes the queues it opened.
- `StorageErrors.Cancelled` and `CancelledCode` (`storage.cancelled`, never transient; classify by code, not
  by the Core error kind); `StorageErrors.Create` rebuilds an error from a stored code, message, and
  details; `StorageErrorInfo.DestinationCommitted` and the keys `DestinationStateKey`, `LeftBehindKey`,
  `TlsReasonKey`, `PresentedCertificateKey`, `PresentedPublicKeyKey`, and `PresentedFingerprintKey`.
- **Three-way sync**: `PlanSyncAsync`/`ApplySyncAsync` (on `StorageLibrary` and as `IStorageService`
  extensions) with an approvable, serializable `StorageSyncPlan` (`Digest`, `ComputeDigest`, `SchemaVersion`,
  connection ids, `OptionsDigest`, `Warnings`, `Conflicts`, `ToJson`/`FromJson`); a baseline store
  (`IStorageSyncStateStore`, `InMemoryStorageSyncStateStore`, `StorageSyncBaseline`,
  `StorageSyncBaselineEntry`, `StorageSyncIdentity`) with a classifier per side;
  `BothModified`/`BothCreated`/`DeleteVersusModify` conflicts (`StorageSyncConflictKind`) under `Block`,
  `KeepBoth`, or `NewerWins`
  (`StorageSyncConflictPolicy`); per-step outcomes (`StorageSyncActionResult`, `StorageSyncActionOutcome`:
  `Applied`, `Failed`, `Stale`, `Withheld`, `NotRun`) with `report.Results`, `Stale`, `Withheld`,
  `Conflicts`, `Cancelled`, `BaselineSaved`, and `BaselineError`; step details on `StorageSyncAction`
  (`Source`, `Destination`, `Conflict`, `DestinationRelativePath`, `TargetPath`, `ReadPath`,
  `WithheldReason`).
- New `StorageSyncOptions`: `SyncId`, `StateStore`, `ConflictPolicy`, `PropagateDeletes`,
  `ApplyWithConflicts`, `Verify`, `MaxDeletes`, `MaxDeletePercent`, `AllowEmptySide`, `ItemRetries`, and
  `ContinueOnError`. New `StorageCompareOptions`: `Include`/`Exclude` globs, `LinkHandling`,
  `CaseInsensitive` (case-collision refusal, `StorageFeature.CaseInsensitivePaths`), `MaxItems`,
  `HashConcurrency`, `MaxHashedFiles`, `MaxHashedBytes`, and `ModifiedMetadataKey` (`cl-mtime`).
  `StorageDiffEntry.DestinationRelativePath`; `StorageCompare.CompareAsync` (the same as the
  `CompareAsync` extension).
- `StorageLibraryOptions` (`RuntimeOnly`, `Settings`) and `new StorageLibrary(options)`: in runtime-only
  mode no configuration section is registered, read, or written, and the settings passed in are copied
  (`StorageLibrary.RuntimeOnly`).
- `tlsReason` on `storage.tls_failure` (`server_certificate_rejected` with the presented certificate,
  `client_certificate_rejected`, `protocol_mismatch`, `handshake_failed`), and `connection_interrupted` as a
  hint on `storage.connection_lost`.
- `ClientCertificateContent` on FTP and WebDAV connections: the client certificate as bytes. On Linux the
  key stays in memory; on macOS .NET keeps it in a temporary keychain; on Windows it goes into a
  non-persisted key container deleted with the connection.
- `S3ConnectionConfig.ConditionalRequests` and `S3StorageBackend.ConditionalRequests`
  (`S3ConditionalRequestSupport`: `Auto` 0, `Enforced` 1, `NotEnforced` 2): how far to trust an
  S3-compatible server's conditional requests; `Auto` probes uploads, copies, and deletes once per
  connection.
- `StorageConditionKind` (`CreateOnly` 0, `MatchVersion` 1, `DeleteMatchVersion` 2) and the
  `IStorageService.GetConditionEnforcementAsync(kind, serverSideCopy, cancellationToken)` extension
  (`StorageConditionEnforcementExtensions` in `CL.Storage.Abstractions`): how a connection enforces a
  condition, `Atomic` or `CheckedBeforeCommit`.
- Swift passes `StorageDownloadOptions.VersionId` (and a copy's `SourceVersionId`) as `?version-id=`
  instead of refusing it.
- `StorageSessionConfig.LingerSeconds`; `StorageWatchOptions.Incremental`, `FullRescanEvery`, and
  `PollFailed`.
- Continuous integration runs the mutual-TLS tests on Windows (SChannel) too.

### Fixed

- Persisting one local connection (`AddOrUpdateConnectionAsync`/`RemoveConnectionAsync` with `persist: true`)
  wrote every other local connection back to `config.storage.local.json` without its `TransferLimits`: the
  library's copy of the section dropped them. The copy now keeps them.
- `Mirror` copied an older source over a newer destination of the same size; its deletes ran even after
  copies failed; a folder deleted as extraneous took excluded files with it.
- Sync looked up each action with a linear search inside the copy loop (quadratic).
- Recursive listings on S3, Azure Blob, and Swift left out folders that exist only as key prefixes; Google
  Cloud repeated inferred folders after every page; a file named like a folder hid the folder.
- Connections from `GetStorage()` never watched natively; the native watcher dropped changes silently on
  overflow. Polling reported every item as created when its first listing failed, and as deleted when a
  listing was cut short; it now retries, reports failures to `PollFailed`, and native watching that cannot
  start or fails falls back to polling.
- FTP could not find dot-files on servers without MLST that hide them from `LIST` (vsftpd); they are found
  with `SIZE`/`MDTM`/`CWD` or a hidden-files listing, and `LIST -a` falls back to `LIST`.
- SFTP `AppendAsync` failed on a missing file.
- S3 metadata read back with an `x-amz-meta-` prefix; a `CompleteMultipartUpload` whose answer was lost
  failed over the object it had committed; SSE-C ETags were taken for MD5; server-side copies over 5 GiB
  failed (one `CopyObject`) and now go part by part (parts of at least 128 MiB).
- Azure Blob listings ended with an empty continuation token instead of none.
- Swift declared conditional updates and deletes, but the server ignores `If-Match`, so an upload with a
  wrong ETag overwrote the object. A create-only Swift copy failed, because the server applied its
  `If-None-Match` to the source (304).
- `MetadataPreservation = Discard` on a same-connection copy or move ran the server's own copy, which kept
  the metadata; it now relays.
- A non-recursive folder delete on FTP (FluentFTP's `DeleteDirectory` after a listing that missed hidden
  files) or WebDAV (a listing, then a `DELETE` of depth infinity) could delete contents.
- FTP listing times without MLSD are whole minutes (or days), so sync with the default 2 s `TimeTolerance`
  saw an unchanged file as changed and copied it again on every run.
- WebDAV treated `207 Multi-Status` on `COPY`/`MOVE` as success, and dropped items it listed in the
  server's own spelling of the root.
- Moving a directory onto an existing one on FTP, SFTP, or WebDAV deleted the existing directory.
- Windows reparse points that are not links (OneDrive placeholders, deduplicated files) were treated as
  links and skipped.
- Found while this release was reviewed (round 4), relative to earlier preview builds: same-server
  `Rename` relayed (and failed with one session); a sync into an empty folder spelled differently made a
  second folder; a refused conditional promote brought back a destination deleted meanwhile; a failed
  confirm deleted the only previous version; resumes across processes could mix data; spare TLS connections turned drops into
  `client_certificate_rejected`; an upload resume keyed only by a path continued an edited file; a hidden
  folder's contents showed on later pages of object-store listings; a same-size edit within the time
  tolerance could be deleted or overwritten by a sync; a folder left with old staging stayed `Stale` for
  ever; queue controls could wait for ever, a failed remove could leave a job unstarted, and a newer-schema
  record could be run by an older worker.
- A staged upload or `StorageWriteStream.CommitAsync` whose content was committed, but whose read-back
  afterwards failed, returned the read's error without `destinationState=complete`, so it looked like a
  plain failure. The read-back is retried on transient errors, and a failure after it now carries
  `destinationState=complete`.
- `TestConnectionAsync` put exception messages (which can carry hosts, paths, or credentials) into its
  errors; it now reports the exception type only. It also joined the session pool of a registered FTP or
  SFTP connection with the same settings; it now opens its own sessions and closes them when it ends.
- Invalid settings passed to `AddOrUpdateConnectionAsync` are `storage.invalid_content` (were
  `storage.provider_error`), as `TestConnectionAsync` reports them.
- On MinIO, `GetConditionEnforcementAsync(MatchVersion)` answered `Atomic` for uploads, which are staged
  there and checked just before the commit; it now answers `CheckedBeforeCommit` unless the server
  enforces `If-Match` on both `PutObject` and `CopyObject`.
- `DownloadToFileAsync` with `ConflictPolicy.Resume` appended the remote tail to any shorter local file and
  took one of equal size as complete, without checking it was the same file. It now downloads into a
  partial file with the remote version recorded beside it, resumes only while the remote is still that
  version, and otherwise downloads from the start.
- Removing a running queue job deleted its record even when the attempt ended `NeedsReconciliation`
  (a move whose copy committed), so nobody was told both source and destination exist. That job is now
  kept, and `RemoveAsync` returns `storage.conflict`.
- A queue job whose claim kept failing in the job store was retried for ever; failed claims now count
  towards the store-failure limit (doubling delay, failed after 8 in a row). A job re-queued after a store
  failure announced its retry with `storage.cancelled`; it is now `storage.unavailable`.
- `ApplySyncAsync` ignored `StorageSyncOptions.DryRun` and applied the plan; with `DryRun` it now reports
  every step `NotRun` (or `Withheld`), writes nothing, and does not save the baseline.
- On FTP and SFTP, two writers racing for one resumable part file could both read back their own lock
  marker (create-only is checked before the write there) and both append to it; the re-check before the
  promote kept only one from committing. The marker is now read back after a short pause.
- Stopping one `StorageLibrary` closed the idle, lingering session pools of every other library in the
  process; it now closes only pools no other library has used.
- Comments in the FTP and SFTP backends no longer say a WebDAV `MOVE` is atomic (WebDAV does not declare
  `AtomicMove`).

## 2026-09-22

### Fixed

- Swift items never carried an `ETag`, because Swift sends it unquoted and the typed header parser
  rejects that, so every conditional delete against Swift failed as a conflict.
- FTP listing times were shifted by the client machine's UTC offset: the FTP client now converts to
  UTC, and unspecified times are no longer reinterpreted as local time.
- WebDAV uploads, moves, and copies each stranded a pooled HTTP connection until garbage collection,
  because the WebDAV client library never disposes those responses. With a connection limit the next
  request hung; without one, sockets piled up under load. The backend now issues MOVE and COPY itself.
- Legacy code-page encodings (windows-1252, iso-8859-x, ibm437, shift_jis) were unavailable because
  .NET does not register them by default.
- A Google Cloud object whose timestamp had fewer than three fractional digits failed the whole
  operation with `storage.provider_error`; timestamps are now parsed leniently.
- Unclassified provider failures now carry the exception type (never its message) in `Details`.
- Ranged Google Cloud Storage downloads failed hash validation, because the stored CRC32C covers the
  whole object; validation is now skipped for byte ranges only.
- Google Cloud clients whose credentials cannot sign URLs (anonymous or emulator clients) threw
  from the backend constructor instead of just disabling signed URLs.
- WebDAV failures were all reported as `storage.provider_error`, and missing-directory detection never
  matched: the HTTP status is read from `WebDAVException.GetHttpCode()` (with a message fallback)
  instead of `ErrorCode`, which the client leaves at zero.
- FTP server replies wrapped in FluentFTP's generic `FtpException` are now unwrapped and classified.
- On Linux and macOS, WebDAV items whose names need URL escaping (spaces, for example) were reported
  as missing right after being written: server-relative hrefs were parsed as file paths.
- Public-key (SPKI) pin checks disposed the server certificate they were given, so anything reading
  it later in the TLS callback saw a disposed certificate.

### Changed (breaking)

- `UploadWithProgressAsync` no longer hides seeking, so uploads with progress can be retried.

- `DownloadToFileAsync` gained a `conflictPolicy` parameter before `cancellationToken`.

- Listings no longer show the library's own staging and backup items (`.cl-storage-*`,
  `.clstorage-*`); set `IncludeInternal` to see them, for example to clean up after a crash.

- `ComputeChecksumAsync` and `VerifyChecksumAsync` gained a `mode` parameter before
  `cancellationToken`; positional callers passing a token must name it.

- FTP, SFTP, and WebDAV now declare `AtomicMove`, so renaming a folder through the library uses a
  single server-side rename (RNFR/RNTO, SFTP rename, WebDAV MOVE) instead of copying the whole tree
  through the client and deleting the original.

- SFTP no longer requires `HostKeyFingerprints` when `KnownHostsPath` is set.

- `StorageConnectionInfo` gained `Host`, `Port`, `Security`, and `LastHealth`. Positional
  deconstruction is unchanged; code comparing whole records for equality now also compares these.

- WebDAV always installs a certificate validation callback (to record the presented certificate).
  Without pins it still accepts only certificates with no policy errors.

- `FtpStorageBackend`, `SftpStorageBackend`, and `WebDavStorageBackend` constructors take optional
  session and retry settings. Retries are on by default (3 attempts); pass
  `new StorageRetryConfig { RetryCount = 0 }` to restore single-attempt behavior.

- Split coarse failures into precise error codes: `storage.authentication_failed`,
  `storage.permission_denied`, `storage.tls_failure`, `storage.host_key_rejected`,
  `storage.connection_failed`, `storage.connection_lost`, `storage.server_busy`, and
  `storage.quota_exceeded`. Code that compared against `storage.unauthorized` or
  `storage.unavailable` for provider failures must also accept the new codes; see `MIGRATION.md`.
- FTP errors are now classified from the server reply code (421, 425/426, 450/550, 452/552,
  530, 553, ...) instead of collapsing to `storage.provider_error`. TLS failures are no longer
  reported as credential failures.
- SFTP reports an untrusted host key as `storage.host_key_rejected` with the presented fingerprint
  in `Details`, and distinguishes refused connections, dropped sessions, and too-many-sessions.
- WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift share one HTTP status mapping: 401 vs 403
  are distinguished, 429/503 become `storage.server_busy` (carrying `retryAfterMs` when the server
  sent `Retry-After`), and 507 becomes `storage.quota_exceeded`.
- A full local disk now returns `storage.quota_exceeded`, and local access denial returns
  `storage.permission_denied`.

### Added

- `TestConnectionAsync` checks connection settings that have not been saved: it validates them,
  connects, lists the root, and reads server details, reporting each step. When a certificate or host key
  is rejected, the report still carries what the server presented so a setup screen can offer to pin it.
- `GetConnectionDiagnosticsAsync` describes a live connection: address, transport security, the
  presented certificate or host key, negotiated TLS/SSH algorithms, server system and software, FTP
  `FEAT` or WebDAV `DAV`/`Allow` features, session pool counters, and the last health check.
- `StorageConnectionHealthChangedEvent` when a health check finds a connection in a new state, and
  `StorageOperationFailedEvent` for every failed service operation (with operation, path, and code).
- `WatchAsync` change streams: native notifications for local connections (`ChangeNotifications`),
  polling with snapshot diffs for every other provider.
- `IStorageCommandService.ExecuteCommandAsync` for raw FTP and SSH commands (opt-in with
  `AllowRawCommands`) and `IStorageSpaceService.GetSpaceAsync` for SFTP, FTP (`AVBL`), and local.
- Directory `CompareAsync` and `SyncAsync` (`Update`, `Mirror` with optional deletes, `TwoWay`) across
  any two connections, with dry runs, timestamp preservation, checksum comparison, and parallel copies.
- `StorageLibrary.CreateTransferQueue`: a background queue with global and per-connection concurrency,
  priorities, pause/resume, cancellation, automatic re-queueing of transient failures, a retryable
  failed list, progress and state events, and started/completed/failed bus events.
- `Progress` on upload, download, and transfer options, with speed, remaining time, and the current
  file; relayed directory transfers report one running total.
- Per-connection `TransferLimits` and library-wide `MaxTotalUploadBytesPerSecond` /
  `MaxTotalDownloadBytesPerSecond` speed limits, shared by concurrent transfers.
- `IStorageAppendService.AppendAsync` (Local, FTP, SFTP), `StorageConflictPolicy.Resume` for
  interrupted uploads and `DownloadToFileAsync`, and `CleanupStaleStagingAsync`.
- `StorageConflictPolicy` (`Fail`, `Overwrite`, `Skip`, `OverwriteIfNewer`, `OverwriteIfSizeDiffers`,
  `OverwriteIfNewerOrSizeDiffers`, `Rename`) on uploads, transfers, directory uploads/downloads, and
  `DownloadToFileAsync`, with `SkippedFiles` in directory reports and partial moves that keep skipped
  sources.
- `StorageListOptions.IncludeInternal`, `IncludeHidden`, and `NamePattern` (`*`/`?` wildcards).
- `StorageTransferOptions.LinkHandling` (`Reject`, `Skip`, `Follow`, `Recreate`) for relayed
  transfers that meet symbolic links.
- Deleting a local link removes the link itself, even when `FollowLinks` is off, and never its target.
- `IStorageChecksumService.GetServerChecksumAsync` for S3, Azure Blob, Google Cloud Storage, Swift, and
  FTP. `ComputeChecksumAsync`/`VerifyChecksumAsync` use the server digest when available (new
  `StorageChecksumMode` parameter) and report it in `StorageChecksum.Source`.
- `IStorageAttributeService` with permissions (including recursive file/directory modes), numeric
  ownership, timestamps, and symbolic links, plus `StorageItem.UnixMode`, `Permissions`, `Owner`,
  `Group`, `OwnerId`, `GroupId`, `LinkTarget`, `Created`, `LastAccessed`, and `IsHidden`, and
  `UnixPermissions` for octal and `rwx` conversion.
- WebDAV: Digest, NTLM, and Negotiate authentication, public-key (SPKI) pins,
  `RequireValidCertificateChain`, client certificates for mutual TLS, and `MaxConnectionsPerServer`.
  Basic credentials are sent up front instead of after a 401 challenge.
- FTP/FTPS: public-key (SPKI) pins, `RequireValidCertificateChain`, revocation checks, TLS version
  selection, `EncryptDataChannel`, active-mode port range and external IP, file-name `Encoding`
  (including legacy code pages), ASCII `TransferType`, `ListingParser`, `ServerTimeZone`, separate
  connect/read/data timeouts, `SocketKeepAlive`, and `LoginCommands` run after each login.
- SFTP: `KeyboardInteractive` and `Auto` authentication (keys, password, keyboard-interactive, and
  multi-method servers), inline private keys (`PrivateKeyContent`), several keys, OpenSSH
  `known_hosts` verification (hashed, wildcard, `[host]:port`, and `@revoked` entries), algorithm
  allow-lists, file-name `Encoding`, `BufferSize`, and `JumpHost` tunnelling through an SSH bastion.
- HTTP, SOCKS5, and SOCKS4 proxy support (`Proxy`) for FTP (including data connections), SFTP,
  WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift.
- Google Cloud Storage `ServiceUrl` and `AllowInsecureHttp` for private endpoints and emulators, plus
  an `Anonymous` authentication mode for public buckets and fake-gcs-server.
- Swift `TempAuthV1` authentication (`X-Auth-User` / `X-Auth-Key`) and `AllowInsecureHttp`.
- FTP, SFTP, and WebDAV retry transient failures (timeouts, refused or dropped connections, busy
  servers) with exponential backoff, jitter, and `Retry-After` support, configured per connection
  through `Retry`. Deletes and moves retry only with `RetryNonIdempotent`; uploads retry only from
  seekable streams.
- FTP and SFTP session pools are configurable per connection through `Session`: `MaxSessions` caps
  open sessions and makes excess callers wait (`storage.server_busy` on timeout), idle sessions are
  probed before reuse, and sessions that fail mid-operation are retired instead of reused.
- `Session.KeepAliveSeconds` enables FTP NOOP and SSH keep-alive packets.
- `StorageConnectionOpenedEvent`, `StorageConnectionLostEvent`, and `StorageConnectionRetryEvent`,
  plus a warning log line per retry.
- `StorageErrorInfo` with `IsTransient`, `IsConnectionFault`, `TryGetRetryAfter`, and `TryGetDetail`
  for retry decisions and provider diagnostics (`ftpReply`, `sftpStatus`, `httpStatus`).

### Added

- Added mounted local/UNC, S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud
  Storage, and OpenStack Swift backends behind one `IStorageService` contract.
- Added typed provider configuration, runtime add/update/remove persistence, stable service proxies,
  per-connection health, custom-backend registration, native clients, scoped native sessions, and
  asynchronous library disposal.
- Added granular `StorageFeature` flags and provider limits for page, object, metadata, batch, and
  multipart boundaries.
- Added bounded cross-connection file and recursive-directory copy/move, plus rollback-safe local
  directory upload/download reports.
- Added page/item async enumeration and bounded order-preserving batch info/delete/copy/move helpers.
- Added file, text, JSON, progress, and streaming checksum convenience APIs.
- Added optional metadata, object-tag, signed-URL, and object-version contracts. S3 and Azure Blob
  support bounded tag reads plus merge/replace updates. S3, Azure Blob, and GCS support exact version
  listing/deletion; S3, Azure, and signing-capable GCS credentials support temporary URLs.
- Added atomic ETag/version upload and delete conditions where providers can enforce them.
- Added typed write/delete/copy/move and cross-connection completion events.
- Added a migration guide from `CodeLogic.StorageS3`.

### Safety

- Added an explicit, default-off `AutoAcceptHostKey` SFTP connection option for trusted
  environments where a server host-key fingerprint cannot be configured.
- Accept SSH.NET's canonical unpadded Base64 SHA-256 host-key fingerprints while continuing to
  reject malformed, noncanonical, or non-SHA-256 values before SFTP trust decisions.
- Centralized path normalization and source/destination relationship checks; equal transfers and
  directory moves/copies below their source are rejected by the library and direct backends.
- Staged local, FTP, SFTP, and WebDAV overwrites so a failed upload cannot truncate existing data.
- Made recursive transfers use unique staging objects, preserve caller upload streams, hold registry
  leases, and cap relay read-ahead at 1 MiB.
- Back up pre-existing destination files and restore them when a later directory item fails. Incomplete
  cleanup or source deletion returns sanitized `storage.partial_failure` state.
- Propagate caller cancellation and keep provider response/session ownership attached to returned
  download streams.
- Enforce byte-buffering, metadata, multipart, serialization, and batch limits.
- Removed certificate and host-key bypass behavior. Clear-text custom endpoints require explicit opt-in;
  SFTP host-key trust is mandatory; FTPS/WebDAV certificate pins use SHA-256.
- Reject header injection, transport-managed custom headers, unsafe endpoint URL components, malformed
  metadata, and unsupported version/metadata options instead of silently ignoring them.
- Sanitized public provider errors so credentials, signed query strings, raw response bodies, and
  provider exception messages are not exposed.

### Changed

- FTP, SFTP, WebDAV, and local listings now page from a single cached, ordinally sorted snapshot per
  listing pass instead of re-materialising and re-sorting the whole listing on every page. Paging a
  recursive listing of N entries at page size P cost `ceil(N/P)` complete directory walks and is now
  one walk; ordering and item content are unchanged. Continuation tokens remain opaque and are still
  rejected when malformed, but their internal format changed, so a token minted by an earlier version
  is not accepted by this one. A token is only meaningful within the listing that minted it;
  presenting one to a different listing resumes that listing from the path the token carries rather
  than failing, because an evicted snapshot cannot be told apart from a foreign one.
- FTP and SFTP reuse pooled, already-authenticated sessions across operations rather than opening and
  tearing down a connection per call. Idle sessions are health-checked before reuse, bounded in count
  and idle lifetime, and closed when the backend is disposed. Sessions handed out through
  `OpenNativeConnectionAsync` are retired rather than pooled, since caller code may leave them in an
  unexpected state.
- Recursive service copy/move now always uses the safe coordinator; same-provider file staging remains
  server-side when the backend advertises a safe native copy.
- Object-provider listing distinguishes an exact file path from a virtual directory and treats root
  directory creation as an idempotent no-op.
- S3 uploads use explicit bounded multipart handling for seekable and non-seekable streams.
- Google Cloud downloads stream through a bounded pipe and listings use real provider paging.
- WebDAV metadata-read capability is now callable through `IStorageMetadataService`; property writes
  remain explicitly unsupported by the portable adapter.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.
