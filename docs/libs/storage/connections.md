# Connections

> How to connect to each kind of server: authentication, trust, proxies, session pools and retries,
> runtime connection changes, and checking a connection before and after you save it.

See the [overview](index.md) for the configuration sections and the mount model.

## SFTP: authentication, host keys, and jump hosts

```json
{
  "Host": "sftp.internal",
  "Username": "deploy",
  "AuthenticationMode": "Auto",
  "Password": "...",
  "PrivateKeyPath": "/secrets/id_ed25519",
  "PrivateKeyContent": null,
  "AdditionalPrivateKeyPaths": [],
  "PrivateKeyPassphrase": "...",
  "KnownHostsPath": "/home/app/.ssh/known_hosts",
  "HostKeyFingerprints": [],
  "Ciphers": ["aes256-gcm@openssh.com", "aes256-ctr"],
  "Encoding": "utf-8",
  "BufferSize": 262144,
  "JumpHost": {
    "Host": "bastion.example.com",
    "Username": "jump",
    "PrivateKeyPath": "/secrets/bastion_ed25519",
    "KnownHostsPath": "/home/app/.ssh/known_hosts"
  }
}
```

- **`AuthenticationMode`**: `Password`, `PrivateKey`, `KeyboardInteractive` (answers the password
  prompt), or `Auto`, which offers keys, then password, then keyboard-interactive, and also satisfies
  servers that demand several methods. Keys can be files or inline text (`PrivateKeyContent`) from a
  secret store. SSH agents are not supported by the underlying SSH library.
- **Host keys** are trusted through `HostKeyFingerprints` (`SHA256:...`), an OpenSSH `KnownHostsPath`
  (plain, hashed, wildcard, and `[host]:port` entries), or `AutoAcceptHostKey` for development. A key
  marked `@revoked` in `known_hosts` is refused even with auto-accept. A rejected key reports
  `storage.host_key_rejected` with the presented fingerprint in `Details`.
- **Algorithms**: `KeyExchangeAlgorithms`, `Ciphers`, `MacAlgorithms`, and `HostKeyAlgorithms`
  restrict and order what is offered, for hardening or for old servers. Unknown names fail validation
  and list what is supported.
- **`JumpHost`** tunnels through an SSH bastion. The target's key is still verified against the
  target's settings, and a configured `Proxy` applies to the bastion connection.

## FTP and FTPS

```json
{
  "Host": "ftp.partner.example",
  "EncryptionMode": "Explicit",
  "TrustedPublicKeySha256": ["SHA256:..."],
  "TlsProtocols": ["Tls12", "Tls13"],
  "EncryptDataChannel": true,
  "DataConnectionMode": "AutoPassive",
  "ActivePortMin": 50000,
  "ActivePortMax": 50100,
  "ActiveExternalIp": "203.0.113.7",
  "Encoding": "windows-1252",
  "TransferType": "Binary",
  "ListingParser": "Auto",
  "ServerTimeZone": "Europe/Copenhagen",
  "ReadTimeoutSeconds": 60,
  "SocketKeepAlive": true,
  "LoginCommands": ["SITE UMASK 022"]
}
```

- **Pins**: `TrustedCertificateSha256` pins the whole certificate; `TrustedPublicKeySha256` pins only
  its public key, so it keeps working across renewals that keep the key. Once pins are set, only a pinned
  certificate is accepted, whatever its chain, name, or expiry errors (a self-signed one too), unless
  `RequireValidCertificateChain` is set; an unpinned certificate is refused even with a valid chain.
  Without pins, normal validation applies. `CheckCertificateRevocation` turns on revocation checks and
  `TlsProtocols` limits the versions (`Tls12`, `Tls13`). TLS problems report `storage.tls_failure` with a `tlsReason` detail (see
  [Errors & Events](errors-events.md#tls-failures)).
- **Client certificates** for mutual TLS come from `ClientCertificatePath`, or from
  `ClientCertificateContent` (the PFX bytes, base64 in JSON) when they live in a secret store;
  `ClientCertificatePassword` decrypts either. The certificate is loaded once per session pool (shared by
  registrations with identical settings) and disposed when the pool closes. On Linux the private key is held in memory only. macOS does not support in-memory keys, so .NET
  imports the key into a temporary keychain that it deletes when the certificate is disposed with the
  connection. On Windows SChannel needs a key container: the key goes into a non-persisted container that
  is deleted when the connection is disposed; a process that crashes can leave that container file behind.
  Only when the user key store is unavailable (the account's profile is not loaded, as for some service
  accounts) is the machine key store used instead; its containers are machine-wide, so the machine's
  administrators can read the key while the connection holds it. A wrong password or a damaged file is
  reported as it is and never falls back. A PKCS#12 file without its private key is
  refused when the connection is registered (`AddOrUpdateConnectionAsync` fails with
  `storage.provider_error`).
- **Active mode** behind NAT: `ActivePortMin`/`ActivePortMax` and `ActiveExternalIp`.
- **Encodings** such as `windows-1252`, `iso-8859-1`, `ibm437`, and `shift_jis` are supported for file
  names on older servers.
- **Listings**: `ListingParser` forces a format (`Unix`, `Windows`, `Machine`, …) and `ServerTimeZone`
  converts times from servers that report local time.
- **Timeouts**: `ConnectTimeoutSeconds`, `ReadTimeoutSeconds`, and `DataConnectionTimeoutSeconds`
  fall back to `TimeoutSeconds`.
- **`LoginCommands`** run after every login; a command the server rejects fails the connection so
  misconfiguration surfaces immediately.

## WebDAV

`AuthenticationMode` accepts `None`, `Basic` (sent up front, saving a challenge round trip),
`BearerToken`, `Digest`, `Ntlm`, `Negotiate`, and `Windows` (current user). HTTPS endpoints support
`TrustedCertificateSha256` and `TrustedPublicKeySha256` pins, `RequireValidCertificateChain`, and a
client certificate for mutual TLS (`ClientCertificatePath`, or the PFX bytes in
`ClientCertificateContent`), loaded once and disposed with the connection. `MaxConnectionsPerServer` caps
concurrent connections, and `Headers` adds custom request headers (not the authorization, host, or framing
headers). A `MOVE` or `COPY` onto an existing
folder is refused (`storage.conflict`) rather than replacing it; a `207 Multi-Status` answer, where some
members failed, is `storage.partial_failure` (`destinationState=partial`). WebDAV does not declare
`AtomicMove`, so the library relays folder moves there.

- An upload is staged and moved into place; the destination is read just before that `MOVE`. An existing
  collection is never replaced (`storage.conflict`), and `Overwrite: T` is sent only when a file was
  found there. WebDAV offers no condition on a `MOVE` that the adapter can send, so a file replaced by a
  collection in the short window between that read and the `MOVE` is not detected (a compliant server
  would then delete the collection); the same window applies to a move or copy onto an existing file.
- A non-recursive folder delete never sends a bare `DELETE`, which always removes everything inside: the
  collection is locked (`LOCK`, depth 0), listed, and deleted under the lock only when empty. A server
  without WebDAV locks (class 2) answers `storage.unsupported` and the folder is left; a folder holding
  anything, hidden names included, is `storage.conflict`. This affects everything that removes emptied
  folders on WebDAV: directory moves, sync folder deletes, and the rollback of created folders.

FTP removes a folder non-recursively with a raw `RMD`, which the server itself refuses while the folder
holds anything (hidden names included); a recursive FTP delete includes hidden files.

## Cloud emulators and compatible services

- **S3-compatible** (MinIO, Ceph, …): set `ServiceUrl` and usually `ForcePathStyle`. Servers differ in
  which conditional headers they enforce, so `ConditionalRequests` (an `S3ConditionalRequestSupport`)
  says how far to trust them:
  - `Auto` (default): the first time a conditional write, copy, or delete needs to know, the connection
    probes whether the server enforces `If-None-Match` and `If-Match` on `PutObject` (and so on
    `CompleteMultipartUpload`) and on `CopyObject`, and `If-Match` on `DeleteObject`. A condition found
    enforced is sent with the committing request (`Atomic`); one found ignored or rejected (400/501) is not
    sent at all, and is checked just before instead (`CheckedBeforeCommit`). AWS S3 enforces all of them;
    MinIO enforces them on `PutObject` and ignores them on `CopyObject` and `DeleteObject`. Until the probe
    has run, and after a probe that could not finish, the connection's
    `ConditionalCreate`/`ConditionalUpdate`/`ConditionalDelete` flags are provisional (all declared);
    after a probe that finished they name only what is enforced on every committing request, uploads and
    copies alike (on MinIO none of the three). `GetConditionEnforcementAsync` asks, running the probe when needed (see
    [Guaranteed transfers](transfers.md#guaranteed-transfers)). A probe that cannot finish (a network
    error, missing permissions) assumes nothing is enforced and is tried again after a back-off that
    doubles from 1 minute up to 32 minutes.
    The probe's requests are ordinary writes: four `PUT`s, two `COPY`s, and three `DELETE`s of two
    one-byte `.cl-storage-probe-*` objects under the prefix, removed afterwards. It needs `PutObject` and
    `DeleteObject` permission there; on a versioned bucket it leaves noncurrent versions and delete
    markers, on an Object Lock bucket versions that cannot be deleted until their retention ends, and it
    triggers event notifications, replication, and access logging like any write. Choose `Enforced` or
    `NotEnforced` to avoid it.
  - `Enforced`: trust every condition without probing.
  - `NotEnforced`: send no conditional headers; the library checks conditions itself just before each
    write, and the connection stops declaring `ConditionalCreate`/`ConditionalUpdate`/`ConditionalDelete`.
    Use it for a server that rejects or ignores conditional uploads.
  A server-side copy above 5 GiB is a multipart copy whose parts are the larger of 128 MiB and
  `MultipartPartSizeBytes` (16 MiB by default), larger still when needed to stay within 10,000 parts, and
  at most 5 GiB. Uploads switch to multipart at `MultipartThresholdBytes` (64 MiB by default).
- **Azure Blob**: a connection string works with Azurite (`UseDevelopmentStorage=true`). A move copies
  the blob's current content only and deletes the source with its snapshots (as `DeleteAsync` does);
  versions kept by blob versioning stay. To keep snapshots, copy and delete the source yourself.
- **Google Cloud Storage**: `ServiceUrl` plus `AuthenticationMode = Anonymous` targets an emulator such as fake-gcs-server.
- **Swift**: Keystone v3 (`KeystoneV3Password`, the default), `TempAuthV1` for SAIO-style servers
  (`AuthenticationUrl` ending in `/auth/v1.0`), or `StaticToken` with a `StorageUrl` and `Token`.

Plain-HTTP endpoints need `AllowInsecureHttp = true` on S3, Google Cloud, Swift, and WebDAV; Azure accepts
HTTP only through a connection string (Azurite).

## Proxies

Every remote provider can tunnel through an HTTP (`CONNECT`), SOCKS5, or SOCKS4 proxy:

```json
"Proxy": { "Type": "Socks5", "Host": "proxy.corp.local", "Port": 1080, "Username": "me", "Password": "..." }
```

SOCKS5 and HTTP proxies resolve the destination host name on the proxy side. SOCKS4 cannot, so
the host must resolve from the client, and SOCKS4 carries no password. FTP data connections are
tunnelled as well, so use passive mode, and the server's passive address must be reachable
from the proxy. Without a `Proxy`, WebDAV and Swift connect directly, while S3, Azure, and Google Cloud
keep their SDK's defaults, which may pick up a system proxy.

## Sessions, retries, and keep-alive

FTP and SFTP keep authenticated sessions in a per-connection pool, and FTP, SFTP, and WebDAV
retry transient failures automatically. Both are tuned per connection:

```json
{
  "Session": {
    "MaxSessions": 4,
    "MaxIdleSessions": 2,
    "IdleLifetimeSeconds": 120,
    "AcquireTimeoutSeconds": 30,
    "ValidateAfterIdleSeconds": 15,
    "KeepAliveSeconds": 60,
    "LingerSeconds": 0
  },
  "Retry": {
    "RetryCount": 3,
    "BaseDelayMs": 100,
    "MaxDelayMs": 30000,
    "RetryNonIdempotent": false
  }
}
```

- `MaxSessions` caps open sessions, busy or idle, so the library stays under a server's per-user
  connection limit. Callers beyond it wait up to `AcquireTimeoutSeconds`, then receive
  `storage.server_busy`. A relayed copy within one FTP or SFTP connection needs two sessions; with
  `MaxSessions = 1` it fails at once with `storage.unsupported`. Renames and moves on the server need one.
- A pooled session idle longer than `ValidateAfterIdleSeconds` is probed (FTP `NOOP`, SFTP `stat`)
  before reuse. Sessions that time out, drop, or fail TLS mid-operation are closed instead of reused.
- `KeepAliveSeconds` sends FTP `NOOP` or SSH keep-alive packets while a session is open.
- Reads, listings, info, and directory creation retry on timeouts, refused or dropped connections,
  and busy servers, with exponential backoff and jitter capped at `MaxDelayMs`. Uploads
  retry only from a seekable stream, which is replayed from its starting position; staged uploads
  never leave a partial file. Deletes and moves retry only with `RetryNonIdempotent`, because the
  first attempt may already have succeeded.
- Set `RetryCount` to `0` for single-attempt behavior.

### Shared sessions across registrations

Registrations with identical settings share one session pool while they coexist, whatever their ids, so
`MaxSessions` applies to all of them together. An application that retires idle registrations and adds
them again under new ids therefore keeps its warm sessions while any registration with those settings is
alive.

When the last one is removed, its sessions close at once. With `LingerSeconds` (0 to 3600, default 0) they
stay open that long instead, and a registration added again with the same settings picks them up. Leave
it at 0 for servers with a strict per-user connection limit, since lingering sessions count against it.
Idle pools are closed when the library stops, except those another library in the same process has used
too (they linger out as usual).

Listing continuation tokens are tied to the settings rather than the registration id too, so paging
continues after the same settings are registered again under a new id. A token refers to a listing
snapshot kept in the process for five minutes after its last use; after that, or in another process, the
listing is walked again from where the token points. Snapshots are shared by the whole process and bounded
(at most 32 of them and 250,000 items in total, the least recently used going first), so under load one can
go sooner; a listing of more than 250,000 items is never kept. This applies to FTP, SFTP, WebDAV, and local
listings; object stores page with the server's own tokens.

A pool, once created, keeps a copy of the settings it was created with; changing the configuration object
afterwards does not change a running pool.

## Runtime connections and native clients

```csharp
await storage.AddOrUpdateConnectionAsync("backup", new SftpConnectionConfig
{
    Host = "sftp.example.com",
    Username = "backup",
    AuthenticationMode = SftpAuthenticationMode.PrivateKey,
    PrivateKeyPath = @"C:\keys\backup_ed25519",
    HostKeyFingerprints = ["SHA256:..."]
});

Result<HealthStatus> health = await storage.CheckConnectionHealthAsync("backup");
```

Runtime changes are persisted to the provider's JSON section, or installed for the process only with
`persist: false`. A new or replacing connection is health-checked before it goes live, and in-flight
operations and leases on a replaced one drain first. Invalid settings, a failed build, or a failed health
check return a failed `Result` (invalid settings as `storage.invalid_content`, a build failure as
`storage.provider_error`); an id
already used by another provider is `storage.conflict`. `RemoveConnectionAsync(id, persist)` removes one,
`TryGetStorage` looks one up without throwing, `GetConnections()` lists them, and
`RegisterBackend`/`RegisterBackendAsync` install a custom `IStorageBackend` for the process only.

Reusable native SDK clients and scoped session clients remain available as an escape hatch for
provider administration (bucket/container creation, IAM/lifecycle policies) and non-portable options:

```csharp
IAmazonS3 s3 = storage.GetNativeClient<IAmazonS3>("media");

var opened = await storage.OpenNativeConnectionAsync<AsyncFtpClient>("legacy-ftp");
if (opened.IsSuccess)
{
    await using var lease = opened.Value!;
    AsyncFtpClient ftp = lease.Client;
}
```

Do not dispose reusable clients returned by `GetNativeClient`; dispose session leases.

### Editing the config while running

The same rules apply when a `config.storage*.json` section is changed outside the library (CodeLogic
4.9+) — through `Libraries.UpdateConfigAsync`, `Libraries.ReloadConfigAsync`, or the config file
watcher:

- **Changed or added connection**: built and health-checked, then swapped in; the connection it
  replaces finishes its in-flight operations and leases before it is disposed.
- **Removed or disabled connection**: retired the same way.
- **Untouched connections**: keep running, sessions and all.
- **Failed health check**: the current backend stays in service, the change stays saved, and the
  result reports `ConfigApplyResult.Applied = false` (the library shows Degraded until a later change
  applies).
- **`storage` section**: `defaultConnection` and `healthCheckTimeoutSeconds` apply at once, the
  library-wide speed limits are re-attached to every connection, and `maxBufferedDownloadBytes` applies
  to connections created or changed afterwards. Turning `enabled` on or off requires a restart.

The default connection cannot be removed or disabled this way; point `defaultConnection` elsewhere
first. Runtime connections installed with `persist: false` stay in effect on top of the file.

### Runtime-only mode

Applications that manage connections themselves can run the library without configuration files:

```csharp
var storage = new StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true });
// after the CodeLogic lifecycle has started it:
await storage.AddOrUpdateConnectionAsync("partner", sftpSettings);
```

No `config.storage*.json` section is registered, read, or written, and no default connection is
required. `persist` only updates the in-memory copy. Pass library-wide settings as
`StorageLibraryOptions.Settings`.

## Testing settings before saving them

`TestConnectionAsync` tries settings without saving or registering them, even before the library is
initialized. It reports each step it ran — `validate`, `connect`, `list`, `details` — and stops at the
first failure of the first three; a failed `details` step is recorded, but the test still succeeds (with
no `Diagnostics`). The test opens its own sessions and closes them when it ends, even when the settings are
identical to a registered connection's; invalid settings fail the `validate` step with
`storage.invalid_content`, as `AddOrUpdateConnectionAsync` does:

```csharp
var settings = new SftpConnectionConfig { Host = "sftp.example.com", Username = "deploy", Password = "..." };
var report = await storage.TestConnectionAsync(settings);

foreach (var step in report.Steps)
    Console.WriteLine($"{step.Name,-8} {(step.Succeeded ? "ok" : step.Error!.Code)} {step.Duration.TotalMilliseconds:0} ms");
```

When the server's certificate or host key is rejected, `ServerIdentity` still says what was presented
(FTP, SFTP, and WebDAV; not for a rejected jump-host key), which is what a setup screen needs for "the
server presented this fingerprint — trust it?":

```csharp
if (!report.Succeeded && report.ServerIdentity is { Kind: "ssh-host-key" } key)
{
    // Show key.Fingerprint and key.Algorithm, and on confirmation:
    settings.HostKeyFingerprints = [key.Fingerprint];
}

if (!report.Succeeded && report.ServerIdentity is { Kind: "tls-certificate" } certificate)
{
    // certificate.Subject, certificate.Issuer, certificate.NotAfter describe it.
    // Pin the public key so the pin survives a renewal that keeps the key:
    ftpSettings.TrustedPublicKeySha256 = [certificate.PublicKeyFingerprint!];
}
```

## Diagnosing a live connection

```csharp
var diagnostics = (await storage.GetConnectionDiagnosticsAsync("partner")).Value!;
Console.WriteLine($"{diagnostics.Host}:{diagnostics.Port} over {diagnostics.Security}");
Console.WriteLine($"{diagnostics.ServerSoftware} ({diagnostics.ServerSystem})");
```

| Property | FTP/FTPS | SFTP | WebDAV |
|---|---|---|---|
| `Security` | `None` or `Tls` | `Ssh` | `None` or `Tls` |
| `ServerSystem` | `SYST` reply | SSH version string | `Server` header |
| `ServerSoftware` | detected server type | e.g. `OpenSSH_9.6p1` | first `Server` token |
| `ServerFeatures` | `FEAT` capabilities | — | `DAV` classes and `Allow` methods |
| `Negotiated` | `tls`, `cipher` | `kex`, `hostKey`, `cipher`, `mac`, `compression`, `sftp` | `http` version |
| `ServerIdentity` | certificate | host key | certificate |
| `Pool` | session counters | session counters | — |

Other providers report host, port, security, and `LastHealth`. `GetConnections()` also carries
`Host`, `Port`, `Security`, and `LastHealth` for every configured connection. Diagnostics never contain
credentials.
