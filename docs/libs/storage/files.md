# Files & Attributes

Local path containment and conversion back to storage paths both follow the volume's probed case rules. A mounted filesystem can differ from its host OS; a case-equivalent root still maps to the same relative path and preserves descendant spelling.

> Everything about a file beyond its bytes: permissions, ownership, timestamps, links, checksums,
> metadata, tags, versions, and signed URLs — plus raw server commands and free space.

Every method here is an extension on `IStorageService` and returns `storage.unsupported` when the
connection cannot do it. Check `Capabilities` first when you need to know in advance.

## What a listing tells you

`StorageItem` carries, wherever the provider reports them:

| Property | Source |
|---|---|
| `UnixMode`, `Permissions` (`rwxr-xr-x`) | FTP listings, SFTP, local (Unix) |
| `Owner`, `Group` | FTP listings |
| `OwnerId`, `GroupId` | SFTP |
| `LinkTarget` | FTP listings, local |
| `Created`, `LastAccessed` | local, FTP listings (`Created`), SFTP (`LastAccessed`) |
| `IsHidden` | dot-files; local hidden attribute |
| `ETag`, `VersionId`, `ContentType`, metadata | object stores, WebDAV; local files get a weak ETag (`W/"…"`) from their write time, creation time, and size |
| `ModifiedPrecision` | how coarse `LastModified` is when a listing gives only minutes or days (FTP `LIST` without `MLSD`); null when exact |
| `Sha256` | only on an item returned by a verified upload or streamed write |

## Permissions, ownership, timestamps, and links

```csharp
await files.SetPermissionsAsync("reports/q3.csv", "640");
await files.SetPermissionsRecursiveAsync("public", fileMode: 0x1A4, directoryMode: 0x1ED); // 0644 / 0755
await files.SetOwnerAsync("reports/q3.csv", ownerId: 1001, groupId: 1001);
await files.SetTimestampsAsync("reports/q3.csv", lastModified: sourceTime);
await files.CreateLinkAsync("current", "releases/v42");
var link = await files.ReadLinkAsync("current");
```

| | Local | FTP | SFTP |
|---|---|---|---|
| Permissions (`Permissions`) | Unix only | `SITE CHMOD` | yes, incl. setuid/setgid/sticky |
| Owner/group (`Ownership`) | no | no | numeric IDs |
| Timestamps (`SetTimestamps`) | modified + accessed | modified only (`MFMT` or `MDTM`) | modified + accessed |
| Create link (`CreateLinks`) | yes (relative) | no | yes |
| Read link (`ReadLinks`) | yes | from listings | no (SSH.NET lacks `readlink`) |

Link targets must stay inside the mounted root. On Windows, creating local links needs Developer Mode
or the symbolic-link privilege.

A local connection reports a link's target (`StorageItem.LinkTarget` in listings, `RawTarget` and `StoragePath` from
`ReadLinkAsync`) only when the target lies inside the root. Otherwise the item is still a link, but `LinkTarget` is
null, `RawTarget` empty and `StoragePath` null: a link may not disclose where on the machine it points. This does not
depend on `FollowLinks`, which decides whether a path may pass *through* a link; reading a link does not follow it.
Links are reported in listings but never descended into; following one is the caller's choice (`LinkHandling` on
transfers and syncs), and a link whose target lies outside the root cannot be followed.

## Server-side checksums

`ComputeChecksumAsync` and `VerifyChecksumAsync` ask the server for a stored digest first and only
download the content when there is none; `StorageChecksum.Source` says which happened.
`GetServerChecksumAsync` returns only the server's value, and `StorageChecksumMode.ComputeOnly`
forces a download.

```csharp
Result<StorageChecksumVerification> verified = await files.VerifyChecksumAsync(
    "large.bin", expectedSha256Hex);
```

| Provider | Server digest |
|---|---|
| S3 | MD5 from single-part ETags of objects without SSE-KMS or SSE-C; SHA-256 when stored with the object |
| Azure Blob | MD5 (`Content-MD5`) |
| Google Cloud Storage | MD5 of non-composite objects |
| Swift | MD5 ETag, except segmented large objects |
| FTP | `HASH`/`XMD5`/`XSHA256`/`XSHA512` when the server offers them |
| SFTP, WebDAV, Local | none (always computed) |

Computed checksums stream through a pooled buffer using MD5, SHA-256, SHA-384, or SHA-512. MD5 is
there for interoperability; prefer SHA-256 or stronger for security-sensitive checks.

## Metadata, tags, versions, and signed URLs

| Provider | Directories | Metadata | Tags | Conditional create/update/delete | Versions | Signed URLs |
|---|---|---|---|---|---|---|
| Local / UNC | physical | no | no | create | no | no |
| S3-compatible | virtual | read/write | read/write | yes/yes/yes (per server, see below) | read/list/delete | read/write |
| FTP / FTPS | physical | no | no | no | no | no |
| SFTP | physical | no | no | no | no | no |
| WebDAV | physical | discovered properties, read-only | no | create | no | no |
| Azure Blob | virtual | read/write | read/write | yes/yes/yes | read/list/delete | SAS when credentials permit |
| Google Cloud Storage | virtual | read/write | no | yes/yes/yes | read/list/delete | when signing credentials permit |
| OpenStack Swift | virtual | read/write | no | create (update/delete checked just before) | no list or delete; downloads and server-side copies can read a version id | no |

The optional contracts behind these are `IStorageMetadataService` (merge or replace user metadata,
optionally matching an ETag or version), `IStorageTagService` (up to ten portable tags),
`IStorageSignedUrlService` (read or write URLs with bounded expiry), and `IStorageVersionService` (version
pages and exact-version deletes). The extension methods below, with `EnumerateVersionPagesAsync`, call them
and return `storage.unsupported` where a connection has none. A relayed transfer that pins an older
`SourceVersionId` needs `IStorageVersionService` on the source.

```csharp
var metadata = await files.GetMetadataAsync("asset.bin");

await files.SetTagsAsync(
    "asset.bin",
    new Dictionary<string, string> { ["tier"] = "archive" },
    new StorageTagUpdateOptions { Mode = StorageTagUpdateMode.Merge });

var signed = await files.CreateSignedUrlAsync("asset.bin", new StorageSignedUrlOptions
{
    Method = StorageSignedUrlMethod.Read,
    ExpiresIn = TimeSpan.FromMinutes(10)
});

var versions = await files.ListVersionsAsync("asset.bin");
await files.DeleteVersionAsync("asset.bin", "provider-version-id");
```

Treat signed URLs as credentials and never log them.

`StorageMutationCondition` applies ETag/version guards to uploads and deletes:

- **Uploads** with an ETag or version `Condition` are atomic on Azure Blob, Google Cloud, and S3 servers
  that enforce `If-Match` on both `PutObject` and `CopyObject` (AWS; under `ConditionalRequests = Auto` this
  is probed). Everywhere else, MinIO included once the probe has run, the content is staged and the
  condition checked immediately before the staged file replaces the destination. An upload returns the
  stored item, not how its condition was enforced; ask the connection with
  `GetConditionEnforcementAsync(MatchVersion)` (see [Guaranteed transfers](transfers.md#guaranteed-transfers);
  on MinIO it answers `CheckedBeforeCommit`), or check `StorageFeature.ConditionalUpdate`, which is what
  decides the staging.
- **Create-only uploads** (`Overwrite = false`, not otherwise staged) are atomic on Local, WebDAV, S3
  servers that enforce `If-None-Match` on `PutObject` (AWS and MinIO), Azure Blob, Google Cloud, and Swift.
  FTP and SFTP have no atomic create-if-absent and do not declare `StorageFeature.ConditionalCreate`; a
  caller that must not race should check that flag (MinIO's is cleared after the probe, because its copies
  do not enforce it) or ask `GetConditionEnforcementAsync(StorageConditionKind.CreateOnly)`. Local
  overwrites are staged too: the new content is written to a
  temporary file in the same folder and moved over the target, so a reader never sees a half-written file.
- **Deletes** are atomic on Azure Blob, Google Cloud, and S3 servers that enforce `If-Match` on
  `DeleteObject`. Swift, and S3 servers that ignore it (MinIO), check the condition immediately before the
  delete. Local, FTP, SFTP, and WebDAV refuse a conditional delete with `storage.unsupported`.
- Swift ignores `If-Match` on writes, so it declares only `ConditionalCreate`.
- Local ETags are weak (`W/"…"`): a different one proves the file changed, but a matching one does not
  prove it did not (two versions written within the file system's clock resolution can share it).
- A WebDAV upload never replaces a folder: an existing collection at the destination is `storage.conflict`.

```csharp
await files.UploadAsync("settings.json", replacement, new StorageUploadOptions
{
    Condition = new StorageMutationCondition { ExpectedETag = current.Value!.ETag }
});
```

## Raw commands

With `AllowRawCommands: true` on an FTP or SFTP connection, `ExecuteCommandAsync` sends a raw FTP
command (`SITE ...`, `SYST`) or runs an SSH shell command as the connection's account:

```csharp
var reply = await files.ExecuteCommandAsync("SITE CHMOD 640 reports/q3.csv");
if (reply.IsSuccess && !reply.Value!.Succeeded)
    Console.WriteLine($"Server said {reply.Value.Code}: {reply.Value.Output}");
```

It is off by default because commands are not confined to the connection's `Root` (an SSH command starts
in the account's home folder). A rejected command or non-zero exit comes back as a result with
`Succeeded = false`, not as an error; an empty or multi-line FTP command is `storage.invalid_content`.
Servers that allow only SFTP (`ForceCommand internal-sftp`) refuse shell commands.

## Free space

`GetSpaceAsync` reports free and used space: SFTP through `statvfs@openssh.com`, FTP through `AVBL`
where the server implements it (available bytes only; total and used stay null), and local connections
from the volume. WebDAV and object stores return `storage.unsupported`.

```csharp
var space = await files.GetSpaceAsync();
if (space.IsSuccess && space.Value!.AvailableBytes < 10L * 1024 * 1024 * 1024)
    Console.WriteLine("Less than 10 GiB left.");
```
