# CL.Common

> A broad utility toolkit — security, generators, JSON, compression, strings, time, web, networking, caching, resilience, scheduling, fast hashing, file handling, and reflection for CodeLogic applications. Trim- and Native-AOT-compatible.

`CL.Common` is a grab-bag of small, dependency-light helpers used across CodeLogic applications. Almost everything is exposed as **static helper classes** grouped by namespace, so you call them directly without wiring up a library instance. Many fallible operations return a framework `Result` / `Result<T>` instead of throwing; the simplest helpers return plain values (`string`, `bool`, `byte[]`). The package also ships a `CommonLibrary` (`ILibrary`) so it participates in the CodeLogic lifecycle and health system — but loading it is optional for the static helpers.

| | |
|---|---|
| **Package** | [`CodeLogic.Common`](https://www.nuget.org/packages/CodeLogic.Common) |
| **Library class** | `CL.Common.CommonLibrary` |
| **Config file** | *None — stateless utility library* |
| **Dependencies** | K4os.Compression.LZ4 1.x · System.IO.Hashing |
| **AOT** | `IsAotCompatible`; reflection-based members are annotated |

> **Imaging moved.** `CLU_Imaging` is now the separate [`CodeLogic.Imaging`](../imaging/index.md) package (5.0).

This overview covers loading, how the helpers are organized, and the full catalog. The deep material lives on two sub-pages:

- **[Security & Data](security-data.md)** — `Encryption` (AES-GCM), `Hashing` (incl. password hashing), `IdGenerator`, `PasswordGenerator` / `PasswordStrength`, `JsonHelper`, `TypeConverter`, `CompressionHelper`.
- **[Utilities](utilities.md)** — `StringHelper` / `StringValidator`, `DateTimeHelper`, the `Web` helpers, the `Networking` set, `CronParser`, `RetryPolicy` / `CircuitBreaker`, `JobScheduler`, `MemoryCache<TKey>`, `FastHash`, `AsyncKeyedLock<TKey>`, `FileSystem`, and the reflection helpers.

## Install & load

```bash
dotnet add package CodeLogic.Common
```

The static helpers are usable directly — import a namespace and call:

```csharp
using CL.Common.Security;
using CL.Common.Generators;

string digest = Hashing.Sha256("my-secret-data");   // static, plain string return
string id     = IdGenerator.NanoId();               // static, plain string return
```

To participate in the lifecycle and health system, load `CommonLibrary` like any other `CL.*` library. This is optional — the helpers work without it.

```csharp
using CL.Common;

await Libraries.LoadAsync<CommonLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var common = Libraries.Get<CommonLibrary>();
```

## How it's organized

Each theme lives in its own namespace under `CL.Common.*`. Import the namespace you need and call the static class on it:

```csharp
using CL.Common.Security;     // Encryption, Hashing
using CL.Common.Generators;   // IdGenerator, PasswordGenerator
using CL.Common.Data;         // JsonHelper
using CL.Common.Web;          // UrlHelper, HtmlHelper, HttpClientHelper, HttpHeaderHelper
```

A few helpers return plain values, others return `Result<T>`. The rule of thumb: anything that can fail for an *expected* reason (bad input, I/O, parsing, network) returns a `Result`; deterministic transforms return the value directly.

```csharp
// Plain return — cannot meaningfully "fail"
string slug = StringHelper.ToSlug("Hello, World!");        // "hello-world"
bool   ok   = StringValidator.IsEmail("a@b.com");          // true
string hex  = Hashing.Sha256("payload");                   // lowercase hex

// Result return — inspect IsSuccess / Value / Error
Result<int>      n    = TypeConverter.ToInt("42");
Result<DateTime> next = CronParser.GetNextOccurrence("0 */6 * * *");
Result<string[]> ips  = await NetworkDns.LookupAsync("example.com");

if (n.IsSuccess)  Console.WriteLine(n.Value);
if (next.IsFailure) Console.WriteLine(next.Error?.Message);
```

## A quick taste

```csharp
using CL.Common.Security;
using CL.Common.Generators;
using CL.Common.Compression;
using CL.Common.Resilience;

// Authenticated encryption
string cipher = Encryption.EncryptAes("top secret", "passphrase");
string plain  = Encryption.DecryptAes(cipher, "passphrase");

// Memorable passphrase + strength
string pass = PasswordGenerator.GeneratePassphrase(wordCount: 4);  // "Apple-Bridge-Cloud-Drift"
PasswordStrength strength = PasswordGenerator.CalculateStrength(pass);

// Compress a payload with LZ4
Result<byte[]> packed = CompressionHelper.CompressLz4(payloadBytes);

// Retry a flaky call with exponential backoff
var retry = new RetryPolicy(new RetryOptions { MaxAttempts = 3 });
Result<string> page = await retry.ExecuteAsync((attempt, ct) => FetchAsync(url, ct),
    isTransient: e => e is TimeoutError or UnavailableError);
```

## The full catalog

Every helper class, grouped by area, with the page that documents it in depth.

### Security & Data — see [Security & Data](security-data.md)

| Namespace / Type | Kind | What it does |
|---|---|---|
| `Security.Encryption` | static | AES-256-GCM authenticated encrypt/decrypt (string & bytes), random key generation. |
| `Security.Hashing` | static | SHA-256/512, PBKDF2 password hash/verify, salt, HMAC-SHA256/512 (MD5 is obsolete). |
| `Generators.IdGenerator` | static | GUIDs, sequential, timestamp, random alphanumeric/hex/Base64, URL-safe, NanoID, sortable. |
| `Generators.PasswordGenerator` | static | Random/strong passwords, passphrases, PINs, strength estimation (`PasswordStrength`). |
| `Data.JsonHelper` | static | Serialize/deserialize, file I/O, validation, merge, property extraction. |
| `Conversion.TypeConverter` | static | Safe `Result`-based conversion to int/long/double/bool/decimal/DateTime/Guid/enum. |
| `Compression.CompressionHelper` | static | GZip, Brotli, Zstandard, and LZ4 for bytes, plus GZip-to-Base64 string helpers. |

### Utilities — see [Utilities](utilities.md)

| Namespace / Type | Kind | What it does |
|---|---|---|
| `Strings.StringHelper` | static | Truncate, slug, case conversion, HTML strip, word count, occurrence counting. |
| `Strings.StringValidator` | static | Email/URL/phone/IP/GUID checks, length and pattern matching. |
| `Time.DateTimeHelper` | static | Unix timestamps, ISO 8601, age, business days, boundaries, relative strings. |
| `Web.HtmlHelper` | static | Strip tags, sanitize, encode/decode, truncate HTML text. |
| `Web.HttpClientHelper` | static | Typed `Result`-based GET/POST/PUT/DELETE over an `HttpClient`. |
| `Web.HttpHeaderHelper` | static | Locale, client IP, bot/mobile detection, header extraction. |
| `Web.UrlHelper` | static | Encode/decode, validate, combine, append/parse query, domain/path. |
| `Networking.NetworkPing` | static | ICMP ping returning a `PingResponse` (no reply is a `TimeoutError` / `UnavailableError`). |
| `Networking.NetworkDns` | static | DNS lookup returning resolved addresses. |
| `Networking.SubnetCalculator` | static | IPv4 subnet math returning a `SubnetInfo`. |
| `Networking.TraceRoute` | static | Walks the route to a host, returning `HopInfo` records. |
| `Parser.CronParser` | static | Parse/validate 5-field cron and compute the next occurrence, in UTC or a time zone (`CronExpression`). |
| `Resilience.RetryPolicy` | instance | `Result`-based retry with constant/linear/exponential/decorrelated-jitter backoff. |
| `Resilience.CircuitBreaker` | instance | Consecutive-failure or failure-ratio breaker with half-open trials, `Isolate`/`Reset`. |
| `Scheduling.JobScheduler` | instance | Interval, cron and one-shot background jobs; overlap control, timeouts, concurrency limit. |
| `Caching.MemoryCache<TKey>` / `MemoryCache` (`ICache<TKey>` / `ICache`) | instance | Expiration, tags, size/entry limits with priority-aware LRU, stampede-protected `GetOrCreateAsync`. |
| `Hashing.FastHash` / `FastHasher` | static / struct | Non-cryptographic XXH3 / XXH128, incremental hashing. |
| `Threading.AsyncKeyedLock<TKey>` | instance | Async mutual exclusion per key, pooled per-key state. |
| `FileHandling.FileSystem` | static | Async read/write/copy/move/delete, directory create, listing, size, existence. |
| `Assemblies.AssemblyHelper` | static | Assembly metadata/loading, type discovery, embedded resources. |
| `Assemblies.ReflectionHelper` | static | Dynamic instance creation, property/method access, object-to-dictionary. |

## Health check

`CommonLibrary` implements `HealthCheckAsync()` so it shows up in operational monitoring like every other `CL.*` library. Because the library itself holds no state and has no external dependencies, the check always reports **Healthy** once the library is initialized.

```csharp
HealthStatus status = await common.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Common)
