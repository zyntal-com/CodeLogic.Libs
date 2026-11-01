# CL.Common — Security & Data

> Encryption, hashing, ID and password generation, JSON, type conversion, and compression.

This page covers the security and data helpers in `CL.Common`. All types here are **static classes** — call them directly. The encryption and hashing helpers return plain values (`string`, `byte[]`, `bool`); JSON, conversion, and compression return a framework `Result` / `Result<T>`.

See the [Overview](index.md) for loading and the full catalog, and [Utilities](utilities.md) for the strings, time, web, networking, cron, resilience, scheduling, caching, fast hashing, threading, file, and reflection helpers.

## Security — Encryption

`CL.Common.Security.Encryption` does AES-256-GCM authenticated encryption. The key is derived from your password with PBKDF2 and a random per-call salt; the salt, nonce, authentication tag, and ciphertext are packed into a single Base64 string (or byte array). Because GCM is *authenticated*, any tampering with the ciphertext is detected and decryption fails.

```csharp
using CL.Common.Security;

// Strings — returns a self-contained Base64 blob (salt + nonce + tag + ciphertext)
string cipher = Encryption.EncryptAes("top secret", "passphrase");
string plain  = Encryption.DecryptAes(cipher, "passphrase");

// Raw bytes — same packing, byte[] in/out
byte[] enc = Encryption.EncryptBytes(File.ReadAllBytes("file.bin"), "passphrase");
byte[] dec = Encryption.DecryptBytes(enc, "passphrase");

// A fresh random key (Base64) — default 32 bytes
string key = Encryption.GenerateKey();          // 32-byte key
string k64 = Encryption.GenerateKey(length: 64);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `EncryptAes(plainText, password)` | `string` | AES-GCM encrypt; Base64 (salt + nonce + tag + ciphertext). |
| `DecryptAes(cipher, password)` | `string` | Decrypt a value produced by `EncryptAes`. |
| `EncryptBytes(data, password)` | `byte[]` | Byte-array variant of `EncryptAes`. |
| `DecryptBytes(data, password)` | `byte[]` | Byte-array variant of `DecryptAes`. |
| `GenerateKey(length = 32)` | `string` | Random key of `length` bytes, Base64-encoded. |

> The salt and nonce are generated fresh for every call, so encrypting the same plaintext twice yields different ciphertext — this is intended.

## Security — Hashing

`CL.Common.Security.Hashing` provides digest, password, and HMAC helpers. SHA digests and HMACs return lowercase hex; password hashing returns a self-describing string (`$pbkdf2-sha256$v1${iterations}${salt}${hash}`) that carries its own iteration count.

```csharp
using CL.Common.Security;

// Digests (lowercase hex)
string sha256 = Hashing.Sha256("payload");
string sha512 = Hashing.Sha512("payload");

// HMAC (keyed) — lowercase hex
string mac = Hashing.HmacSha256("payload", "shared-key");
string m5  = Hashing.HmacSha512("payload", "shared-key");

// PBKDF2 password hashing — the salt is embedded in the returned string
string stored = Hashing.HashPassword("hunter2");                 // "$pbkdf2-sha256$v1$600000$...", 600,000 iterations
bool   valid  = Hashing.VerifyPassword("hunter2", stored);       // true; iterations read from the hash
if (valid && Hashing.NeedsRehash(stored))                        // legacy format or fewer iterations
    stored = Hashing.HashPassword("hunter2");                    // store the upgraded hash

// Standalone salt
string salt = Hashing.GenerateSalt();   // 16 bytes by default
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Sha256(input)` | `string` | SHA-256 digest, lowercase hex. |
| `Sha512(input)` | `string` | SHA-512 digest, lowercase hex. |
| `Md5(input)` *(obsolete)* | `string` | MD5 digest — kept for legacy interop only; do not use for security. |
| `HashPassword(password, iterations = 600000)` | `string` | PBKDF2-SHA256 hash in the self-describing `$pbkdf2-sha256$v1$...` format. |
| `VerifyPassword(password, hashedPassword, iterations = 100000)` | `bool` | Constant-time verify. New-format hashes use their stored count (`iterations` ignored); legacy Base64 salt+hash strings (4.x) use `iterations`. Malformed input returns `false`. |
| `NeedsRehash(hashedPassword, iterations = 600000)` | `bool` | True for legacy, weaker or unparsable hashes; re-hash after a successful verify. |
| `DefaultPasswordIterations` / `LegacyPasswordIterations` / `PasswordHashPrefix` | constants | `600000`, `100000`, `"$pbkdf2-sha256$v1$"`. |
| `GenerateSalt(size = 16)` | `string` | Random salt, Base64. |
| `HmacSha256(input, key)` | `string` | HMAC-SHA256, lowercase hex. |
| `HmacSha512(input, key)` | `string` | HMAC-SHA512, lowercase hex. |

> Use `HashPassword`/`VerifyPassword` for credentials, not `Sha256`. PBKDF2 deliberately makes brute-forcing slow. The iteration count is stored in each hash, so you can raise it later without invalidating existing hashes; use `NeedsRehash` to upgrade them on login.

## Generators — IdGenerator

`CL.Common.Generators.IdGenerator` produces identifiers of various shapes. All methods return a plain `string` (or `long` for `Sequential`).

```csharp
using CL.Common.Generators;

string guid  = IdGenerator.NewGuid();            // standard GUID with dashes
string guidN = IdGenerator.NewGuidNoDashes();    // 32-char hex
long   seq   = IdGenerator.Sequential();         // monotonic long
string ts    = IdGenerator.Timestamp();          // timestamp-based id
string tsp   = IdGenerator.TimestampWithPrefix("ord");

string rnd   = IdGenerator.Random();             // random alphanumeric, 16 chars
string hex   = IdGenerator.RandomHex();          // random hex, 32 chars
string b64   = IdGenerator.RandomBase64();       // random Base64 from 24 bytes
string url   = IdGenerator.UrlSafe();            // URL-safe, 22 chars
string nano  = IdGenerator.NanoId();             // NanoID, 21 chars
string sort  = IdGenerator.Sortable();           // lexicographically sortable
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `NewGuid()` | `string` | GUID with dashes. |
| `NewGuidNoDashes()` | `string` | GUID as 32-char hex. |
| `Sequential()` | `long` | Monotonic numeric id. |
| `Timestamp()` | `string` | Timestamp-derived id. |
| `TimestampWithPrefix(prefix)` | `string` | Timestamp id with a leading prefix. |
| `Random(length = 16)` | `string` | Random alphanumeric. |
| `RandomHex(length = 32)` | `string` | Random hex string. |
| `RandomBase64(byteLength = 24)` | `string` | Random Base64 from N bytes. |
| `UrlSafe(length = 22)` | `string` | URL-safe random id. |
| `NanoId(length = 21)` | `string` | NanoID — compact, URL-safe, collision-resistant. |
| `Sortable()` | `string` | Lexicographically sortable id. |

## Generators — PasswordGenerator

`CL.Common.Generators.PasswordGenerator` builds passwords, passphrases, and PINs, and estimates strength. Generation methods return `string`; `CalculateStrength` returns the `PasswordStrength` enum.

```csharp
using CL.Common.Generators;

string pw   = PasswordGenerator.Generate(16);                          // mixed character classes
string only = PasswordGenerator.Generate(12, includeSpecial: false);  // letters + digits only
string str  = PasswordGenerator.GenerateStrong(20);                   // strong defaults
string pass = PasswordGenerator.GeneratePassphrase(wordCount: 4);     // "Apple-Bridge-Cloud-Drift"
string pin  = PasswordGenerator.GeneratePin(6);                       // numeric PIN

PasswordStrength s = PasswordGenerator.CalculateStrength(pw);
// PasswordStrength : VeryWeak | Weak | Medium | Strong | VeryStrong
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Generate(length = 16, includeLowercase = true, includeUppercase = true, includeDigits = true, includeSpecial = true)` | `string` | Random password from the selected character classes. |
| `GenerateStrong(length = 16)` | `string` | Password using strong defaults. |
| `GeneratePassphrase(wordCount = 4, separator = "-", capitalize = true)` | `string` | Word-based passphrase. |
| `GeneratePin(length = 6)` | `string` | Numeric PIN. |
| `CalculateStrength(password)` | `PasswordStrength` | Strength estimate. |

```csharp
public enum PasswordStrength
{
    VeryWeak,
    Weak,
    Medium,
    Strong,
    VeryStrong
}
```

## Data — JsonHelper

`CL.Common.Data.JsonHelper` wraps serialization with `Result` returns so malformed JSON never throws. `IsValidJson` is the one plain-`bool` member.

```csharp
using CL.Common.Data;

Result<string> json = JsonHelper.Serialize(new { name = "Ada" });          // {"name":"Ada"}
Result<string> nice = JsonHelper.Serialize(person, indented: true);
Result<Person> back = JsonHelper.Deserialize<Person>(json.Value!);

bool ok = JsonHelper.IsValidJson(json.Value!);

// File round-trip
await JsonHelper.SerializeToFile(person, "person.json");                    // Task<Result>
Result<Person> loaded = await JsonHelper.DeserializeFromFile<Person>("person.json");

// Merge an overlay over a base document
Result<string> merged = JsonHelper.Merge(baseJson, overlayJson);

// Pull a single property out as a JSON string
Result<string> name = JsonHelper.GetProperty(json.Value!, "name");
```

**Trimming and Native AOT.** Pass source-generated metadata and nothing is reflected over:

```csharp
[JsonSerializable(typeof(Person))]
internal sealed partial class AppJson : JsonSerializerContext;

Result<string> json = JsonHelper.Serialize(person, AppJson.Default.Person);       // JsonTypeInfo<T>
Result<Person> back = JsonHelper.Deserialize<Person>(json.Value!, AppJson.Default); // or the whole context
await JsonHelper.SerializeToFile(person, "person.json", AppJson.Default.Person, ct);
```

The reflection-based overloads are marked `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`, so the trimming and
AOT analyzers point at every call that still needs them.

| Member | Returns | Purpose |
|--------|---------|---------|
| `Serialize<T>(value, indented = false)` | `Result<string>` | Object → JSON. |
| `Deserialize<T>(json)` | `Result<T>` | JSON → object. |
| `SerializeToFile<T>(value, filePath, indented = true)` | `Task<Result>` | Write JSON to a file. |
| `DeserializeFromFile<T>(filePath)` | `Task<Result<T>>` | Read JSON from a file. |
| `IsValidJson(json)` | `bool` | Whether the input parses as JSON. |
| `Merge(baseJson, overlayJson)` | `Result<string>` | Overlay one document onto another. |
| `GetProperty(json, propertyName)` | `Result<string>` | Extract a property as a JSON string. |
| `Serialize<T>(value, JsonTypeInfo<T> \| JsonSerializerContext)` | `Result<string>` | AOT-safe serialize. A context without metadata for `T` gives `not_supported`. |
| `Deserialize<T>(json, JsonTypeInfo<T> \| JsonSerializerContext)` | `Result<T>` | AOT-safe deserialize. |
| `SerializeToFile<T>(value, filePath, typeInfo, ct)` / `DeserializeFromFile<T>(filePath, typeInfo, ct)` | `Task<Result>` / `Task<Result<T>>` | AOT-safe file round-trip (streams, cancellable). |

## Conversion — TypeConverter

`CL.Common.Conversion.TypeConverter` does safe, non-throwing conversions. The generic `Convert<T>` and the typed `ToXxx` helpers return `Result`; `TryConvert<T>` follows the classic `out` pattern.

```csharp
using CL.Common.Conversion;

Result<int>      n = TypeConverter.ToInt("42");
Result<double>   d = TypeConverter.ToDouble("3.14");
Result<bool>     b = TypeConverter.ToBool("true");
Result<DateTime> t = TypeConverter.ToDateTime("2026-06-20");
Result<Guid>     g = TypeConverter.ToGuid("d3b07384-d9a0-4c9b-8b3e-...");

Result<DayOfWeek> day = TypeConverter.ToEnum<DayOfWeek>("Monday");

// Generic / try pattern
Result<decimal> amount = TypeConverter.Convert<decimal>("19.99");
if (TypeConverter.TryConvert<long>("1000", out long? value)) { /* ... */ }
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Convert<T>(value)` | `Result<T>` | Convert any object to `T`. |
| `TryConvert<T>(value, out result)` | `bool` | Non-throwing try-convert. |
| `ToInt` / `ToLong` / `ToDouble` / `ToDecimal` / `ToBool` / `ToDateTime` / `ToGuid(string)` | `Result<...>` | Typed string conversions. |
| `ToEnum<T>(string)` *(where `T : struct, Enum`)* | `Result<T>` | Parse an enum value. |

## Compression — CompressionHelper

`CL.Common.Compression.CompressionHelper` offers four byte-array codecs — GZip, Brotli, Zstandard, and LZ4 — plus a string convenience pair that GZip-compresses and Base64-encodes in one step. Everything returns `Result`.

```csharp
using CL.Common.Compression;

// Byte arrays
Result<byte[]> gz   = CompressionHelper.CompressGzip(payloadBytes);
Result<byte[]> br   = CompressionHelper.CompressBrotli(payloadBytes);
Result<byte[]> zst  = CompressionHelper.CompressZstandard(payloadBytes);
Result<byte[]> lz4  = CompressionHelper.CompressLz4(payloadBytes);
Result<byte[]> back = CompressionHelper.DecompressLz4(lz4.Value!);

// Text → Base64 (GZip) and back
Result<string> blob = CompressionHelper.CompressString("a lot of repeated text...");
Result<string> text = CompressionHelper.DecompressString(blob.Value!);

// Decompression is capped (default 100 MiB); pass a tighter limit for untrusted input
Result<byte[]> small = CompressionHelper.DecompressGzip(untrusted, maxOutputBytes: 1_000_000);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `CompressGzip(data)` / `DecompressGzip(data, maxOutputBytes?)` | `Result<byte[]>` | GZip codec. |
| `CompressBrotli(data)` / `DecompressBrotli(data, maxOutputBytes?)` | `Result<byte[]>` | Brotli codec. |
| `CompressZstandard(data)` / `DecompressZstandard(data, maxOutputBytes?)` | `Result<byte[]>` | Zstandard codec (built-in `System.IO.Compression.ZstandardStream`, .NET 11). Reads any standard zstd frame. |
| `CompressLz4(data)` / `DecompressLz4(data, maxOutputBytes?)` | `Result<byte[]>` | LZ4 codec (via K4os). |
| `CompressString(text)` | `Result<string>` | GZip + Base64 in one step. |
| `DecompressString(text, maxOutputBytes?)` | `Result<string>` | Reverse of `CompressString`. |

Every `Decompress*` method stops once the output passes `maxOutputBytes` (default `DefaultMaxDecompressedBytes`,
100 MiB) and fails with `compression.output_limit_exceeded` (`CompressionHelper.OutputLimitExceededCode`), so a
small "decompression bomb" cannot exhaust memory.

> Pick the codec for the job: Brotli tends to compress smallest, GZip is the safe interop default, Zstandard compresses almost as well as Brotli at much higher speed (a good default for new formats), and LZ4 is the fastest for hot paths where size matters less than throughput.

## See also

- [Overview](index.md) — loading, organization, and the full catalog.
- [Utilities](utilities.md) — strings, time, web, networking, cron, resilience, scheduling, caching, fast hashing, threading, file, and reflection helpers.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Common)
