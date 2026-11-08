# CodeLogic.TwoFactorAuth

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> TOTP two-factor authentication with QR-code generation for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — compatible with Google Authenticator, Authy, and 1Password.

Implements RFC 6238 time-based one-time passwords over [Otp.NET](https://www.nuget.org/packages/Otp.NET) and renders provisioning QR codes with [QRCoder](https://www.nuget.org/packages/QRCoder). Two services are exposed: an **authenticator** (generate/validate codes) and a **QR-code generator**.

## Install

```bash
dotnet add package CodeLogic.TwoFactorAuth
```

## Quick start

```csharp
using CL.TwoFactorAuth;
using CL.TwoFactorAuth.Models;
using CodeLogic.Core.Results;

await Libraries.LoadAsync<TwoFactorAuthLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var tfa = Libraries.Get<TwoFactorAuthLibrary>();

// 1. Enrol: create a key bound to issuer + user (fresh Base32 secret)
TwoFactorKey key = tfa.GenerateNewKey("MyApp", "user@example.com");
//    persist key.SecretKey alongside the user record

// 2. Show a QR code for the authenticator app
Result<string> qr = tfa.GenerateQrCodeDataUri(key);   // data:image/png;base64,...
if (qr.IsSuccess) { /* <img src="qr.Value"> */ }

// 3. Verify a code the user typed. Pass the user id: a code that was already used for that user is refused.
TotpValidationResult check = tfa.ValidateTotp("123 456", key.SecretKey, userId: "user@example.com");
if (check.IsValid) { /* grant access */ }

// At sign-in, LoginGuard adds attempt limits (five per user per five minutes by default).
TotpValidationResult login = tfa.LoginGuard.Validate("123 456", key.SecretKey, "user@example.com");
```

> **Limit login attempts.** A six-digit code has a million values and a drift window of three steps gives an attacker roughly three chances per guess, so unlimited attempts break TOTP in hours. `tfa.LoginGuard` counts attempts per user and locks out after five failures in five minutes; `ValidateTotp` does not count attempts. On several servers, supply a shared `ITotpAttemptStore`. Also rate-limit by source address, log failed attempts, and never reveal which factor failed.

## Features

- **Secret generation** — cryptographically random Base32 TOTP secrets (`GenerateSecretKey`).
- **Provisioning** — `GenerateNewKey(issuer, user)` returns a `TwoFactorKey` with a standard `otpauth://` `ProvisioningUri` that carries the configured `period`; issuer, user and secret are percent-encoded.
- **Generate & validate** — `GenerateTotpCode` / `ValidateTotp` with a configurable time step and drift window; whitespace in a typed code is ignored. Validation returns a `TotpValidationResult` with the matched absolute time step (`MatchedWindow`, a `long`) and the drift in steps (`WindowOffset`). Errors are logged; `ErrorMessage` is generic and never exception text.
- **Replay protection** — with a `userId`, a code whose time step was already accepted for that user is refused (`TotpValidationResult.ReplayedCodeMessage`). The check is atomic in an `ITotpUsedStepStore`; the default keeps the last accepted step per user in memory, and a shared implementation serves several servers.
- **Login guard** — `LoginGuard.Validate(code, secret, userId)` combines replay protection with per-user attempt limits (`ITotpAttemptStore`; default `InMemoryTotpAttemptStore`, five attempts per five minutes). Wrong, replayed and locked-out codes give the same result.
- **Lifetime helpers** — `GetSecondsUntilExpiry()` and `GetCurrentTimeWindow()`.
- **QR rendering** — PNG / BMP bytes, Base64, data URI, and save-to-file (fully qualified `.png` path), with configurable module size and error-correction level. Failures carry a generic message; details go to the log.
- **Events** — `SecretKeyGeneratedEvent` and `TotpValidatedEvent` on the CodeLogic event bus.

## Configuration

Auto-generated on first run as `config.twofactorauth.json`. Keys are camelCase and matched case-sensitively, so a PascalCase key is ignored and its default stays active:

```json
{
  "enabled": true,
  "timeStepSeconds": 30,
  "windowSize": 1,
  "qrCodeModuleSize": 20,
  "errorCorrectionLevel": "q"
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch; when `false` the services aren't created and health reports *disabled*. |
| `TimeStepSeconds` | `30` | TOTP time step (1–300); must match the authenticator app. |
| `WindowSize` | `1` | Steps before/after the current window accepted (0–10); `1` ≈ ±30s drift. |
| `QrCodeModuleSize` | `20` | Pixel size of each QR module (1–100). |
| `ErrorCorrectionLevel` | `Q` | `L` (~7%), `M` (~15%), `Q` (~25%), `H` (~30%). |

Values outside these ranges fail configuration validation.

## Documentation

Full guide: **[CL.TwoFactorAuth documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/twofactorauth.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- Otp.NET 1.x · QRCoder 1.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
