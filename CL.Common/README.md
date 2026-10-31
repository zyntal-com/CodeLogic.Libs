# CodeLogic.Common

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Common)](https://www.nuget.org/packages/CodeLogic.Common)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> A broad, stateless utility toolkit for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — security, ID/password generation, JSON, compression, imaging, strings, time, web, networking, caching, file handling, and reflection.

`CodeLogic.Common` is a grab-bag of small, dependency-light helpers used across CodeLogic applications. Almost everything is exposed as **static helper classes** grouped by namespace, so you call them directly — `Hashing.Sha256(...)`, `IdGenerator.NanoId()`, `JsonHelper.Serialize(...)` — with no library instance to wire up. Many fallible operations return a framework `Result` / `Result<T>` instead of throwing, while simple helpers return plain values (`string`, `bool`, `byte[]`). The package also ships a `CommonLibrary` (`ILibrary`) so it participates in the CodeLogic lifecycle and health system.

## Install

```bash
dotnet add package CodeLogic.Common
```

## Quick start

The static helpers need no setup — import a namespace and call:

```csharp
using CL.Common.Security;
using CL.Common.Generators;
using CL.Common.Data;

// Hashing — plain string return (lowercase hex)
string digest = Hashing.Sha256("my-secret-data");

// AES-256-GCM authenticated encryption — tampering is detected on decrypt
string cipher = Encryption.EncryptAes("top secret", "passphrase");
string plain  = Encryption.DecryptAes(cipher, "passphrase");

// A URL-safe, collision-resistant id — plain string return
string id = IdGenerator.NanoId();   // e.g. "V1StGXR8_Z5jdHi6B-myT"

// JSON — Result-based (never throws)
Result<string> json = JsonHelper.Serialize(new { name = "Ada" });   // {"name":"Ada"}
```

To participate in the CodeLogic lifecycle and health checks, load the library as usual — the static helpers remain available either way:

```csharp
using CL.Common;

await Libraries.LoadAsync<CommonLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

## Features

- **Security** — `Hashing` (SHA-256/512, PBKDF2 password hash/verify, HMAC) and `Encryption` (AES-256-GCM for strings and bytes, key generation).
- **Generators** — `IdGenerator` (GUID, sequential, timestamp, NanoID, URL-safe, sortable) and `PasswordGenerator` (passwords, passphrases, PINs, strength estimation).
- **Data & JSON** — `JsonHelper` serialize/deserialize, file I/O, validation, merge, property extraction.
- **Conversion** — `TypeConverter` safe `Result`-based conversion to int/long/double/bool/decimal/DateTime/Guid/enum.
- **Compression** — `CompressionHelper` GZip, Brotli, and LZ4 for bytes, plus GZip-to-Base64 string helpers.
- **Strings** — `StringHelper` (truncate, slug, case conversion, HTML strip) and `StringValidator` (email/URL/phone/IP/GUID checks).
- **Time** — `DateTimeHelper` Unix timestamps, ISO 8601, age, business days, boundaries, relative strings.
- **Web** — `HtmlHelper`, `HttpClientHelper`, `HttpHeaderHelper`, `UrlHelper` for HTML sanitization, typed HTTP requests, header/IP/UA parsing, and URL building.
- **Networking** — `NetworkPing`, `NetworkDns`, `SubnetCalculator`, `TraceRoute`.
- **Parser / Cron** — `CronParser` parse/validate 5-field cron and compute the next UTC occurrence.
- **Imaging** — `CLU_Imaging` safe upload processing (pixel limit, EXIF orientation, fit, WebP), resize, crop, format conversion (JPEG/PNG/WebP), thumbnails, dimensions, Base64 — via SkiaSharp.
- **Caching** — `ICache` / `MemoryCache` async typed in-process cache with per-entry TTL.
- **File handling** — `FileSystem` async read/write/copy/move/delete, directory create, listing, size, existence.
- **Reflection** — `AssemblyHelper` and `ReflectionHelper` for assembly metadata, type discovery, embedded resources, and dynamic property/method access.

## Documentation

Full guide: **[CL.Common documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/common/index.html)**

## Requirements

- No configuration required — `CL.Common` is a stateless utility library and writes no config file.
- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- SkiaSharp 3.x (imaging) · K4os.Compression.LZ4 1.x (LZ4) · Newtonsoft.Json 13.x

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
