# CodeLogic.Storage

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Storage)](https://www.nuget.org/packages/CodeLogic.Storage)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

Provider-neutral, root-scoped storage for CodeLogic 5 and .NET 11. One API mounts local/UNC,
S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift
connections, and adds what a desktop file-transfer client needs on top: verified and resumable
transfers between any two connections, a durable background queue, three-way sync with approved plans,
change watching, and connection diagnostics.

This README is an overview. The full guide is on the documentation site:

| Page | What it covers |
|---|---|
| [Overview](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/index.html) | loading, the mount model, configuration, everyday operations, capabilities |
| [Connections](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/connections.html) | per-provider options, TLS and host keys, proxies, session pools, runtime connections, testing and diagnostics |
| [Transfers & Sync](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/transfers.html) | transfer reports, guaranteed transfers, streamed writes, conflict policies, resume, the transfer queue, compare and sync, watching |
| [Files & Attributes](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/files.html) | permissions, ownership, timestamps, links, checksums, metadata, tags, versions, signed URLs, raw commands, free space |
| [Errors & Events](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/errors-events.html) | the `storage.*` error codes, TLS failure reasons, and every published event |

## Install and load

```bash
dotnet add package CodeLogic.Storage
```

```csharp
using CL.Storage;
using CodeLogic;

var init = await CodeLogic.CodeLogic.InitializeAsync();
if (init.ShouldExit) return;
await Libraries.LoadAsync<StorageLibrary>();
await CodeLogic.CodeLogic.ConfigureAsync();   // the class CodeLogic.CodeLogic, not the namespace
await CodeLogic.CodeLogic.StartAsync();

var storage = Libraries.Get<StorageLibrary>();
IStorageService media = storage.GetStorage("media");
```

`storage` (the `StorageLibrary`) owns connections, cross-connection transfers, the queue, sync, and
diagnostics; an `IStorageService` is one connection's file operations. Every connection mounts exactly one
local root, bucket or container prefix, or remote directory. Paths are relative, slash-separated paths
below that mount: a leading `/` is ignored, and `..` segments, control characters (NUL, CR, LF and the rest of
U+0000 to U+001F, U+007F), or a local path that resolves outside the root fail with `storage.invalid_path`. A
local `RootPath` must be absolute.

Applications that manage connections themselves can skip configuration files entirely with
`new StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true })` and
`AddOrUpdateConnectionAsync`.

## Providers and configuration

Provider connections live in typed, case-insensitive `Connections` dictionaries, one configuration
section per provider. Connection IDs must be unique across all sections.

| Section | Connection model | Mounted resource |
|---|---|---|
| `storage.local` | `LocalConnectionConfig` | local directory or UNC share |
| `storage.s3` | `S3ConnectionConfig` | bucket plus optional prefix |
| `storage.ftp` | `FtpConnectionConfig` | FTP/FTPS directory |
| `storage.sftp` | `SftpConnectionConfig` | SFTP directory |
| `storage.webdav` | `WebDavConnectionConfig` | WebDAV endpoint plus root |
| `storage.azure` | `AzureBlobConnectionConfig` | Blob container plus prefix |
| `storage.gcs` | `GoogleCloudConnectionConfig` | GCS bucket plus prefix |
| `storage.swift` | `SwiftConnectionConfig` | Swift container plus prefix |

The `storage` section holds the master switch, `DefaultConnection`, the buffered-download limit, the
health-check timeout, and library-wide speed limits. Example `config.storage.s3.json`:

```json
{
  "connections": {
    "media": {
      "enabled": true,
      "bucket": "company-media",
      "prefix": "production",
      "region": "eu-north-1",
      "authenticationMode": "DefaultCredentialChain"
    },
    "minio": {
      "enabled": true,
      "bucket": "documents",
      "serviceUrl": "https://minio.example.com",
      "forcePathStyle": true,
      "authenticationMode": "StaticCredentials",
      "accessKey": "...",
      "secretKey": "..."
    }
  }
}
```

Security is on by default: clear-text custom endpoints need `AllowInsecureHttp`, SFTP needs a trusted host
key (fingerprints or `known_hosts`; `AutoAcceptHostKey` is for development only), FTPS and WebDAV validate
certificates and accept certificate or public-key pins but have no accept-any switch, and raw FTP/SSH
commands need `AllowRawCommands`. FTP and SFTP keep pooled sessions, shared by registrations with identical
settings; FTP, SFTP, and WebDAV retry transient failures. Every remote provider can use an HTTP, SOCKS5, or
SOCKS4 proxy. See [Connections](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/connections.html).

## Everyday operations

```csharp
await using var source = File.OpenRead("photo.jpg");
Result<StorageItem> uploaded = await media.UploadAsync(
    "photos/photo.jpg",
    source,
    new StorageUploadOptions
    {
        Overwrite = false,
        ContentType = "image/jpeg",
        Metadata = new Dictionary<string, string> { ["owner"] = "42" }
    });

Result<StoragePage> page = await media.ListAsync("photos", new StorageListOptions
{
    Recursive = true,
    PageSize = 250
});

Result<byte[]> range = await media.DownloadBytesAsync(
    "photos/photo.jpg",
    new StorageDownloadOptions { Offset = 1024, Length = 4096 });

Result deleted = await media.DeleteAsync(
    "photos",
    new StorageDeleteOptions { Recursive = true });
```

The common contract covers info and exists, paged (and recursive) listings, directories, streaming and
bounded byte uploads and downloads, ranges, delete, copy, move, and cancellation. `EnumeratePagesAsync` and
`EnumerateItemsAsync` walk continuation tokens lazily, and batch helpers run bounded, order-preserving
info, delete, copy, and move. `StorageServiceExtensions` adds file, text, JSON, progress, and checksum
helpers. Caller upload streams stay open; a returned download stream owns its provider response and must
be disposed.

Capabilities are granular flags plus provider limits; check them rather than inferring behavior from a
provider name. Optional features (metadata, tags, versions, signed URLs, permissions, links, raw commands,
free space) return `storage.unsupported` where a connection lacks them:

```csharp
if (media.Capabilities.Supports(StorageFeature.MetadataWrite))
    await media.SetMetadataAsync("photo.jpg", new Dictionary<string, string> { ["reviewed"] = "yes" });
```

## Transfers

`StorageLibrary.CopyAsync` and `MoveAsync` copy or move a file or a whole tree between any two
connections, through a bounded relay into a staging object that is promoted only when complete. They
return a `StorageTransferReport` rather than throwing: `Completed`, `Skipped`, `Failed` (nothing
committed), `NeedsReconciliation` (a mixed state), or `Cancelled`, plus exactly what was left behind and a
`ResumeToken` when the transfer can continue.

```csharp
StorageTransferReport report = await storage.CopyAsync("sftp", "in/report.pdf", "s3", "archive/report.pdf", new StorageTransferOptions
{
    DestinationCondition = new StorageMutationCondition { ExpectedETag = seenDestination.ETag }, // replace only this version
    ExpectedSourceETag = plannedSource.ETag,        // or SourceVersionId, to read an exact version
    ExpectedSourceLength = plannedSource.Size,
    Verify = true                                   // SHA-256 during the copy, destination confirmed
});
Console.WriteLine($"{report.Outcome}: {report.WrittenPath} ({report.ConditionEnforcement}, verified by {report.VerifiedBy})");
```

- A single file's committed destination is never rolled back; what went wrong afterwards is reported.
  A move deletes its source only while it is still the version that was copied.
- `ConditionEnforcement` says whether the destination condition was enforced by the server in the
  committing request (`Atomic`) or checked just before it (`CheckedBeforeCommit`);
  `GetConditionEnforcementAsync` asks a connection in advance.
- `ConflictPolicy` mirrors FileZilla's "target exists" choices (`Skip`, `OverwriteIfNewer`, `Rename`, …) and
  `Resume` continues staged bytes of the same source, across processes with a resume token.
- `OpenWriteAsync` returns a `StorageWriteStream` for push-style producers, committed with `CommitAsync`.
- Progress reports carry speed and time remaining; speed limits apply per connection and library-wide.

See [Transfers & Sync](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/transfers.html) for the
per-provider guarantees, resume rules, and streamed writes.

## Transfer queue

`OpenTransferQueueAsync` runs transfers in the background, like FileZilla's queue, with global and
per-connection limits, priorities, pause and resume, and automatic retries of transient failures:

```csharp
var opened = await storage.OpenTransferQueueAsync(new StorageTransferQueueOptions
{
    MaxConcurrentTransfers = 4,
    MaxTransfersPerConnection = 2,
    Store = myDurableStore          // optional: jobs survive restarts and can be shared between processes
});
await using var queue = opened.Value!;
queue.ProgressChanged += job => Console.WriteLine($"{job.Destination}: {job.Progress?.BytesTransferred:N0} B");

await queue.EnqueueCopyAsync("s3", "reports/q3.pdf", "sftp", "outbox/q3.pdf",
    new StorageTransferOptions { Verify = true, ConflictPolicy = StorageConflictPolicy.Resume },
    priority: 10, jobId: "q3-report");   // same id + same work = same job
await queue.WaitForIdleAsync();
```

Jobs are data (`StorageTransferJobSpec`) kept in an `IStorageTransferJobStore`. Revisions and leases with
fencing tokens keep two processes from running one job; after a restart a job goes back to the queue when
its destination was never touched, and otherwise waits as `Interrupted` for a person. Trust and credential
failures block a job, and a mixed state marks it `NeedsReconciliation`.

## Compare and sync

```csharp
var options = new StorageSyncOptions
{
    Direction = StorageSyncDirection.TwoWay,
    StateStore = baselines, SyncId = "site",          // a baseline makes two-way three-way
    MaxDeletes = 100, MaxDeletePercent = 10,
    Compare = new StorageCompareOptions { Exclude = ["**/*.tmp", "cache/**"] }
};
var plan = (await storage.PlanSyncAsync("sftp", "site", "s3", "backup/site", options)).Value!;
// show plan.Actions, plan.Conflicts, and plan.Warnings; approve plan.Digest
var synced = await storage.ApplySyncAsync("sftp", "site", "s3", "backup/site", plan, approvedDigest, options);
```

`CompareAsync` diffs two trees by size and time or by checksum. Sync runs `Update`, `Mirror`, or `TwoWay`;
with a baseline, two-way is a three-way sync that carries edits and deletions to the other side and
reports changes on both sides as conflicts (`Block`, `KeepBoth`, or `NewerWins`). A plan is approved by its
digest and applied only as planned: each step re-checks its items, deletions are withheld when they exceed
the limits or a side looks unexpectedly empty, and folders are never deleted recursively.

## Watching for changes

```csharp
await foreach (var change in media.WatchAsync("incoming", cancellationToken: stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

Local connections use native notifications; other providers are polled (optionally incrementally).

## Connections at runtime

```csharp
await storage.AddOrUpdateConnectionAsync("backup", new SftpConnectionConfig
{
    Host = "sftp.example.com",
    Username = "backup",
    AuthenticationMode = SftpAuthenticationMode.PrivateKey,
    PrivateKeyPath = @"C:\keys\backup_ed25519",
    HostKeyFingerprints = ["SHA256:..."]
}, persist: false);
```

`TestConnectionAsync` tries settings before they are saved and reports the certificate or host key a
server presented, ready to pin; `GetConnectionDiagnosticsAsync` describes a live connection (negotiated
TLS or SSH algorithms, server software and features, session pool counters). Native SDK clients remain
available through `GetNativeClient` and `OpenNativeConnectionAsync`.

### Editing the config while running

With CodeLogic 4.9+, the `storage*` sections also apply live when they are changed outside the library,
through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file watcher. Only
the connections that changed are touched: a changed or added connection is built and health-checked,
then swapped in while the one it replaces drains its in-flight operations; removed or disabled
connections are retired the same way; every other connection keeps running. A replacement that fails
its health check leaves the current backend in service and the change is reported as not applied;
`Libraries.ReloadConfigAsync` retries it once the cause is fixed, even if the file did not change. The
default connection cannot be removed or disabled this way, and turning the library on or off still
needs a restart. Library-wide speed limits apply to every connection at once; `MaxBufferedDownloadBytes`
applies to connections created or changed afterwards.

## Errors and events

Expected failures come back as a failed `Result` with a stable `storage.*` code, such as
`storage.not_found`, `storage.conflict`, `storage.authentication_failed`, `storage.tls_failure` (with a
`tlsReason`), or `storage.unsupported`; `StorageErrorInfo.IsTransient` says whether retrying may help.
Connection calls throw `OperationCanceledException` on a cancel, while `StorageLibrary.CopyAsync`/`MoveAsync`,
the queue, and a sync that has started applying report it. Writes, deletes, transfers, health changes,
session events, failed operations, and queue job changes are published on the CodeLogic event bus. See
[Errors & Events](https://zyntal-com.github.io/CodeLogic.Libs/libs/storage/errors-events.html).

Upgrading from 4.8.93 or from the legacy S3-only package? See [MIGRATION.md](MIGRATION.md) and
[CHANGELOG.md](CHANGELOG.md).

## Requirements

- CodeLogic 5.0+
- .NET 11

MIT license.
