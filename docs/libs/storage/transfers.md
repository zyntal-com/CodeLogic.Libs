# Transfers & Sync

> Moving data safely: copies and moves between any two connections, guaranteed single-file transfers,
> streamed writes, what happens when the destination already exists, resume and append, progress and
> speed limits, a durable background queue, compare and three-way sync, watching for changes, and links.

The queue and sync types live in `CL.Storage.Queue` and `CL.Storage.Sync`.

## Safe copies and moves

The library copies or moves files and complete directory trees between any two mounted connections:

```csharp
StorageTransferReport copied = await storage.CopyAsync(
    "primary", "exports/2026",
    "archive", "yearly/2026",
    new StorageTransferOptions { MetadataPreservation = StorageMetadataPreservation.BestEffort });

StorageTransferReport moved = await storage.MoveAsync(
    "incoming", "ready/item.bin",
    "processed", "item.bin");
```

`CopyAsync` and `MoveAsync` return a `StorageTransferReport`; `IsSuccess`, `Error`, and `ToResult()` work
as on a `Result`. Its `Outcome` (a `StorageTransferOutcome`) says what happened:

- `Completed`;
- `Skipped`, with a `SkipReason`;
- `Failed`, when nothing was committed;
- `NeedsReconciliation`, when the transfer left a mixed state;
- `Cancelled`, when the caller cancelled before anything was committed.

`CopyAsync` and `MoveAsync` never throw for a transfer that could not run: a token cancelled before the
call, an unregistered connection id (`storage.not_found`), invalid options or paths, or a provider that
throws all come back as a report. Only a null or blank connection id throws (`ArgumentException`). The
report also carries `SourceType`, `Files`, `Directories`, `Bytes`, `BytesResumed` (bytes reused from an
earlier attempt), and `SkippedFiles`; `SkipReason` is a `StorageSkipReason` (`DestinationExists`,
`SourceNotNewer`, `SameSize`, `Unchanged`, `AlreadyComplete`, or `Link`).

For a single file it also gives:

- `WrittenPath`, the path actually written (it differs after `Rename`);
- the number of bytes and, when verified, the SHA-256;
- the destination's new `DestinationETag` and `DestinationVersionId`.

When a transfer does not finish, these fields say exactly what it left behind: `DestinationCommitted`,
`SourceDeleted`, `StagingLeftBehind`, `BackupRestored`, `BackupLeftBehind`, and a `ResumeToken`.
`SourceDeleted` is exact: it is `true` only when the whole source of a move is gone, so a directory move
that kept skipped files reports `Completed` with `SourceDeleted = false`.

A single file's committed destination is never rolled back; what goes wrong afterwards is reported
instead:

- a backup or staging object that the library, or the provider's own replace, could not remove makes the
  transfer `Completed` with `BackupLeftBehind` or `StagingLeftBehind` set;
- a destination that does not hold the committed length (or, when verified, digest) is
  `NeedsReconciliation`, even without `Verify`, because someone else may have written it (or the promote
  went wrong). The previous version is then kept, never deleted and never put back: the report names it in
  `BackupLeftBehind`.

A directory transfer that fails part-way does undo the files it already committed, but each only while it is
still the version this transfer committed: the delete, or the restore of the previous version from its
backup, carries that version as a condition where the provider enforces one, and is otherwise preceded by
a comparison just before. A file someone else changed after the commit, or whose committed version could
not be read, is left where it is: the transfer is `NeedsReconciliation` (`storage.partial_failure` whose
details carry `transferError=<code>`, `rollbackError=storage.partial_failure`, and the rollback's own
`fileCleanupErrors=…` or `restoreErrors=…`, such as `storage.conflict`; or a `storage.cancelled` saying the
rollback was incomplete), and the previous version stays in the backup named by `BackupLeftBehind`. A
directory transfer reports the weakest `ConditionEnforcement` of its files, and
`BackupLeftBehind`/`StagingLeftBehind` for the first file that left one. `Verify` works for directories,
file by file; a condition, a source pin, `ExpectedSourceLength`, `ExpectedSha256`, and a `ResumeToken` apply
to a single file only (`storage.invalid_content` otherwise).

The transfer coordinator:

1. leases both active connections;
2. validates normalized path relationships (equal paths and a destination inside its source are rejected);
3. stages each destination file under an internal name;
4. relays cross-provider data through a pipe with 1 MiB maximum read-ahead;
5. backs up overwritten files: with a server-side copy where the destination has one; on FTP and SFTP a
   single file needs no backup, because the provider's own replace keeps the old file until the new one is
   in place (so nothing is downloaded and uploaded again), and a directory transfer renames the old file
   aside instead; any other destination without a server-side copy (a third-party backend that can move
   files) also has the old file renamed aside before the promote. When a later file fails, it rolls back only the files that
   still hold what this transfer wrote (see above);
6. deletes a move source only after every destination file commits, file by file and only while each
   file is still the version it listed, then removes the emptied folders without recursion.

A file added to, or changed in, the source of a directory move while it runs stays where it is, and the
move is `NeedsReconciliation` (details `sourceChanged=N;sourceAdded=N`).

Folder renames on the same FTP, SFTP, or local connection use a single server-side rename
(`AtomicMove`) instead of copying the tree, but only when the destination does not exist; onto an
existing folder the transfer relays and merges, on every provider. A native rename moves links as they
are: `LinkHandling` applies to relayed transfers. WebDAV folder moves always relay, because a WebDAV
`MOVE` can fail half-way (a `207 Multi-Status` answer is reported as `storage.partial_failure`). A native
folder move names no files, so its report counts `Files`, `Directories`, and `Bytes` by listing the
destination afterwards.

A file moved within one connection stays on the server:

- On FTP, SFTP, and Local it is the server's rename.
- On an object store (S3, Azure, Google Cloud, Swift) it is a copy pinned to the version the library read,
  then a delete of that version only.
- Where the move cannot be pinned (WebDAV refuses the pin, and a source without an ETag or version has
  nothing to pin), the source is compared with what was read immediately before the server's own move, and
  the report's `ConditionEnforcement` is `CheckedBeforeCommit`: a write in that short window is not seen.
- `ConflictPolicy = Rename` picks the free name first and then uses the server's rename or move, trying the
  next free name (up to 8 times) if the chosen one is taken at that moment. It does not relay, so it works
  with `Session.MaxSessions = 1`.

Safe same-provider file copies stay server-side when the provider can guarantee them. A transfer with
guarantees (a condition, a pin, `Verify`, an expected length or digest, resume) or with
`MetadataPreservation = Discard` never uses the provider's one-step copy or move: it stages and promotes.
On one connection with a server-side copy, a transfer whose only guarantees are a destination condition or
`ExpectedSourceETag` makes its staging copy on the server; everything else relays the bytes through the
client.

A relay within one FTP or SFTP connection holds two sessions at once (one reading, one writing), so it
needs `Session.MaxSessions` of at least 2. With 1 it fails at once with `storage.unsupported`
(`requiredSessions=2;maxSessions=1`) instead of waiting for a session that never frees up. Renames and
moves on the server need only one session. A file copy within one SFTP connection is always a relay (SFTP
has no server-side copy), so it needs two sessions too, also through `IStorageService.CopyAsync`, which
goes through the library's transfer.

Local folders transfer without registering a connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

These return a `Result<StorageDirectoryTransferReport>` with file, directory, byte, and skipped-file
counts, and throw `OperationCanceledException` on a cancel. Links in a local upload follow `LinkHandling`
(rejected by default); on Windows only symbolic links and junctions count as links.

## Guaranteed transfers

A single-file transfer can be made to promise exactly what was planned:

```csharp
var report = await storage.CopyAsync("sftp", "in/report.pdf", "s3", "archive/report.pdf", new StorageTransferOptions
{
    DestinationCondition = new StorageMutationCondition { ExpectedETag = seenDestination.ETag }, // replace only this version
    ExpectedSourceETag = plannedSource.ETag,        // or SourceVersionId, to read an exact version
    ExpectedSourceLength = plannedSource.Size,      // shorter or longer fails without committing
    Verify = true,                                  // SHA-256 during the copy, then the destination confirmed
    ExpectedSha256 = knownDigest                    // optional
});
```

- **Staging.** Content always goes to a staging object beside the destination first, and its length
  and digest are checked there.
- **Verification.** A verified copy is confirmed on the destination before it is promoted: by the
  server's SHA-256 where the server keeps one, otherwise by reading it back. `VerifiedBy` says which
  (`server` or `reread`), and `Sha256` holds the digest.
  After promotion the destination is confirmed again by its length and, where the server keeps one,
  its SHA-256.
- **Destination condition.** `DestinationCondition` is checked before the copy starts and again just
  before promotion, and then handed to the provider's move, which enforces it in the same request where
  it can. `ConditionEnforcement` (a `StorageConditionEnforcement`: `None` without a condition) reports how
  it was enforced:
  - `Atomic` when the provider enforced it in the committing request: create-new (`Overwrite = false`)
    on Local, WebDAV, Azure, Google Cloud, and S3 servers that enforce `If-None-Match` on `CopyObject`; replace-only-this-version on Azure, Google Cloud, and S3 servers that enforce
    `If-Match` on `CopyObject`.
  - `CheckedBeforeCommit` otherwise: FTP and SFTP (their protocols have no conditional rename), Swift
    (its server-side copy takes no destination condition), a version condition on Local and WebDAV, and
    S3-compatible servers that ignore or reject those headers. AWS S3
    enforces them; MinIO does not on `CopyObject` (see `ConditionalRequests` in
    [Connections](connections.md#cloud-emulators-and-compatible-services)).

  When the condition fails at promotion, the destination is left exactly as the other writer left it; a
  destination deleted by someone else meanwhile is not brought back (a promote the provider refused is
  not treated as having touched the destination).
- **Asking a connection.** `GetConditionEnforcementAsync` (an extension on `IStorageService`, namespace
  `CL.Storage.Abstractions`) returns how a connection enforces a `StorageConditionKind` (`CreateOnly`,
  `MatchVersion`, `DeleteMatchVersion`), for an upload or, with `serverSideCopy: true`, for a server-side
  copy or move: `Atomic` or `CheckedBeforeCommit`, never `None`. On an S3 connection under
  `ConditionalRequests = Auto` it runs the connection's probe first when it has not run yet. The
  `ConditionalCreate`/`ConditionalUpdate`/`ConditionalDelete` flags describe the provider; under `Auto`
  they are provisional until the probe has run, and then name only what the server enforces.

  ```csharp
  var enforcement = await files.GetConditionEnforcementAsync(StorageConditionKind.DeleteMatchVersion);
  ```

- **Check-before windows.** `CheckedBeforeCommit` means the condition was compared immediately before the
  committing request; a write by someone else in that short window is not detected. This is where each
  guarantee has that window:

  | Connection | Create-new | Replace only this version | Delete only this version (a move's source, a sync delete, a rollback) |
  |---|---|---|---|
  | Local | atomic | checked just before | checked just before |
  | FTP, SFTP | checked just before | checked just before | checked just before |
  | WebDAV | atomic | checked just before | checked just before |
  | Azure Blob, Google Cloud | atomic | atomic | atomic |
  | S3 (AWS, or `ConditionalRequests = Enforced`) | atomic | atomic | atomic |
  | MinIO (`Auto`) | uploads atomic; copies, moves, and staged promotes checked just before | checked just before, uploads too once the probe has run | checked just before |
  | Swift | uploads atomic; copies, moves, and staged promotes checked just before | checked just before | checked just before |

  Staged uploads (with `Verify`, `ExpectedLength`, `ExpectedSha256`, or `Resume`) and sync copies promote
  through a server-side move, so on MinIO and Swift their create-new drops to a check just before;
  an upload returns the stored item and does not report which it was. An upload with a version
  `Condition` is staged whenever the connection does not declare `ConditionalUpdate`, which on an S3
  server needs `If-Match` enforced on both `PutObject` and `CopyObject`: on MinIO it is therefore checked
  just before once the probe has run. A connection's own
  `DeleteAsync` with a `Condition` is refused (`storage.unsupported`) on Local, FTP, SFTP, and WebDAV; the
  library's moves, syncs, and rollbacks compare there immediately before deleting instead.
- **Pinned version.** `SourceVersionId` reads that version, and its length, ETag, and a move's source
  deletion refer to it. Moving an older version does not delete the source, because the current object
  is another version (`NeedsReconciliation`).
- **Pinned source.** A source pinned by ETag is read again after streaming. If it changed in between,
  the transfer fails with `storage.conflict` and nothing is committed.
- **Moves.** A move deletes its source only while it is still the version that was copied: with a
  conditional delete where the provider enforces one (Azure, Google Cloud, S3 servers that enforce
  `If-Match` on `DeleteObject`), otherwise by comparing it immediately before the delete (Local, FTP, SFTP,
  WebDAV, Swift, MinIO; see the table above). A source that changed is kept, and the report is
  `NeedsReconciliation` with `SourceDeleted = false`. A move that committed but kept its source still
  publishes the copy event, so watchers and caches see the new file.
- **Local files** carry a weak ETag (`W/"…"`) made of the last-write time, creation time, and size, so
  conditions work on them too, checked right before the file is replaced. It is only as fine as the file
  system's clock: on FAT (2 s), exFAT, HFS+ (1 s), some SMB and NFS shares, or after a tool restores file
  times, two versions can share it. It is compared as a weak validator: a different ETag proves the file
  changed, but a matching one never proves it did not. Where the library needs that proof (resuming
  staged bytes, "unchanged since it was read", rolling back only its own version), a weak match falls
  through to comparing size and time, and a weak-ETag source resumes staged bytes only with `Verify`.
- **Cancellation** is reported, not thrown: the report's `Outcome` is `Cancelled` (error
  `storage.cancelled`), staging and backup objects are removed, and a resumable transfer's `ResumeToken`
  continues it. A cancel that arrives after the destination was committed does not make the transfer
  `Cancelled`, and the copy event is still published. A copy is then `Completed`; so is a move whose
  source turns out to be gone. A move whose source is still (partly) there is `NeedsReconciliation` with
  `destinationState=complete`, and a directory move that was deleting its sources adds
  `sourceItemsDeleted=N`. A report for a transfer stopped before its commit keeps `StagingLeftBehind`,
  `BackupRestored`, and `ResumeToken`. `IStorageService.CopyAsync`/`MoveAsync` on a connection throw
  `OperationCanceledException`, like every other connection call.
- **Server-side copies that have started** are not abandoned on a cancel: an Azure copy is waited for, and
  an S3 `CopyObject` or `CompleteMultipartUpload` is not cancelled once sent (it may commit with its reply
  lost). Either is then reported as committed. A multipart S3 completion whose reply was lost is
  recognised by its ETag, and in a versioned bucket among the key's versions when another writer is
  already on top.

`StorageUploadOptions` has the matching `ExpectedLength`, `Verify`, and `ExpectedSha256` for uploads.
`ExpectedSha256` implies `Verify`, before and after the commit. An upload's `Condition` on a connection
that cannot enforce it in its own upload is checked right before the staged file replaces the
destination; an upload returns the stored item, not how the condition was enforced.

A staged upload that committed but whose provider left an internal object behind (its own backup, or a
staging object it could not remove) fails with `storage.partial_failure` carrying
`destinationState=complete` and one `leftBehind=<path>` entry per object still there: the content is at
the destination, so treat it as written and clean up rather than retry. A promote that failed without
committing names, in a `leftBehind` entry, a staging object that could not be removed afterwards.

## Streamed writes

`OpenWriteAsync` (an extension in `StorageWriteExtensions`) returns a `StorageWriteStream` to write a
file's content into, for push-style producers:

```csharp
var opened = await files.OpenWriteAsync("exports/data.csv", new StorageUploadOptions { Verify = true });
await using var writer = opened.Value!;
await writer.WriteAsync(chunk);
Result<StorageItem> committed = await writer.CommitAsync(); // or AbortAsync(); disposing without commit aborts
```

The content is staged and checked like any other upload, and the destination appears only on commit.
`Overwrite`, the conflict policy, and a `Condition` are decided when the stream is opened, before any byte
is written (the condition is checked again at the commit): a policy that skips returns `storage.conflict`
with a `skipReason` detail, `Rename` writes to the free name in `DestinationPath`, and `Resume` is refused
(`storage.invalid_content`), since pushed content cannot be replayed. `BytesWritten` counts what was
written. A cancel of `CommitAsync` throws `OperationCanceledException` and removes the staging object
unless the content was already promoted.
With `Verify`, the committed item carries the content's `Sha256`, as verified uploads do. Disposing
without committing aborts without waiting; `DisposeAsync` and `AbortAsync` wait until the staging object
is gone.

A write that fails or is cancelled aborts the stream: its bytes may already be buffered, so writing them
again would duplicate them. Start a new stream instead. When the destination stopped accepting data, the
write throws `StorageWriteException` (an `IOException`) whose `Error` says why.

When `CommitAsync` fails, the destination is as it was, unless the error carries
`destinationState=complete` (`StorageErrorInfo.DestinationCommitted`): then the content was committed, but
it does not read back as written (another writer may have replaced it) or the provider left an internal
object behind. Every internal object still there (a staging object that could not be removed, the
provider's own backup) is named by a `leftBehind` entry in the error's details. When the promote succeeded
but the destination could not be read back afterwards (a transient read is retried a few times first), the
read's error is returned with `destinationState=complete` too.

## When the destination already exists

`ConflictPolicy` on `StorageUploadOptions` and `StorageTransferOptions` mirrors FileZilla's
"target file already exists" choices:

| Policy | Behavior |
|---|---|
| `Fail` | return `storage.conflict` |
| `Overwrite` | replace it |
| `Skip` | keep it; an upload succeeds and returns the existing item (without a write event), a copy or move reports `Skipped` |
| `OverwriteIfNewer` | replace only when the source is newer (2 s clock slack) |
| `OverwriteIfSizeDiffers` | replace only when the sizes differ |
| `OverwriteIfNewerOrSizeDiffers` | either of the above |
| `Rename` | write the first free name: `name (1).ext`, `name (2).ext`, … (a folder counts as taken; `name (1).ext` goes on to `name (2).ext`) |
| `Resume` | continue a staged copy (see below) |

When `ConflictPolicy` is not set, the `Overwrite` flag decides as before.

```csharp
await files.UploadFileAsync("backup/db.bak", @"C:\dumps\db.bak",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer });

var report = await storage.UploadDirectoryAsync(@"C:\site", "web", "public",
    new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers });
Console.WriteLine($"{report.Value!.Files} uploaded, {report.Value.SkippedFiles} unchanged");
```

- Directories are decided file by file, so a directory transfer with a conditional policy relays.
- A folder at the destination path is never skipped or replaced: `Skip` and the `OverwriteIf…` policies
  return `storage.conflict` for it.
- Moving a directory deletes only the source files that were transferred; skipped files stay.
- Conditional policies on copies and moves are applied by `StorageLibrary` and the connections it returns,
  not by a backend's own `CopyAsync`/`MoveAsync` used on its own.
- Moving a single link with `LinkHandling = Skip` skips it (`SkipReason = Link`) and leaves it in place.
- With a pinned `SourceVersionId`, "newer" and "size differs" are judged by that version, not the latest.
- `UploadFileAsync` supplies the local file's time; for stream uploads set `SourceLastModified`.
  Unknown times or sizes count as newer or different.
- `DownloadToFileAsync` takes a `conflictPolicy` for the local file.

## Resume and append

`ConflictPolicy = Resume` writes through a resumable staging object (`.cl-storage-part-…`). The
destination is replaced only once that object is complete, so it is never half-written.

- **Continuing after a failure.** The staged bytes stay when an attempt fails. The next attempt reads
  only the missing tail and appends it (FTP `APPE`, SFTP append, local files), provided it has the same
  destination and the same source.
- **The same source only.** Staged bytes are keyed by the source's identity, so a different source of the
  same length never continues them. Uploads need a seekable stream and `SourceLastModified`, or a
  `SourceIdentity` marked `SourceIdentityIsContentVersion = true` (a content hash, an ETag, a version id:
  something that changes whenever the content does). `UploadFileAsync` sets the path and the time. A
  `SourceIdentity` alone, without that mark, is refused with `storage.invalid_content`, since a path or
  name stays the same when a file is edited and an edited file of the same length would continue the old
  prefix. Never mark a name or a path as a content version.
- **Copies and moves** identify the source by its ETag, time, or version, and do not resume a source that
  has none. A source known only by a weak ETag (Local) reuses staged bytes only with `Verify`, which reads
  the staged prefix from the source again; without it the copy is not resumable and writes from the start.
- **One writer.** A resumable staging object is held by one transfer at a time, across processes too:
  beside it the transfer creates a lock marker (`<part file>.lock`, also a `.cl-storage-part-…` name)
  create-only, holding a random owner id and salted hashes of its machine, process, and start time (never
  the names or ids themselves), and reads it back to confirm it won (on FTP
  and SFTP, where create-only is checked before the write, after a 1 s pause, so a racing writer's marker
  has landed and wins). The hold
  lasts until the staged file has been promoted (or settled), so nobody appends between the last write
  and the commit. Another transfer of the same source to the same destination, in this process or another,
  stages privately instead (not resumable). A marker whose owner is gone is taken over: on the same
  machine as soon as its process no longer runs, from another machine only once it is 24 hours old. A
  connection that cannot create the marker create-only gets the private, non-resumable staging. A staged
  file taken over by another transfer is neither promoted nor removed (`storage.conflict`), and a staging
  object whose size changed under a transfer is never promoted.
- **Across restarts.** A failed or cancelled copy's report carries a `ResumeToken`, a `StorageResumeToken`
  record (`DestinationPath`, `StagingPath`, `BytesStaged`, and the source's path, ETag, version, length, and
  time) that serializes as JSON. Store it and pass it back in `StorageTransferOptions.ResumeToken`; this
  works from another process after a restart too. A
  token is followed only for exactly the same source, and only onto its own `.cl-storage-part-…` object;
  a token naming anything else is ignored, and never deleted. The one exception is the token's own part
  file for this destination staged from a source version that has since changed: that stale part file is
  removed when no transfer holds it.
- **A token does not mean overwrite.** A token never turns overwriting on by itself: an existing
  destination is replaced only under `ConflictPolicy = Resume` or `Overwrite = true`. A `ResumeToken` with
  `ConflictPolicy = Fail`, or with `Overwrite = false` and no policy, fails validation.
- **Integrity.** With `Verify`, the part staged earlier is read again from the source and must match
  before the rest is appended, so the digest covers the whole file.
- **Already complete.** A destination counts as complete only when its content provably matches; an equal
  size is not enough. For a copy or move both servers must report the same stored digest (SHA-256, then
  MD5), so a destination on Local, SFTP, or WebDAV, a pinned `SourceVersionId`, or a transfer given a
  `ResumeToken` is never taken as complete and is written again; for an upload the stream is hashed and
  compared with the destination's digest (read back where the server keeps none). A complete copy is
  `Skipped` with `SkipReason = AlreadyComplete`; a resumed move whose destination is complete deletes its
  source.
- **Destinations that cannot append** (without `StorageFeature.Append`: object stores and WebDAV) rewrite
  the staging object from the start.
- A staging object whose size cannot be read (for any reason but "not found") is kept, and the attempt
  fails rather than appending after bytes it cannot count.

```csharp
await files.UploadFileAsync("big/image.iso", @"D:\image.iso",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume });

await files.DownloadToFileAsync("big/image.iso", @"D:\image.iso", conflictPolicy: StorageConflictPolicy.Resume);

await using var line = new MemoryStream("entry\n"u8.ToArray());
await files.AppendAsync("logs/today.log", line);

await files.CleanupStaleStagingAsync("", TimeSpan.FromDays(1)); // leftovers of crashed transfers
```

`DownloadToFileAsync` with `Resume` downloads into `name.clstorage-partial` next to the destination and
records the remote version it holds (source, length, ETag, version, modification time) in
`name.clstorage-partial.json` before the first byte. An interrupted download keeps both; the next one
appends only the missing range, and only while the remote is still that version (checked again after the
tail is read). Anything else — a changed remote, a server that reports no ETag, version, or time, or a
local file this download did not leave — is downloaded again from the start. The destination is replaced
only when the content is complete. `AppendAsync` appends to a file directly, which needs `StorageFeature.Append`.
A resumable upload that fails carries `stagingPath` and `bytesStaged` in its error details.

## Progress and speed limits

Upload, download, and transfer options take a `Progress` sink. While bytes flow, reports arrive at most
every 250 ms; each finished or skipped file of a directory transfer, and the end, report too. They carry:

- `BytesTransferred`, `TotalBytes`, `BytesPerSecond`, and `EstimatedRemaining`;
- for directory transfers, the `ItemPath` of the current file plus `FilesCompleted`/`FilesTotal`.

Directory transfers accumulate bytes across files. Set `StorageTransferOptions.PreScan` to list a
directory first, so its reports carry totals and a time estimate. A server-side copy or move reports
its start and its end.

```csharp
var progress = new Progress<StorageTransferProgress>(p =>
    Console.WriteLine($"{p.ItemPath}: {p.BytesTransferred:N0} B at {p.BytesPerSecond / 1024:N0} KiB/s"));

await storage.CopyAsync("sftp", "exports", "s3", "archive", new StorageTransferOptions { Progress = progress });
```

Speed limits are set per connection and shared by all of its concurrent transfers:

```json
"transferLimits": { "maxUploadBytesPerSecond": 1048576, "maxDownloadBytesPerSecond": 5242880 }
```

`MaxTotalUploadBytesPerSecond` and `MaxTotalDownloadBytesPerSecond` in the `storage` section cap all
connections together. Limits are enforced inside each provider's upload and download, so they apply to
every path: `UploadAsync` and `DownloadAsync` streams used directly, the file helpers, and relayed
transfers. Download progress carries `TotalBytes` even without a `Length`: when the stream cannot
report it, the item's size is looked up once.

## Transfer queue

`OpenTransferQueueAsync` runs transfers in the background, like FileZilla's queue:

```csharp
using CL.Storage.Queue;

var opened = await storage.OpenTransferQueueAsync(new StorageTransferQueueOptions
{
    MaxConcurrentTransfers = 4,
    MaxTransfersPerConnection = 2,
    AutomaticRetries = 3,
    Store = myDurableStore          // optional: jobs survive restarts
});
await using var queue = opened.Value!;
queue.ProgressChanged += job => Console.WriteLine($"{job.Destination}: {job.Progress?.BytesTransferred:N0} B");

await queue.EnqueueUploadDirectoryAsync(@"C:\exports", "sftp", "incoming");
await queue.EnqueueCopyAsync("s3", "reports/q3.pdf", "sftp", "outbox/q3.pdf",
    new StorageTransferOptions { Verify = true, ConflictPolicy = StorageConflictPolicy.Resume },
    priority: 10, jobId: "q3-report");   // same id + same work = same job

await queue.WaitForIdleAsync();
```

**Options.** `StorageTransferQueueOptions`:

| Option | Default | Meaning |
|---|---|---|
| `MaxConcurrentTransfers` | 2 | jobs running at once (1 to 64); the ceiling under `AdaptiveConcurrency` |
| `MaxTransfersPerConnection` | 2 | jobs using one connection at once (1 to 64) |
| `AutomaticRetries` | 3 | automatic retries of transient failures (0 to 50) |
| `RetryBaseDelay`, `RetryMaxDelay` | 2 s, 5 min | backoff: doubling with jitter from the base up to the maximum |
| `StartPaused` | `false` | open the queue paused |
| `Store` | in memory | the `IStorageTransferJobStore` jobs live in |
| `WorkerId` | a new GUID | this queue's lease owner; keep it stable across restarts, distinct per running queue |
| `LeaseDuration` | 60 s | how long a claim lasts between renewals (renewed every third of it); 1 s to 1 day |
| `RequeueInterruptedWhenSafe` | `true` | put untouched jobs from an earlier process straight back in the queue |
| `MaxFinishedJobs` | 1,000 | finished jobs kept; the oldest beyond it are removed from the store; null keeps all |
| `AdaptiveConcurrency` | `false` | start at one transfer and adapt (see below) |
| `ProgressInterval` | 250 ms | minimum time between two progress events of one job |
| `EventContext` | none | a `SynchronizationContext` (a UI thread) to raise the queue's events on |
| `StoreRefreshInterval` | never | how often to read the store for jobs other processes changed; up to 1 day |
| `ShutdownTimeout` | 30 s | how long disposing waits for running jobs and background work |
| `ControlTimeout` | 30 s | how long pause, cancel, and remove wait for a running job (see Control) |

**The queue.** `Jobs` is a snapshot of every job (highest priority first, then in queue order), `Get(id)`
returns one, and `FailedJobs` the failed ones. `IsPaused` and `ConcurrencyLimit` (the current limit under
adaptive concurrency) describe the queue. Besides `EnqueueCopyAsync`, `EnqueueMoveAsync`,
`EnqueueUploadAsync`, `EnqueueDownloadAsync`, `EnqueueUploadDirectoryAsync`, and
`EnqueueDownloadDirectoryAsync`, `EnqueueAsync(spec, priority, jobId)` takes a `StorageTransferJobSpec`
directly. `RefreshAsync` reads the store again (a job removed here while the store was read does not come
back), and `WaitForIdleAsync` waits until nothing is queued or running in this queue (jobs leased by other
workers do not count, and a paused queue with waiting jobs is not idle).

A `StorageTransferJob` is a snapshot: its `Record` (the stored `StorageTransferJobRecord`), the live
`Progress`, and the `LastReport` of a copy or move attempt in this process, with `Id`, `Kind`, `State`,
`BlockReason`, `Priority`, `Attempts`, `Source`, `Destination`, and `Error` for convenience. The record adds
`RetriesLeft`, `NextAttemptAt`, `Failure` (a `StorageTransferFailure` with code, message, and details),
`EnqueuedAt`, `StartedAt`, `FinishedAt`, the `Checkpoint` (a `StorageTransferCheckpoint`: the
`StorageTransferPhase` the last attempt reached, `NotStarted`, `Transferring`, `Committing`, or
`DeletingSource`, and a `StorageResumeToken` for staged data), and the store-owned `Revision`, `LeaseOwner`,
`LeaseExpiresAt`, and `FencingToken`. `IsFinished` is true for `Completed`, `Failed`, and `Cancelled`.

**Jobs are data.** A `StorageTransferJobSpec` describes the `Kind` (a `StorageTransferKind`: `Copy`, `Move`, `UploadFile`,
`DownloadFile`, `UploadDirectory`, `DownloadDirectory`), both endpoints, and the options of that kind
(`TransferOptions`, `UploadOptions`, or `DownloadOptions` with `DownloadConflictPolicy`); progress sinks are
not part of a spec; `SourceLabel` and `DestinationLabel` describe the endpoints (`connection:path`, or a
local path). `ToJson`/`FromJson` store it, with enums by name and a `SchemaVersion` (a spec from a
newer schema fails to read). Caller-chosen ids make enqueueing idempotent, as `SameWorkAs` decides
(connection ids compare without case, and options left null equal the defaults):

- the same id with the same work returns the existing job;
- the same id with different work fails with `storage.conflict`.

**A durable, shared store.** `IStorageTransferJobStore` holds every job; it is in memory by default.

- Every record has a `Revision`, and saves are compare-and-swap on it: a stale copy never overwrites a
  newer state, so a job finished in another process cannot be re-queued here.
- A worker claims a job at the revision it read, with a lease that carries a fencing token, and renews the
  lease while it runs. The store owns the lease fields.
- A save with a stale lease is refused, so a worker that lost its lease never records an outcome.
- As a result, two processes never run the same job.
- A failing store does not wedge the queue: renewals and saves are retried while the lease holds, and a
  job whose outcome could not be recorded is recovered later. A store failure never uses up a job's
  `AutomaticRetries`, but it does not go on for ever either: a claim that fails, or an attempt the store
  stopped, is tried again after a delay that doubles from `RetryBaseDelay` (at least 100 ms, at most
  `RetryMaxDelay`; a re-queued attempt is announced with `storage.unavailable`), and after 8 such
  failures in a row the job fails with `storage.unavailable` (only in this queue's view when the store
  cannot record even that).
- Removing, clearing, and pruning are conditional on the record's revision and lease too, so they never
  delete a job another process is running or has just retried.

**Writing a store.** `InMemoryStorageTransferJobStore` is the reference implementation (it takes a
`TimeProvider` for tests). `IStorageTransferJobStore` has eight methods, each atomic for its job:
`LoadAsync` (every record), `AddAsync` (null when the id exists), `GetAsync`, `TryClaimAsync(jobId,
workerId, expectedRevision, duration)` (a `StorageTransferLease`, or null when the record changed or another
worker holds an unexpired lease; the same worker may claim again), `RenewAsync`, `ReleaseAsync`,
`SaveAsync(record, lease, releaseLease)` (the stored record at its next revision, or null when refused; the
lease fields in `record` are ignored), and `RemoveAsync(jobId, expectedRevision, lease)` (true when removed
or already gone). A save or remove with a lease succeeds only while that lease is current (same owner and
token, unexpired); one without a lease only while nobody holds an unexpired lease. A store must:

- decide lease expiry by its own clock alone (a database should use its own `now()`). The queue reads
  `LeaseExpiresAt` with its own clock only as a hint, to decide when to retry a claim or when a renewal is
  overdue; a skewed worker clock costs a refused claim or an early stop, never two workers holding one job;
- issue, on every claim, a fencing token greater than any issued before for that job id, including before
  the id was removed and added again;
- continue revisions the same way: `AddAsync` stores revision 1 for an id never stored before, and above
  every revision the id had when it is removed and added again, so a copy from its earlier life can
  neither save over nor remove the new job (keep the last revision and fencing token of each removed id);
- remove a record only at the expected revision (`RemoveAsync(jobId, expectedRevision, lease)`), and end a
  lease without a save (`ReleaseAsync`);
- keep `SchemaVersion` with the record and its spec, or store `StorageTransferJobRecord.ToJson()` and read
  it back with `FromJson` (`CurrentSchemaVersion` is what this version writes). A queue leaves records
  written by a newer schema (`IsReadable` false) alone. `FromJson` throws for
  a newer schema, so a store that keeps JSON must skip a row it cannot read in `LoadAsync` instead of
  failing the whole load, and may return null for it from `GetAsync` (the job leaves this queue's view,
  unchanged in the store);
- where the revision and the lease fields (`Revision`, `LeaseOwner`, `LeaseExpiresAt`, `FencingToken`) are
  kept in columns of their own next to the JSON, return the columns' values: the copies inside the JSON
  are only as new as the last save, since claims, renewals, and releases change the columns alone.

**Restarts.** A transfer records its phase as it goes. When a new process finds a job that was running
in an earlier one:

- if the destination was never touched (phase `NotStarted` or `Transferring`), the job goes straight back
  to the queue, and a resumable transfer continues from its staged bytes when the job's options let a
  resume token replace the destination (`ConflictPolicy = Resume`, or `Overwrite` without another policy;
  otherwise it stages again);
- otherwise the job becomes `Interrupted`, for a person to decide (and a `StorageTransferInterruptedEvent`
  is published). File uploads and downloads cannot tell the queue when they reach their destination, so
  once running they always count as touched. With `RequeueInterruptedWhenSafe = false`, every job found
  running becomes `Interrupted`.

The recorded phase never goes backwards, within an attempt or from one attempt to the next: a retried
directory copy keeps the highest phase an earlier attempt reached, so a copy that began committing is
never mistaken for an untouched one.

A process restarted with the same `WorkerId` takes back its own leases at once, so give every running
queue its own `WorkerId`. A transfer that succeeded is recorded `Completed` even if it was being paused,
cancelled, or shut down as it finished, and a move whose destination committed stays
`NeedsReconciliation` even when a cancel or pause arrives, so a committed move is never run again.

Disposing the queue, or stopping the library that opened it, waits up to `ShutdownTimeout` (30 s by
default) for running transfers, and for the queue's background work (store refreshes, recovery) and store
calls; each transfer ends `Queued` or `Interrupted` by its phase, with its lease released, and never
`Failed`. Once `DisposeAsync` returns, the queue makes no new call to the store, so the store may be
closed: an attempt that outlived `ShutdownTimeout` records nothing more, and its job is recovered by the
restart rules once its lease lapses (a store call already in flight is the store's to finish). The
library's synchronous `Dispose()` stops its queues the same way, so it blocks the calling thread while
they stop, up to their `ShutdownTimeout`; prefer `DisposeAsync` or the CodeLogic stop. After disposal, calls that change jobs return `storage.unavailable`. A queue that
finishes opening while the library is stopping is disposed at once, and `OpenTransferQueueAsync` fails
with `storage.unavailable`.

**States.**

| State | Meaning |
|---|---|
| `Queued`, `Running`, `Paused` | waiting, in progress, or held |
| `Completed`, `Failed`, `Cancelled` | finished (`MaxFinishedJobs`, 1,000 by default, keeps the newest) |
| `Blocked` | needs a person: `BlockReason` (a `StorageTransferBlockReason`) is `Trust` (an untrusted host key or certificate) or `Credential` |
| `NeedsReconciliation` | left a mixed state; the job's `LastReport` says which |
| `Interrupted` | found running after a restart, with the destination already touched |

Only transient failures are retried, with exponential backoff and jitter that honours a server's
`Retry-After` (up to 30 days). `FailedJobs` and `RetryFailedAsync` cover `Failed` jobs only; `Cancelled`,
`Blocked`, `NeedsReconciliation`, and `Interrupted` jobs are retried one by one with `RetryAsync` once a
person has looked at them. A provider's `storage.partial_failure`, or an error with
`destinationState=complete`, ends a job as `NeedsReconciliation`. `storage.host_key_rejected` and a TLS
failure about the server's certificate block a job on `Trust`; `storage.authentication_failed` and a
refused client certificate block it on `Credential`.

The numbers of `StorageTransferState` are fixed: `Queued` 0 to `Cancelled` 4 as in 4.8.93, then `Paused`,
`Blocked`, `NeedsReconciliation`, and `Interrupted`. New states are only ever added at the end.

**Control.**

- Pause or resume the whole queue with `Pause`/`Resume`, or one job with `PauseJobAsync`/`ResumeJobAsync`.
  A running resumable transfer keeps its staged data while paused.
- `CancelAsync`, `RetryAsync`, and `RemoveAsync` act on one job. `RetryAsync` re-queues a `Failed`,
  `Cancelled`, `Blocked`, `NeedsReconciliation`, or `Interrupted` job and resets `Attempts` and
  `RetriesLeft` (clearing its failure and block reason), so backoff starts from the base delay again.
- Pausing or cancelling a running job waits until its transfer has stopped, and succeeds only if it took
  effect; when the job finished first, or committed a move, the call returns `storage.conflict`. Removing a
  running job cancels it and removes it once its attempt stops (a completed job is removed too), except
  when the attempt ended `NeedsReconciliation` (a move whose copy committed): that job is kept, and the call
  returns `storage.conflict` saying why. The wait is bounded by
  `StorageTransferQueueOptions.ControlTimeout` (30 s by default, zero to one day) and by the call's token,
  since a provider may not honour cancellation at once, or at all. The request is held by the queue before
  the wait and still takes effect when the attempt stops; a call that stops waiting first fails with
  `storage.timeout` (or `storage.cancelled`). With `ControlTimeout = TimeSpan.Zero` a call on a running job
  returns `storage.timeout` at once, and the request applies when the attempt stops. A job running in
  another process cannot be controlled from here (`storage.conflict`).
- Methods that return a `Result` never throw `OperationCanceledException`; an enqueue whose store call was
  cancelled after the store committed returns the stored job. A null job id on a control method is a failed
  result (`storage.invalid_content`), and `Get(null)` returns null; enqueueing with a null id generates one.
- Priorities are integers, higher first. Change a waiting job's with `SetPriorityAsync`, or reorder with
  `MoveUpAsync`/`MoveDownAsync`. Within a priority, jobs run in `Order` (then id), a time-based number, so
  jobs added by different queues sharing a store keep roughly the order they were added in. A move is one
  save of one job. When several jobs share an order, so there is no room between them, their orders are
  first spread apart, one save per job from the last to the first, so that every step keeps the queue's
  order and a save that fails part-way reorders nothing.
- Enqueueing checks that the spec fits its kind, and refuses with `storage.invalid_content`: a blank source
  or destination path or job id, a missing connection the kind needs, a connection it does not take (an
  upload has no source connection, a download no destination connection), options of another kind
  (`TransferOptions` on a file upload or download, `UploadOptions` on anything but a file upload,
  `DownloadOptions` or `DownloadConflictPolicy` on anything but a file download), and a `Progress` sink in
  any options (subscribe to `ProgressChanged` instead).

**History and events.**

- `MaxFinishedJobs` caps history (checked after every attempt, when a job finishes, and when the queue
  opens), and `ClearAsync(states)` removes jobs in those states: `Completed`, `Failed`, and `Cancelled` by
  default. `Running` is never cleared, and a job whose attempt is live is skipped.
- Progress events are throttled by `ProgressInterval`.
- `EventContext` raises `JobChanged`, `ProgressChanged`, and `JobRemoved` on a UI thread. Without one,
  handlers run inline on the queue's own threads (progress on the transfer's), never under one of the
  queue's locks, and one progress report at a time per job; a report that arrives while a handler runs is
  held back like a throttled one. Handlers must be quick and must not wait synchronously on the queue's
  methods: an attempt does not end while its progress handler runs, so a handler that blocks on pausing,
  cancelling, or removing its own job stalls that job until `ControlTimeout` ends the wait.
- The event bus receives started, completed, failed, cancelled, retrying, blocked, needs-reconciliation,
  and interrupted events.
- The last progress report of each attempt always arrives, even when it fails; none arrives after the
  attempt ended.

**Adaptive concurrency.** With `AdaptiveConcurrency`, the queue starts with one transfer. It adds one
after each success and halves after a transient failure, up to `MaxConcurrentTransfers`.

`EnqueueDownloadAsync` takes `StorageDownloadOptions`, so a job can fetch an exact version or a range.

## Compare and sync

```csharp
using CL.Storage.Sync;

var diff = await storage.CompareAsync("sftp", "site", "s3", "backup/site");
foreach (var entry in diff.Value!.Entries.Where(e => e.Kind != StorageDiffKind.Same))
    Console.WriteLine($"{entry.Kind,-18} {entry.Reasons,-12} {entry.RelativePath}");

var options = new StorageSyncOptions
{
    Direction = StorageSyncDirection.TwoWay,
    StateStore = baselines, SyncId = "site",          // a baseline makes two-way three-way
    ConflictPolicy = StorageSyncConflictPolicy.Block,
    MaxDeletes = 100, MaxDeletePercent = 10,
    Verify = true,
    Compare = new StorageCompareOptions { Exclude = ["**/*.tmp", "cache/**"] }
};
var plan = (await storage.PlanSyncAsync("sftp", "site", "s3", "backup/site", options)).Value!;
// show plan.Actions, plan.Conflicts, and plan.Warnings; store plan.ToJson(); approve plan.Digest
var report = await storage.ApplySyncAsync("sftp", "site", "s3", "backup/site", plan, approvedDigest, options);
```

`CompareAsync` and the sync methods also work between any two `IStorageService` instances, such as a
`LocalStorageBackend` over a local folder. `SyncAsync` plans and applies in one call; when conflicts block
it, its failure (`storage.conflict`) names up to ten of them and carries `conflicts=N` in its details, as
`ApplySyncAsync` does for a plan with blocked conflicts.

### Directions

| Direction | Behavior |
|---|---|
| `Update` (default) | copy new and changed files; never delete; never replace a newer destination with an older source |
| `Mirror` | make the destination match the source; delete extra items with `DeleteExtraneous`; leave a newer destination alone |
| `TwoWay` | change both sides, folders included (see below) |

With a baseline (`StateStore` + `SyncId`), `TwoWay` is a three-way sync. An edit or deletion on one side
is carried to the other; with `PropagateDeletes = false` a file or folder deleted on one side is copied
back from the other instead. Changes on both sides are conflicts: `BothModified`, `BothCreated`, or
`DeleteVersusModify`. A folder removed on one side is removed on the other only once
everything inside it goes too. Without a baseline, missing files and folders are copied and files that
differ are conflicts.

- A path that is a file on one side and a folder on the other is left alone, with everything below it.
- Where one side ignores case, a name spelled differently on each side (`Readme.TXT` / `readme.txt`) is
  one item, and each side keeps its own spelling, folders included: a new file under `Docs/` goes into the
  destination's existing `docs/`, even when that folder is empty or holds only items the filters leave out,
  and a copy back to the source uses the source's current spelling of its folders. Case is folded with `ToUpperInvariant`, and names in the two Unicode
  normal forms (NFC and NFD) are the same item.
- The baseline saved after a run records the versions both sides agreed on when the plan was made and the
  versions the run itself wrote, never a listing taken afterwards. A file edited during or just after a run
  is still seen as changed next time, and a step that did not complete is tried again. Neither side having
  changed while their content differs is a conflict, not "in sync".

### Conflict policies

| Policy | Behavior |
|---|---|
| `Block` (default) | plan the conflict; the plan cannot be applied until it is resolved, unless `ApplyWithConflicts` is set |
| `KeepBoth` | the source's version keeps the name; the destination's version is kept on both sides as `name (conflict xxxxxxxx).ext`, named from its identity (`… 2`, `… 3` when that name is taken); for a delete against a modify, the modified file is copied back to the side that deleted it |
| `NewerWins` | the newer side wins; a modification beats a deletion (it is copied back to the side that deleted it); two versions whose times are within `TimeTolerance`, or unknown, stay a `Conflict` step (`NotRun`) that does not block the rest |

### Plan, then apply

- The plan lists every step with the versions it depends on. It serializes with `ToJson()`, and
  `Digest` is a SHA-256 over its content, which includes its `SchemaVersion`, both connection ids, whether
  case was ignored, and `OptionsDigest`, a digest of the options that shape the plan.
- `ApplySyncAsync` refuses a plan that is not the one approved or was changed after it was made
  (`storage.conflict`), and, with `storage.invalid_content`, a plan made for other connections, other
  folders, another `SyncId`, or in an older format (plan again), and options that differ from the plan's.
  Only `MaxConcurrency`, `ItemRetries`, `ContinueOnError`, `DryRun`, `Progress`, `StateStore`,
  `ApplyWithConflicts`, and `Compare.HashConcurrency` may differ at apply; `TimeTolerance`, the filters, and the deletion limits
  must be set when planning. `ApplyWithConflicts` is an apply-time choice: a plan made without it can be
  shown with its conflicts, and its other steps applied with it. (Plans made by earlier preview builds,
  whose options digest still covered those two, are refused: plan again.)
- A plan records its connections by id only (`SourceConnectionId`, `DestinationConnectionId`): a connection
  registered again under the same id is taken for the same store, and one registered under a new id
  refuses the plan. Keep connection ids stable between planning and applying.
- The baseline is bound to `SyncId` only, not to the connections or folders: give each pair of folders its
  own `SyncId`, since a baseline read against other folders takes their differences for edits and
  deletions (the empty-side and deletion limits still apply).
- It also refuses a plan whose baseline moved on because another run saved in between (`storage.conflict`).
- Each step re-checks its items first. A step whose item changed since planning, or whose path changed
  type (a folder where a file was), is reported `Stale` and not taken. A check that fails for any reason
  other than "not found" fails the step (or retries it), rather than treating the item as gone.
- What a step deletes, overwrites, or renames aside must be exactly the version planned: that check uses
  no time tolerance (`TimeTolerance` applies only to recognising a source's version), so a same-size edit
  within the tolerance is never deleted or overwritten. On FTP servers without `MLSD`/`MLST`, `LIST` gives
  times to the minute (or only a date for files older than about six months) while a lookup of one file
  (`MDTM`) gives seconds. Such a time carries its precision in `StorageItem.ModifiedPrecision`, and stands for
  the interval it was truncated from: the same file then matches at apply, and a copy whose time was kept does
  not look changed on the next compare. An edit within that same minute (or day) cannot be told apart there.
- Planning never throws for a provider whose listing throws or a filter pattern that runs too long: the
  plan (or `CompareAsync`) fails with the provider's error, or `storage.invalid_content` for the pattern.
  Only the caller's cancellation is thrown.
- Two applies of the same `SyncId` run one after the other within a process; the lock does not reach other
  processes.
- `DryRun` applies to `SyncAsync` and `ApplySyncAsync`: they return the plan, with every step `NotRun` (or
  `Withheld`), without changing anything or saving the baseline (`ApplySyncAsync` still runs its checks
  on the plan first). `PlanSyncAsync` never changes anything.

### Deletion safety

Deletions are withheld, each with a `WithheldReason`, when:

- they would exceed `MaxDeletes` or `MaxDeletePercent` (counted over files; `NaN` is refused);
- a side is unexpectedly empty, as when a drive is not mounted or a root is wrong (unless
  `AllowEmptySide` is set);
- any other step failed or was `Stale` in the same run.

The first two are decided when planning and are listed in `plan.Warnings` too; all of them appear in
`report.Withheld`.

A listing that fails part-way fails the plan. Folders are never deleted recursively: their files are
deleted one by one, each checked at apply time, and then the emptied folders. A folder that gained items
after the plan, or holds items the filters left out, is kept, and keeps its baseline entry. A folder that
holds nothing but the library's own staging (`.cl-storage-*` files) older than 24 hours, left by a
crashed transfer, is deleted: that staging is removed first. Fresher staging, which a transfer may still
be writing, a backup of a replaced file, staging without a modification time, and more than 1,000 staging
entries keep the folder. A path the source left out (a link, a hidden
item, anything below an excluded folder) is never deleted from the destination, and nothing is written
onto or through what the destination left out (a hidden item there, a skipped link, anything below a
skipped link to a folder).

### Copies and comparison

- **Copies.** Each copy is a staged write that is conditional on the planned versions (create-new, or
  replace only that version), pinned to the planned source, and optionally verified (`Verify`, which
  confirms the destination after the promote and records the SHA-256 in the baseline). A source without an
  ETag or version (FTP, SFTP) is checked again after streaming, by size and time. A `KeepBoth` rename is
  pinned to the planned version too; on FTP and SFTP, which cannot pin, size and time are checked just
  before an atomic server-side rename. A copy that committed is recorded in the baseline even when a cancel
  lands right after it. A failed copy starts again from the beginning on its next attempt.
- **Missing root.** A destination root that does not exist yet is created by the plan's first step.
- **Timestamps.** Copied files keep the source's modification time (`PreserveTimestamps`, on by default).
  On object stores that cannot set times, the time is kept in `cl-mtime` metadata and used by later
  comparisons; a kept time without an offset is read as UTC. Listings often omit that metadata, so a pair
  whose sizes agree but whose listed times differ costs one info request on such a store.
- **What is compared.** Size and time, by default, with a two-second tolerance. `CompareBy.Checksum`
  uses the server's digest where there is one.
- **Hashing.** Without a server digest, files are hashed in parallel (`HashConcurrency`) within
  `MaxHashedFiles`/`MaxHashedBytes`, taken for both sides of a pair at once; a file of unknown size is not
  hashed under a byte budget. Beyond the budget a difference is `Undecidable`, as is a pair where one file
  could not be read; a connection failure still fails the comparison. One-way sync leaves an
  `Undecidable` pair alone, with a warning, while size and time agree. `ChecksumAlgorithm` is a
  preference: each side uses a digest its server keeps (MD5 or SHA-256) where it can, so only the other
  side is downloaded.
- **Filters.** `Include`/`Exclude` globs, where `**` spans folders and `*` and `?` stay within one name,
  match the path relative to the compared folder without regard to case, and apply to both sides and to
  the baseline. `Include` selects files; folders are always walked. Excluding a folder excludes everything inside it, as in `.gitignore`
  (`Exclude = ["**/node_modules"]`). `IncludeHidden = false` leaves out hidden folders with their
  contents. A path excluded, hidden, or under a file/folder clash keeps its baseline entry, so lifting the
  filter later does not bring back a deletion made meanwhile. Entries below a folder the filters (or the
  hidden rule) leave out are dropped once neither side lists that folder any more: there is no deletion
  left to protect.
- **Case.** Names that differ only by case are refused on a case-insensitive side
  (`CaseInsensitivePaths`, or `CaseInsensitive`), instead of letting the last copy win; one such
  collision fails the whole comparison with `storage.conflict`. Local connections decide
  `CaseInsensitivePaths` when the connection is created, by probing up to 64 entries of their root for one
  whose name has ASCII letters (swapping their case; names without ASCII letters are skipped, since file
  systems fold other letters by their own tables), and assume the folders below it behave the same. A root
  with no such entry (a new, empty folder) or one that cannot be read gets the platform's default:
  insensitive on Windows and macOS, sensitive elsewhere. The spelling a side gives a folder comes from every folder it lists, an empty one too.
- **Links.** `LinkHandling` skips links by default, together with anything listed below a link to a
  folder; `Reject` fails the comparison with `storage.unsupported`, and `Recreate` is refused. With `Follow`, a link is filtered and compared as what it leads to: a link to a file as that
  file, a link to a folder as a folder holding its target's contents (links inside followed the same way,
  up to 8 deep). A link that leads outside the connection, is broken, or leads back into a folder being
  listed (a cycle) is left out like a skipped one. A copy of an item reached through a link reads its
  content where the link leads (`StorageSyncAction.ReadPath`), and a copy into a followed folder lands in
  its target. A sync never deletes or replaces anything through a followed link: a delete of a link or of
  anything below a followed folder link, and a copy or rename onto a followed file link, are taken out of
  the plan with a warning, and the path keeps its baseline entry.
- **Size cap.** `MaxItems` (1,000,000 by default) caps what one side lists, items left out included; a
  larger tree fails the plan with `storage.too_large`.
- **Out-of-root items.** An item a provider lists outside the compared folder fails the comparison
  (`storage.provider_error`) instead of disappearing; a root spelled in the server's own case is accepted.

### Runs

Transient failures are retried per step (`ItemRetries`, 2 by default, after 200 ms and then twice as long
each time). `ContinueOnError = false` stops at the first step that fails or is `Stale`; steps it stopped are `NotRun`, while a step that failed on its own as the run was stopping is
reported as it ended. Read each step's outcome from `report.Results`. A copy's baseline entry records
the version it wrote, read back (retried a few times); when that read keeps failing, the planned identity
is recorded only if it has a time, otherwise nothing is recorded and the next run compares the two sides
afresh. A provider or
state store that throws fails its step or is reported in `report.BaselineError`; it never loses the
report.

**Cancelling.** Once `ApplySyncAsync` or `SyncAsync` has started applying, cancelling does not throw
`OperationCanceledException` (4.8.93 did): the result is a success whose `report.Cancelled` is `true`.
`Results` says what was applied, and steps not started are `NotRun`. For a two-way sync with a baseline,
the baseline is still saved: completed steps are recorded and every other path keeps its previous entry.
A successful result therefore does not mean the sync finished; check `report.Cancelled`. Cancelling while
planning (`PlanSyncAsync`, or the planning part of `SyncAsync`), while waiting for another apply of the
same `SyncId`, or while the baseline is read before the first step, still throws
`OperationCanceledException`.

### The sync types

- **Entry points.** `StorageLibrary.CompareAsync`, `PlanSyncAsync`, `ApplySyncAsync`, and `SyncAsync` take
  connection ids; the static `StorageSync` class has the same four methods over any two `IStorageService`
  instances, and `StorageCompare.CompareAsync` is the comparison alone.
- **Comparison.** A `StorageDiff` holds one `StorageDiffEntry` per path (`RelativePath` in the source's
  spelling, `DestinationRelativePath` when the destination spells it differently, `Kind` of
  `OnlyInSource`, `OnlyInDestination`, `Different`, or `Same`, the `Source` and `Destination` items, and
  `IsDirectory`), sorted so a folder comes right before its contents; `Identical` says whether everything
  matched. `Reasons` is a `StorageDiffReason` flag set: `Size`, `SourceNewer`, `DestinationNewer`,
  `Checksum`, `Type`, `Undecidable`. `StorageCompareOptions` adds `CompareBy` (a `StorageCompareBy` flag
  set: `Size | Time` by default, `Checksum`), `TimeTolerance` (2 s), `ChecksumAlgorithm` (MD5 by default, a preference),
  `IncludeHidden`, `NamePattern` (a `*`/`?` file-name filter; folders are always walked), `Include`,
  `Exclude`, `HashConcurrency` (4), `MaxHashedFiles`, `MaxHashedBytes`, `CaseInsensitive` (null decides from
  the connections), `LinkHandling` (`Skip`, `Follow`, or `Reject`; `Recreate` is refused), and `MaxItems`.
- **Options.** Besides the ones above, `StorageSyncOptions` has `PropagateDeletes` (on), `PreserveTimestamps`
  (on: copies keep the source's time, in `cl-mtime` metadata where it cannot be set,
  `StorageCompareOptions.ModifiedMetadataKey`), `MaxConcurrency` (4 copies at once), `ItemRetries` (2), and
  `ContinueOnError` (on). `StateStore` and `SyncId` are set together.
- **Plans.** A `StorageSyncPlan` carries `SourceRoot`, `DestinationRoot`, both connection ids, `SyncId`,
  `BaselineGeneration`, `Direction`, `ConflictPolicy`, `CaseInsensitive`, `OptionsDigest`, `Actions`,
  `Unchanged` (files that already matched), `Agreed` (what the baseline will record for paths the plan
  leaves alone or found in agreement), `Warnings`, `CreatedAt`, `Digest`, and `SchemaVersion` (a plan whose
  version is not `StorageSyncPlan.CurrentSchemaVersion` is refused at apply). `Conflicts` lists the conflict
  steps, `IsApprovable` says whether it applies without `ApplyWithConflicts`, `ComputeDigest()` recomputes
  the digest, and `ToJson`/`FromJson` store it.
- **Steps.** A `StorageSyncAction` has the `RelativePath` (and `DestinationRelativePath`), a
  `StorageSyncActionKind` (`CopyToDestination`, `CopyToSource`, `DeleteFromDestination`, `CreateDirectory`,
  `DeleteFromSource`, `CreateDirectoryAtSource`, `RenameAtDestination`, `Conflict`; the numbers are stable),
  the `Reason`, `Bytes`, the planned `Source` and `Destination` versions (`StorageSyncIdentity`: size,
  time and its `ModifiedPrecision`, ETag, version, and SHA-256 when known), the `StorageSyncConflictKind`,
  a `TargetPath` for renames, a `WithheldReason`, and a `ReadPath` for content read through a followed link.
- **Reports.** A `StorageSyncReport` has the `Plan`, one `StorageSyncActionResult` per step (a
  `StorageSyncActionOutcome` of `NotRun`, `Applied`, `Failed`, `Stale`, or `Withheld`, an `Error`, and `Attempts`), `DryRun`,
  `Cancelled`, `BaselineSaved`, and `BaselineError`, with `Failed`, `Stale`, `Withheld`, `Conflicts`,
  `Copied`, and `Deleted` as shortcuts. An applied step can still carry an error when a provider left an
  internal object behind (`leftBehind` details).
- **Baselines.** `IStorageSyncStateStore` has `LoadAsync(syncId)` (null before the first run) and
  `SaveAsync(syncId, baseline, expectedGeneration)`, which returns false when another run saved in between.
  A `StorageSyncBaseline` is a `Generation` (one more per saved run) and per-path
  `StorageSyncBaselineEntry` values (the source's and the destination's `StorageSyncIdentity`, and whether it
  was a folder). `InMemoryStorageSyncStateStore` keeps them for the life of the process; implement the
  interface over a database to keep them between runs.

## Watching for changes

```csharp
await foreach (var change in files.WatchAsync("incoming", cancellationToken: stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

Local connections use native file-system notifications, including renames; this also works for
connections from `GetStorage()`. If notifications arrive faster than they can be buffered, a
`StorageChangeKind.Overflow` change for the watched directory is reported, and the caller should list
it again. If native watching stops for good (the folder was removed, a network share dropped), `Overflow`
is reported and watching continues by polling.

Every other provider is polled, and so is a local connection with `ForcePolling = true`. The directory
(and, with `Recursive`, on by default, everything below it) is listed every
`StorageWatchOptions.PollInterval` (30 s by default) and compared with the previous listing: new and
vanished paths are `Created` and `Deleted`, and a file whose size, time, or ETag changed is `Changed`
(folders report only `Created` and `Deleted`), so a rename appears as a delete plus a create. A watched
folder that does not exist counts as empty. A failed poll is retried on the next interval rather than reported as deletions, and reported to
`StorageWatchOptions.PollFailed`; a first listing that fails is retried rather than taken as an empty
folder, and a listing cut short part-way (a folder vanishing mid-poll) is retried too. The library's own
staging items never appear; a rename to or from one is reported as a delete or a create. If native
watching cannot even start, `Overflow` is reported and the directory is polled.

Each `StorageChange` carries the `Kind`, the `Path`, the `OldPath` of a native rename, the `ItemType`
when known, and `ObservedAt`. A provider with native notifications implements `IStorageWatchService` and
declares `StorageFeature.ChangeNotifications`. An invalid `PollInterval` (not positive) or
`FullRescanEvery` (below 1) throws `ArgumentOutOfRangeException`.

Polling a large remote tree is cheaper with `Incremental = true` (recursive watches only):

- After the first listing, a poll lists only the root and the folders whose modification time changed
  (entries were added, removed, or renamed in them). The previous listing is reused for the rest.
- Every `FullRescanEvery` polls (10 by default), the whole tree is listed again. This catches edits to
  existing files, and changes deep inside folders whose own time did not change. FTP servers that report
  folder times to the minute can hide a change made in the same minute until then.
- Where folders have no times (object stores), listing folder by folder would cost more than one
  recursive listing, so every poll lists everything there.

```csharp
var options = new StorageWatchOptions { Recursive = true, Incremental = true, FullRescanEvery = 20 };
await foreach (var change in partner.WatchAsync("outbox", options, stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

## Links in transfers

Relayed copies and moves (across connections, or directory copies) meet links as provider-specific
items. `StorageTransferOptions.LinkHandling` decides what happens:

| Mode | Behavior |
|---|---|
| `Reject` (default) | fail with `storage.unsupported` and roll back |
| `Skip` | leave links out |
| `Follow` | copy the target file's content; links to directories are refused, so loops cannot occur |
| `Recreate` | create an equivalent link; targets inside the copied tree point into the copy |

`Recreate` needs `ReadLinks` on the source and `CreateLinks` on the destination. A link whose target lies
outside the source root, or, in a directory transfer, outside the transferred directory, fails with
`storage.unsupported`. SFTP cannot be a
`Recreate` source, because SSH.NET cannot read link targets. Compare and sync have their own
`StorageCompareOptions.LinkHandling`, whose `Follow` does follow links to folders (see
[Copies and comparison](#copies-and-comparison)).

On Windows only symbolic links and junctions are links. Other reparse points, such as OneDrive
Files-On-Demand placeholders, deduplicated files, and app execution aliases, are ordinary files and
folders. A recursive local listing never descends into a link to a folder.
