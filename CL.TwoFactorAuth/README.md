# CodeLogic.TwoFactorAuth

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> TOTP two-factor authentication with QR-code generation for [CodeLogic 4](https://github.com/zyntal-com/CodeLogic) — compatible with Google Authenticator, Authy, and 1Password.

Implements RFC 6238 time-based one-time passwords over [Otp.NET](https://www.nuget.org/packages/Otp.NET) and renders provisioning QR codes with [QRCoder](https://www.nuget.org/packages/QRCoder). Two services are exposed: an **authenticator** (generate/validate codes) and a **QR-code generator**.

## Install

```bash
dotnet add package CodeLogic.TwoFactorAuth
```

## Quick start

```csharp
await Libraries.LoadAsync<TwoFactorAuthLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var tfa = Libraries.Get<TwoFactorAuthLibrary>();

// 1. Enrol: create a key bound to issuer + user (fresh Base32 secret)
TwoFactorKey key = tfa.GenerateNewKey("MyApp", "user@example.com");
//    persist key.SecretKey alongside the user record

// 2. Show a QR code for the authenticator app
Result<string> qr = tfa.GenerateQrCodeDataUri(key);   // data:image/png;base64,...
if (qr.IsSuccess) { /* <img src="qr.Value"> */ }

// 3. Verify a code the user typed
TotpValidationResult check = tfa.ValidateTotp("123456", key.SecretKey);
if (check.IsValid) { /* grant access */ }
```

## Features

- **Secret generation** — cryptographically random Base32 TOTP secrets (`GenerateSecretKey`).
- **Provisioning** — `GenerateNewKey(issuer, user)` returns a `TwoFactorKey` with a standard `otpauth://` `ProvisioningUri`.
- **Generate & validate** — `GenerateTotpCode` / `ValidateTotp` with a configurable time step and drift window; validation returns a `TotpValidationResult` including the matched window offset.
- **Lifetime helpers** — `GetSecondsUntilExpiry()` and `GetCurrentTimeWindow()`.
- **QR rendering** — PNG / BMP bytes, Base64, data URI, and save-to-file, with configurable module size and error-correction level.
- **Events** — `SecretKeyGeneratedEvent` and `TotpValidatedEvent` on the CodeLogic event bus.

## Configuration

Auto-generated on first run as `config.twofactorauth.json`:

```json
{
  "Enabled": true,
  "TimeStepSeconds": 30,
  "WindowSize": 1,
  "QrCodeModuleSize": 20,
  "ErrorCorrectionLevel": "Q"
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch; when `false` the services aren't created and health reports *disabled*. |
| `TimeStepSeconds` | `30` | TOTP time step (1–300); must match the authenticator app. |
| `WindowSize` | `1` | Steps before/after the current window accepted (0–10); `1` ≈ ±30s drift. |
| `QrCodeModuleSize` | `20` | Pixel size of each QR module (1–100). |
| `ErrorCorrectionLevel` | `Q` | `L` (~7%), `M` (~15%), `Q` (~25%), `H` (~30%). |

## Documentation

Full guide: **[CL.TwoFactorAuth documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/twofactorauth.html)**

## Requirements

- [CodeLogic 4.9+](https://github.com/zyntal-com/CodeLogic) · .NET 10
- Otp.NET 1.x · QRCoder 1.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
