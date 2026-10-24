# CodeLogic.StorageS3

> **Legacy package:** use [`CodeLogic.Storage`](../CL.Storage/) for new development. The replacement
> adds mounted S3 connections, seven additional providers, bounded multipart uploads, version APIs,
> and rollback-safe cross-provider transfers.

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.StorageS3)](https://www.nuget.org/packages/CodeLogic.StorageS3)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> S3-compatible object storage for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — one API across Amazon S3, MinIO, and Cloudflare R2.

Built on [AWSSDK.S3](https://www.nuget.org/packages/AWSSDK.S3). Configure one or more named connections, then upload, download, list, copy, and presign objects through a single `S3StorageService`. Every operation returns the framework `Result<T>` (existence checks return a plain `bool`), so failures surface as `Error` values instead of exceptions.

## Install

```bash
dotnet add package CodeLogic.StorageS3
```

## Quick start

```csharp
await Libraries.LoadAsync<StorageS3Library>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var storage = Libraries.Get<StorageS3Library>();
var s3 = storage.DefaultService;            // S3StorageService for the "Default" connection

// Upload (byte array or stream)
using var file = File.OpenRead("photo.webp");
Result<S3ObjectInfo> put = await s3.PutObjectAsync(
    "my-bucket", "uploads/photo.webp", file,
    new UploadOptions { ContentType = "image/webp" });

if (put.IsSuccess)
    Console.WriteLine(put.Value!.PublicUrl);   // populated when PublicUrl is configured

// Download
Result<byte[]> data = await s3.GetObjectAsync("my-bucket", "uploads/photo.webp");
if (data.IsSuccess)
    File.WriteAllBytes("photo.webp", data.Value!);

// List with a prefix
Result<ListObjectsResult> list = await s3.ListObjectsAsync("my-bucket", prefix: "uploads/");
foreach (var obj in list.Value!.Objects)
    Console.WriteLine($"{obj.Key} ({obj.Size} bytes)");

// Delete
await s3.DeleteObjectAsync("my-bucket", "uploads/photo.webp");
```

Existence checks (`BucketExistsAsync`, `ObjectExistsAsync`) return a plain `Task<bool>` — no `Result` wrapper.

## Features

- **Objects** — upload and download by byte array or stream, copy, delete, metadata-only lookup, and existence checks.
- **Range & version downloads** — `DownloadOptions` for byte-range reads and `VersionId` retrieval.
- **Upload options** — `UploadOptions` for content type, cache-control, content-disposition, storage class, public-read ACL, and custom metadata.
- **Buckets** — create, delete, list, and exists.
- **Listing** — prefix filtering, pagination via continuation tokens, and common-prefix (virtual folder) detection.
- **Multiple connections** — named connection IDs for multi-bucket or multi-provider setups; reach any via `storage.GetService("id")`.
- **Presigned URLs** — temporary GET access links with a configurable expiry.
- **Public URLs** — optional public base URL auto-populated on `S3ObjectInfo.PublicUrl`.
- **Events** — `ObjectUploadedEvent`, `ObjectDeletedEvent`, and `BucketCreatedEvent` on the CodeLogic event bus.
- **Health check** — tests every configured connection inside its `defaultBucket` when available, so bucket-scoped credentials are sufficient (`Healthy` / `Degraded` / `Unhealthy`).

## Configuration

Auto-generated on first run as `config.storages3.json` (section `storages3`):

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "your-access-key",
      "secretKey": "your-secret-key",
      "serviceUrl": "https://s3.amazonaws.com",
      "publicUrl": "https://cdn.example.com",
      "region": "us-east-1",
      "defaultBucket": "my-bucket",
      "forcePathStyle": true,
      "useHttps": true,
      "timeoutSeconds": 30,
      "maxRetries": 3,
      "disablePayloadSigning": false
    }
  ]
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Master switch; when `false` no connections are registered and health reports *disabled*. |
| `connections` | `[]` | One or more connection blocks. At least one is required when enabled; each `connectionId` must be unique. |
| `connectionId` | `"Default"` | Identifier used to look up the connection via `GetService`. |
| `accessKey` | `""` | Access key ID (secret). |
| `secretKey` | `""` | Secret access key (secret). |
| `serviceUrl` | `""` | Endpoint URL. AWS `https://s3.amazonaws.com`; MinIO `http://localhost:9000`; R2 `https://<account>.r2.cloudflarestorage.com`. |
| `publicUrl` | `""` | Optional public base URL for object links; leave blank for private buckets. |
| `region` | `"us-east-1"` | Request-signing region. Use `"auto"` for Cloudflare R2. |
| `defaultBucket` | `""` | Bucket used when code doesn't specify one. |
| `forcePathStyle` | `true` | Path-style addressing; required for MinIO and most non-AWS services. |
| `useHttps` | `true` | Use HTTPS for the connection. |
| `timeoutSeconds` | `30` | HTTP request timeout (1–600). |
| `maxRetries` | `3` | Retry attempts on transient failures (0–20). |
| `disablePayloadSigning` | `false` | Disables streaming payload signing; set `true` for Cloudflare R2. |

Provider tips: **AWS** → `forcePathStyle: false`, `disablePayloadSigning: false`. **MinIO** → `forcePathStyle: true`, `useHttps: false`. **R2** → `disablePayloadSigning: true`, `forcePathStyle: false`.

## Documentation

Full guide: **[CL.StorageS3 documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/storages3.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- AWSSDK.S3 4.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
