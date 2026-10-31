# CL.TwoFactorAuth

> TOTP two-factor authentication with QR-code generation — compatible with Google Authenticator, Authy, and 1Password.

`CL.TwoFactorAuth` adds RFC 6238 time-based one-time passwords to a CodeLogic application. It builds on [Otp.NET](https://www.nuget.org/packages/Otp.NET) for the TOTP algorithm and [QRCoder](https://www.nuget.org/packages/QRCoder) for rendering provisioning QR codes. The library exposes a single `TwoFactorAuthLibrary` surface with two underlying services — an **authenticator** and a **QR-code generator** — plus convenience pass-throughs for the common enrol/verify flow.

| | |
|---|---|
| **Package** | [`CodeLogic.TwoFactorAuth`](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth) |
| **Library class** | `CL.TwoFactorAuth.TwoFactorAuthLibrary` |
| **Config file** | `config.twofactorauth.json` |
| **Dependencies** | Otp.NET 1.x · QRCoder 1.x |

## Install & load

```bash
dotnet add package CodeLogic.TwoFactorAuth
```

```csharp
using CL.TwoFactorAuth;

await Libraries.LoadAsync<TwoFactorAuthLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var tfa = Libraries.Get<TwoFactorAuthLibrary>();
```

## The enrolment flow

Two-factor enrolment is a three-step exchange: generate a secret, show it to the user as a QR code, then verify the first code they type back.

### 1. Generate a key

`GenerateNewKey(issuer, user)` returns a `TwoFactorKey` carrying a fresh Base32 secret and a ready-to-use `otpauth://` provisioning URI.

```csharp
TwoFactorKey key = tfa.GenerateNewKey("MyApp", "user@example.com");

// Persist key.SecretKey with the user record — you need it to validate later.
string secret = key.SecretKey;            // Base32
string uri    = key.ProvisioningUri;      // otpauth://totp/MyApp:user@example.com?secret=...&issuer=MyApp
```

> **Store the secret encrypted.** Anyone with the Base32 secret can generate valid codes. Treat it like a password.

### 2. Show a QR code

The authenticator app scans a QR code that encodes the provisioning URI. The simplest path is a data URI you can drop straight into an `<img>` tag.

```csharp
Result<string> qr = tfa.GenerateQrCodeDataUri(key);
if (qr.IsSuccess)
{
    // qr.Value -> "data:image/png;base64,iVBORw0K..."
    // <img src="@qr.Value" alt="Scan with your authenticator app" />
}
```

### 3. Verify the first code

```csharp
TotpValidationResult check = tfa.ValidateTotp("123456", key.SecretKey);
if (check.IsValid)
{
    // Mark 2FA as enabled for this user.
    // check.MatchedWindow tells you which time window matched (drift offset).
}
```

## Validating at sign-in

After enrolment, every sign-in re-runs step 3 against the stored secret:

```csharp
TotpValidationResult result = tfa.ValidateTotp(userTypedCode, storedSecret);
if (!result.IsValid)
    return Unauthorized("Invalid authentication code.");
```

`ValidateTotp` accepts an optional `userId`; when supplied it is attached to the published `TotpValidatedEvent` so you can audit verification attempts on the event bus.

```csharp
var auth = tfa.Authenticator;
TotpValidationResult r = auth.ValidateTotp("123456", storedSecret, userId: "alice");
```

The `WindowSize` setting controls how much clock drift is tolerated — `1` accepts the previous and next time step (≈ ±30s) in addition to the current one.

## Services

The library exposes two services plus convenience pass-throughs for the most common calls.

| Member | Returns | Purpose |
|--------|---------|---------|
| `Authenticator` | `TwoFactorAuthenticator` | TOTP generation / validation service |
| `QrCode` | `QrCodeGenerator` | QR-code rendering service |
| `GenerateSecretKey()` | `string` | New Base32 secret |
| `GenerateNewKey(issuer, user)` | `TwoFactorKey` | New key + provisioning URI |
| `GenerateTotpCode(secret)` | `Result<string>` | Current 6-digit code for a secret |
| `ValidateTotp(code, secret)` | `TotpValidationResult` | Verify a 6-digit code |
| `GenerateQrCodeDataUri(key)` | `Result<string>` | `data:image/png;base64,…` URI |

### TwoFactorAuthenticator

```csharp
var auth = tfa.Authenticator;

string secret              = auth.GenerateSecretKey();
Result<string> code        = auth.GenerateTotpCode(secret);
TotpValidationResult check = auth.ValidateTotp("123456", secret, userId: "alice");

int  secondsLeft = auth.GetSecondsUntilExpiry();   // seconds until the current code rolls over
long window      = auth.GetCurrentTimeWindow();     // current TOTP time-step index
```

### QrCodeGenerator

All QR methods accept a `TwoFactorKey` and render its `ProvisioningUri`.

```csharp
var qr  = tfa.QrCode;
var key = tfa.GenerateNewKey("MyApp", "user@example.com");

Result<byte[]> png    = qr.GenerateQrCodePng(key);
Result<byte[]> bmp    = qr.GenerateQrCodeBmp(key);
Result<string> base64 = qr.GenerateQrCodeBase64(key);
Result<string> uri    = qr.GenerateQrCodeDataUri(key);
Result         saved  = qr.SaveQrCodeToFile(key, "qrcode.png");
```

Module size and error-correction level come from configuration (`QrCodeModuleSize`, `ErrorCorrectionLevel`).

## TwoFactorKey

```csharp
public sealed class TwoFactorKey
{
    public string Issuer { get; }
    public string AccountName { get; }
    public string SecretKey { get; }        // Base32 — persist this
    public string ProvisioningUri { get; }  // otpauth://totp/...
}
```

## TotpValidationResult

```csharp
public sealed class TotpValidationResult
{
    public bool IsValid { get; }
    public long? MatchedWindow { get; }   // which time window matched (drift offset), null if invalid
}
```

## Configuration

The library writes `config.twofactorauth.json` with defaults on first run.

```json
{
  "Enabled": true,
  "TimeStepSeconds": 30,
  "WindowSize": 1,
  "QrCodeModuleSize": 20,
  "ErrorCorrectionLevel": "Q"
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the services aren't created and the health check reports *disabled*. |
| `TimeStepSeconds` | `int` | `30` | TOTP time step in seconds (1–300). Must match the authenticator app — `30` is the universal default. |
| `WindowSize` | `int` | `1` | Steps before/after the current window to accept (0–10). `1` ≈ ±30s drift tolerance; raise only if users report clock-skew failures. |
| `QrCodeModuleSize` | `int` | `20` | Pixel size of each QR module (1–100). Larger = bigger image. |
| `ErrorCorrectionLevel` | `string` | `Q` | QR redundancy: `L` (~7%), `M` (~15%), `Q` (~25%), `H` (~30%). Higher tolerates more damage at the cost of density. |

## Events

Both events implement `IEvent` and are published to the CodeLogic event bus.

| Event | Published when |
|-------|----------------|
| `SecretKeyGeneratedEvent` | A new secret/key is generated. |
| `TotpValidatedEvent` | `ValidateTotp` runs with a `userId` supplied (carries the user id and outcome). |

## Health check

`HealthCheckAsync()` exercises live TOTP key generation to confirm the algorithm and configuration are working. When `Enabled` is `false`, it reports *disabled* rather than failing.

```csharp
var status = await tfa.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth)
