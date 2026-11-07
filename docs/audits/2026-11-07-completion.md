# Audit completion: 2026-11-07

The remaining issues in the review of Claude's 2026-11-07 changes are addressed locally in CodeLogic.Libs.
No source changes were needed in the sibling CodeLogic framework or CL.WebLogic2 checkout. Validation finished
before integration or publication; subsequent PR and release actions are recorded in GitHub. Existing work from Claude was preserved.

## Fixes

- **MQTT admission:** client ownership, quotas and pending handshakes are reserved atomically. Connection generations
  and endpoints protect replacement sessions from stale disconnect/deletion events. Sweeping preserves pending
  handshakes, restores the previous owner after a failed replacement, and blocks admission until deletion finishes.
- **MQTT transport and reconnect:** warnings cover every network-visible plaintext listener, including when TLS is
  also enabled. Authentication refusal stops automatic reconnect until an explicit new ConnectAsync call.
- **MQTT persistence/shutdown:** a deterministic test schedules a snapshot during an ongoing retained-file write.
  Synchronous disposal starts observed cleanup; asynchronous disposal and normal stop join the same cleanup task.
- **Replay protection:** only Steam-confirmed assertions consume nonce entries. Verification concurrency and nonce
  capacity are bounded; overload fails closed without evicting valid entries. Unsafe TOTP replay retention and
  sweep intervals are rejected. TotpLoginGuard and atomic attempt-store contracts provide configurable login limits.
- **Mail:** whole-message downloads were replaced by bounded MIME-section reads. Aggregate message, per-body,
  per-attachment and attachment-count budgets apply. GetAttachmentAsync downloads one attachment on demand;
  descriptors expose PartSpecifier/EncodedSize and messages expose ContentTruncated.
- **Other lifetime/cancellation work:** pooled buffers cannot be resurrected after final release; GeoIP extraction
  checks cancellation throughout decompression and before installation; Windows counter disposal waits for sampling.
- **Quality:** completed CA2007 disposal conversion in all database providers, corrected transform formatting,
  shared single-line log formatting and observed cleanup helpers without package dependencies, and split the
  oversized files flagged in the supplied audit. Transfer policy remains provider-neutral.
- **Documentation:** updated affected READMEs, changelogs and guides; corrected the exception-result claim because
  MQTTnet 5.2 has no Result property on its connect-failure exception.

## Validation

**3,303 tests passed, 16 skipped, zero failures.** MySQL 8.4, PostgreSQL 18 and SQL Server 2019 ran in
isolated local test containers; SQLite ran directly. The live storage suite exercised FTP, FTPS, SFTP, SSH jumping,
WebDAV (Basic/Digest/TLS/mTLS), S3/MinIO, Azure/Azurite, GCS, Swift and proxies.

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Common.Tests | 337 | 0 |
| Data.Generator.Tests | 8 | 0 |
| Data.Tests | 252 | 0 |
| Data.Sqlite.Tests | 164 | 3 |
| Data.MySql.Tests | 133 | 3 |
| Data.PostgreSql.Tests | 130 | 4 |
| Data.SqlServer.Tests | 134 | 1 |
| GameNetQuery.Tests | 106 | 0 |
| GitHelper.Tests | 115 | 1 |
| Imaging.Tests | 127 | 0 |
| Mail.Tests | 120 | 1 |
| Mqtt.Tests | 104 | 0 |
| NetUtils.Tests | 151 | 2 |
| SocialConnect.Tests | 173 | 1 |
| Storage.Tests | 986 | 0 |
| SystemStats.Tests | 37 | 0 |
| TwoFactorAuth.Tests | 61 | 0 |
| Storage.Integration.Tests | 165 | 0 |
| **Total** | **3303** | **16** |

The MQTT suite was rerun with all 104 tests after CI exposed a queued-sweeper shutdown race. A Linux path-case conversion issue was also corrected; the complete Storage suite then passed on Linux (983 passed, three Windows-only skips) as well as Windows (986 passed).
Skipped tests retain their existing environmental/provider/replica conditions; replica-pair and reflection-only
release variants were not enabled. No release signing or publishing was attempted.

All 16 library package projects build in Release with CodeLogicFromNuGet=true, so the fixes do not require an
unpublished framework change. A local CodeLogic.Mqtt 5.0.0-audit.1 package was produced for inspection only.

The standalone original-race harness now reports:

```text
Unsafe TOTP retention: rejected
Rejected fake OpenID assertions retained in replay store: 0
Concurrent connections accepted with session limit 1: 1
Distinct users accepted concurrently for same client id: 1
```

## Operational choices

- Use the new LoginGuard for attempt limiting; low-level ValidateTotp keeps its existing no-throttling contract.
  Supply shared attempt/replay stores for multiple nodes, and rate-limit at the application boundary.
- Await DisposeAsync or normal stop when cleanup must finish before process exit. Dispose initiates cleanup.
- IMAP budgets bound retained decoded content, with additional bounded wire buffers and string/parser overhead;
  an omitted section can have unknown decoded size (Size = 0).
- Broker TLS can coexist with plaintext for migration. Set DisablePlainPort to require encryption.
- In-memory nonce capacity exhaustion refuses new sign-ins. Failed provider assertions consume no nonce capacity.

The normal deliberate PR/release workflow still applies when the owner is ready to push.
