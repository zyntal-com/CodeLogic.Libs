# CL.TwoFactorAuth — Changelog

All notable changes to **CodeLogic.TwoFactorAuth** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 5.0 (unreleased)

### Breaking

- Targets **.NET 11** (`net11.0`) and requires **CodeLogic 5.0** (`[5.0.0, 6.0.0)`). On .NET 10, stay on
  4.9.x. CodeLogic 5 makes `Result<T>` / `Result` C# 15 unions and `Error` a closed record with
  kind subtypes (`NotFoundError`, `ValidationError`, ...). Results can now be matched exhaustively
  with `switch`.
- **`TotpValidationResult.MatchedWindow` is a `long?` holding the absolute time step** that matched
  (Unix time / `TimeStepSeconds`, what Otp.NET reports and what `GetCurrentTimeWindow()` returns). It
  was an `int?` documented as a drift offset, and the narrowing could overflow with short periods. Use it
  for replay tracking (store the last accepted step per user, atomically). `TotpValidatedEvent.MatchedWindow`
  is `long?` too, and `TotpValidationResult.Valid(...)` takes `(long? matchedWindow, int? windowOffset)`.
- **Configuration validation enforces the documented upper limits:** `TimeStepSeconds` 1–300,
  `WindowSize` 0–10, `QrCodeModuleSize` 1–100. Only the lower bounds were checked before, so a config with
  larger values loaded and now fails validation.

### Added

- `TotpValidationResult.WindowOffset` (`int?`): the drift in steps (0 = current step, -1 = previous, +1 = next).
- `ValidateTotp(code, secretKey, DateTime timestampUtc, userId)` validates as of a given moment.
- `TwoFactorKey.PeriodSeconds` (default 30) and the `MinTimeStepSeconds` / `MaxTimeStepSeconds` /
  `MaxWindowSize` / `MaxQrCodeModuleSize` constants on `TwoFactorAuthConfig`.

### Fixed

- **The provisioning URI carries the time step.** `ProvisioningUri` omitted `period`, so with a
  non-default `TimeStepSeconds` the authenticator app generated 30-second codes the server rejected.
  `GenerateNewKey` now sets `PeriodSeconds` from the configuration and the URI always ends in
  `&period=<seconds>`, which also changes the QR code content.

### Documentation

- Configuration examples use the camelCase keys the CodeLogic loader reads (PascalCase keys were ignored).

## 2026-10-24

### Changed

- Requires **CodeLogic 4.9+**. The official libraries move to the 4.9 line together with the framework,
  whose live config changes the stateful libraries now use. No API changes in this library.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Documentation

- Full rewrite of the README and the `docs/libs/twofactorauth.md` guide to the
  shared house style: a concise, NuGet-rendered README (badges, tagline, install,
  quick start, features, configuration table + JSON, requirements) and a single
  deep documentation page covering the enrolment flow, sign-in validation, the
  `Authenticator` and `QrCode` services, the `TwoFactorKey` /
  `TotpValidationResult` shapes, configuration, events, and the health check.
  No API changes.

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README and the security guide to match the shipping API. The
  previous docs referenced members and config keys that do not exist
  (`GenerateSecret`, `Validate(secret, token)`, `GetOtpAuthUri`, backup-code
  helpers, and the `Issuer`/`SecretKeyLength`/`TokenValiditySeconds`/
  `AllowedClockSkewSeconds`/`QrCodeSize` settings).
- Documented the real configuration keys: `Enabled`, `TimeStepSeconds`,
  `WindowSize`, `QrCodeModuleSize`, and `ErrorCorrectionLevel`
  (`L`/`M`/`Q`/`H`).
- Documented the `Authenticator` and `QrCode` services and their full surface:
  `GenerateSecretKey`, `GenerateNewKey`, `GenerateTotpCode`, `ValidateTotp`
  (incl. the optional `userId` argument and the `TotpValidationResult` shape),
  `GetSecondsUntilExpiry`, `GetCurrentTimeWindow`, and the QR outputs
  `GenerateQrCodePng` / `GenerateQrCodeBmp` / `GenerateQrCodeBase64` /
  `GenerateQrCodeDataUri` / `SaveQrCodeToFile`.
- Documented the `TwoFactorKey.ProvisioningUri` (`otpauth://`) property, the
  `SecretKeyGeneratedEvent` / `TotpValidatedEvent` bus events, and the actual
  health-check behavior (live TOTP key generation).

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated 2FA configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. TOTP-based 2FA with QR code generation, backup codes,
and a CodeLogic-native flow for Google Authenticator / Authy / 1Password.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/zyntal-com/CodeLogic.Libs/commits/main/CL.TwoFactorAuth).
