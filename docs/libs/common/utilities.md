# CL.Common — Utilities

> Strings, time, web, networking, cron, imaging, caching, file handling, and reflection.

This page covers the everyday utility helpers in `CL.Common`. Most are **static classes** — the one exception is the cache, which is an instance type (`MemoryCache` implementing `ICache`). Pure transforms and predicates return plain values; anything involving I/O, parsing, or the network returns a framework `Result` / `Result<T>`.

See the [Overview](index.md) for loading and the full catalog, and [Security & Data](security-data.md) for encryption, hashing, generators, JSON, conversion, and compression.

## Strings — StringHelper

`CL.Common.Strings.StringHelper` is a kit of text transforms. Every method returns a plain value.

```csharp
using CL.Common.Strings;

string slug  = StringHelper.ToSlug("Hello, World!");        // "hello-world"
string camel = StringHelper.ToCamelCase("hello world");     // "helloWorld"
string pascal= StringHelper.ToPascalCase("hello world");    // "HelloWorld"
string snake = StringHelper.ToSnakeCase("HelloWorld");      // "hello_world"
string kebab = StringHelper.ToKebabCase("HelloWorld");      // "hello-world"

string clip  = StringHelper.Truncate("a long sentence", 6); // "a long..."
string plain = StringHelper.StripHtml("<b>hi</b>");          // "hi"
int    words = StringHelper.WordCount("one two three");      // 3
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Truncate(value, maxLength, suffix = "...")` | `string` | Clip with an ellipsis (or custom suffix). |
| `ToSlug` / `ToCamelCase` / `ToPascalCase` / `ToSnakeCase` / `ToKebabCase(value)` | `string` | Case / slug conversions. |
| `StripHtml(value)` | `string` | Remove HTML tags. |
| `WordCount(value)` | `int` | Count words. |
| `Repeat(value, count)` | `string` | Repeat a string. |
| `Reverse(value)` | `string` | Reverse the characters. |
| `ContainsAny(value, params values)` | `bool` | Whether any candidate appears. |
| `CountOccurrences(text, substring)` | `int` | Count non-overlapping occurrences. |
| `ExtractNumbers(text)` | `IEnumerable<long>` | Pull integer runs out of text. |
| `IsAscii(value)` | `bool` | Whether the string is pure ASCII. |
| `Capitalize(value)` | `string` | Capitalize the first letter. |

## Strings — StringValidator

`CL.Common.Strings.StringValidator` is a set of `bool` predicates for common formats.

```csharp
using CL.Common.Strings;

bool email = StringValidator.IsEmail("a@b.com");
bool url   = StringValidator.IsUrl("https://example.com");
bool ip4   = StringValidator.IsIPv4("192.168.0.1");
bool guid  = StringValidator.IsGuid("d3b07384-d9a0-4c9b-8b3e-...");
bool fits  = StringValidator.HasMaxLength("hi", 10);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `IsEmail` / `IsUrl` / `IsPhoneNumber` / `IsNumeric` / `IsAlphanumeric` / `IsIPv4` / `IsIPv6` / `IsGuid(value)` | `bool` | Format checks. |
| `HasMinLength(value, min)` / `HasMaxLength(value, max)` | `bool` | Length bounds. |
| `MatchesPattern(value, pattern)` | `bool` | Regex match. |
| `IsNullOrWhiteSpace(value)` / `IsNotEmpty(value)` | `bool` | Emptiness checks. |

## Time — DateTimeHelper

`CL.Common.Time.DateTimeHelper` handles Unix time, ISO 8601, business-day math, and boundary calculations. Almost all return plain values; `FromIso8601` returns a `Result` because parsing can fail.

```csharp
using CL.Common.Time;

long     unix = DateTimeHelper.ToUnixTimestamp(DateTime.UtcNow);
DateTime dt   = DateTimeHelper.FromUnixTimestamp(unix);              // UTC
string   iso  = DateTimeHelper.ToIso8601(DateTime.UtcNow);
Result<DateTime> parsed = DateTimeHelper.FromIso8601("2026-06-20T12:00:00Z");

int      age  = DateTimeHelper.GetAge(new DateTime(1990, 1, 1));
bool     wknd = DateTimeHelper.IsWeekend(DateTime.UtcNow);
DateTime nbd  = DateTimeHelper.NextBusinessDay(DateTime.UtcNow);
DateTime som  = DateTimeHelper.StartOfMonth(DateTime.UtcNow);
string   rel  = DateTimeHelper.ToRelativeString(DateTime.UtcNow.AddMinutes(-3)); // "3 minutes ago"
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `ToUnixTimestamp(value)` | `long` | Seconds since epoch. |
| `FromUnixTimestamp(seconds)` | `DateTime` | UTC time from epoch seconds. |
| `ToUnixMilliseconds(value)` | `long` | Milliseconds since epoch. |
| `ToIso8601(value)` | `string` | ISO 8601 string. |
| `FromIso8601(value)` | `Result<DateTime>` | Parse ISO 8601. |
| `GetAge(birthDate)` | `int` | Whole years. |
| `IsWeekend(value)` / `IsBusinessDay(value)` | `bool` | Day classification. |
| `NextBusinessDay(value)` | `DateTime` | Next non-weekend day. |
| `StartOfDay` / `EndOfDay` / `StartOfMonth` / `EndOfMonth(value)` | `DateTime` | Period boundaries. |
| `StartOfWeek(value, startOfWeek = Monday)` | `DateTime` | Week start. |
| `ToRelativeString(value)` | `string` | Human "3 minutes ago" text. |

## Web

The `CL.Common.Web` namespace bundles four helpers. `HttpClientHelper` returns `Result` (network calls fail); the rest return plain values.

### HtmlHelper

```csharp
using CL.Common.Web;

string text = HtmlHelper.StripTags("<p>Hello <b>world</b></p>");   // "Hello world"
string safe = HtmlHelper.Sanitize(userHtml);
string enc  = HtmlHelper.Encode("<a>");                            // "&lt;a&gt;"
string clip = HtmlHelper.TruncateText(longHtml, 120);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `StripTags` / `Sanitize` / `TrimWhitespace` / `Encode` / `Decode(html)` | `string` | HTML cleanup and encoding. |

`Sanitize` is an allowlist: basic formatting elements without attributes, and links with http(s),
mailto or relative `href`s (`SanitizeAllowedTags` lists the elements). Images, styles, tables and embeds
are removed; use a full HTML sanitizer for rich text.
| `TruncateText(html, maxLength, suffix = "...")` | `string` | Length-limit HTML text. |

### HttpClientHelper

Typed `Result`-based requests over an `HttpClient` you supply, with optional headers.

```csharp
using CL.Common.Web;

using var http = new HttpClient();

Result<string> body = await HttpClientHelper.GetStringAsync(http, "https://example.com");
Result<MyDto>  dto  = await HttpClientHelper.GetJsonAsync<MyDto>(http, "https://api.example.com/item/1");
Result<string> post = await HttpClientHelper.PostJsonAsync(http, url, new { name = "Ada" });
Result<MyDto>  rt   = await HttpClientHelper.PostJsonAsync<CreateDto, MyDto>(http, url, payload);
Result         del  = await HttpClientHelper.DeleteAsync(http, url);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetStringAsync(client, url, headers?)` | `Task<Result<string>>` | GET as text. |
| `GetJsonAsync<T>(client, url, headers?)` | `Task<Result<T>>` | GET + deserialize. |
| `PostJsonAsync<TBody>(client, url, body, headers?)` | `Task<Result<string>>` | POST JSON, raw response. |
| `PostJsonAsync<TBody, TResponse>(client, url, body, headers?)` | `Task<Result<TResponse>>` | POST JSON, typed response. |
| `PutJsonAsync<TBody>(client, url, body, headers?)` | `Task<Result<string>>` | PUT JSON. |
| `DeleteAsync(client, url, headers?)` | `Task<Result>` | DELETE. |

### HttpHeaderHelper

Pulls request-context facts out of raw header values.

```csharp
using CL.Common.Web;

string locale = HttpHeaderHelper.GetPrimaryLocale(acceptLanguageHeader);   // e.g. "en"
string ip     = HttpHeaderHelper.GetEffectiveClientIp(remoteIp, xForwardedFor);
bool   bot    = HttpHeaderHelper.IsBot(userAgent);
bool   mobile = HttpHeaderHelper.IsMobile(userAgent);
bool   json   = HttpHeaderHelper.AcceptsJson(acceptHeader);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetPrimaryLocale(acceptLanguage?)` | `string` | Best language from `Accept-Language`. |
| `GetEffectiveClientIp(remoteIp?, xForwardedFor?)` | `string` | Real client IP behind a proxy. |
| `IsPrivateIp(ip)` | `bool` | Whether an IP is in a private range. |
| `IsBot(userAgent?)` / `IsMobile(userAgent?)` / `AcceptsJson(accept?)` | `bool` | User-agent / accept checks. |
| `GetHeader(headers, name)` | `string?` | Case-insensitive header lookup. |

### UrlHelper

```csharp
using CL.Common.Web;

string url = UrlHelper.AppendQuery("https://api.example.com/search",
    new Dictionary<string, string> { ["q"] = "cats", ["page"] = "2" });

Dictionary<string, string> q = UrlHelper.ParseQuery(url);
string domain = UrlHelper.GetDomain(url);
bool   secure = UrlHelper.IsHttps(url);
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Encode(value)` / `Decode(value)` | `string` | URL encode/decode. |
| `IsValid(url)` | `bool` | Whether the string is a valid URL. |
| `Combine(baseUrl, relativePath)` | `string` | Join base + path. |
| `AppendQuery(url, params)` | `string` | Append query parameters. |
| `ParseQuery(url)` | `Dictionary<string, string>` | Parse the query string. |
| `GetDomain(url)` / `GetPath(url)` | `string` | Extract parts. |
| `IsHttps(url)` | `bool` | Whether the scheme is HTTPS. |

## Networking

`CL.Common.Networking` provides ping, DNS, subnet math, and traceroute as static helpers. Each returns a `Result` carrying a record.

```csharp
using CL.Common.Networking;

Result<PingResponse>   ping = await NetworkPing.PingAsync("example.com");
Result<string[]>       ips  = await NetworkDns.LookupAsync("example.com");
Result<SubnetInfo>     net  = SubnetCalculator.Calculate("192.168.1.10", "255.255.255.0");
Result<List<HopInfo>>  hops = await TraceRoute.TraceAsync("example.com");

if (ping.IsSuccess)
    Console.WriteLine($"{ping.Value.Address}: {ping.Value.RoundTripMs} ms");
else if (ping.Error is TimeoutError)
    Console.WriteLine("no reply in time");
else
    Console.WriteLine($"{ping.Error.Code}: {ping.Error.Message}");
```

A ping that gets no reply is a failed result, never a successful one:

| Outcome | Error kind | Code |
|---------|------------|------|
| No reply within `timeout` | `TimeoutError` | `networking.ping_timeout` |
| Host/network unreachable, other non-success `IPStatus`, or the name does not resolve | `UnavailableError` | `networking.ping_unreachable` (`Details` = the `IPStatus` or exception type) |
| Anything else | `InternalError` | `networking.ping_failed` |

| Member | Returns | Purpose |
|--------|---------|---------|
| `NetworkPing.PingAsync(host, timeout = 3000)` | `Task<Result<PingResponse>>` | ICMP ping; success means the host replied. |
| `NetworkDns.LookupAsync(hostname)` | `Task<Result<string[]>>` | Resolve a host to addresses. |
| `SubnetCalculator.Calculate(ip, subnetMask)` | `Result<SubnetInfo>` | IPv4 subnet math. |
| `TraceRoute.TraceAsync(host, maxHops = 30)` | `Task<Result<List<HopInfo>>>` | Walk the route to a host. |

```csharp
public sealed record PingResponse(string Host, string Address, long RoundTripMs);
public record SubnetInfo(string NetworkAddress, string BroadcastAddress, string SubnetMask, int UsableHosts);
public record HopInfo(int Hop, string Address, long Ms);
```

## Parser — Cron

`CL.Common.Parser.CronParser` parses 5-field cron expressions and computes the next UTC fire time. `Parse` and `GetNextOccurrence` return `Result`; `IsValid` returns `bool`.

```csharp
using CL.Common.Parser;

bool good = CronParser.IsValid("0 */6 * * *");
Result<CronExpression> parsed = CronParser.Parse("0 */6 * * *");
Result<DateTime> next = CronParser.GetNextOccurrence("0 */6 * * *");        // from now (UTC)
Result<DateTime> from = CronParser.GetNextOccurrence("0 0 * * *", DateTime.UtcNow.AddDays(1));
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Parse(expression)` | `Result<CronExpression>` | Parse into fields. |
| `IsValid(expression)` | `bool` | Quick validity check. |
| `GetNextOccurrence(expression, from? = null)` | `Result<DateTime>` | Next UTC fire time at/after `from`. |

```csharp
public record CronExpression(string Raw, string Minutes, string Hours,
                             string DayOfMonth, string Month, string DayOfWeek);
```

## Imaging — CLU_Imaging

`CL.Common.Imaging.CLU_Imaging` does image validation and transformation via SkiaSharp, working with both `Stream`s and file paths. The validation predicates return `bool`; transforms return `Result`.

Every method that decodes refuses images over `CLU_Imaging.DefaultMaxPixels` (40 megapixels, checked from the header before decoding; error `imaging.too_large`) and turns photos upright from their EXIF orientation. The size checks read only the header.

For user uploads use `ProcessImage`: one call that checks the pixel count, orients, scales down into a box and re-encodes (which also drops metadata such as GPS positions).

```csharp
using var upload = request.File.OpenReadStream();
Result<ProcessedImage> avatar = CLU_Imaging.ProcessImage(upload, new ImageProcessOptions
{
    MaxWidth = 512, MaxHeight = 512,   // scaled down, never up
    Format = ImageFormat.Webp,         // default
    Quality = 85,                      // default
    MaxPixels = 40_000_000             // default; refused before decoding
});
if (avatar.IsFailure) return BadRequest(avatar.Error!.Message);   // imaging.decode_failed / imaging.too_large
await storage.UploadBytesAsync($"avatars/{id}.webp", avatar.Value!.Data);
// avatar.Value.Width / Height are the final size
```

```csharp
using CL.Common.Imaging;

// Validate
using var input = File.OpenRead("photo.jpg");
bool fits = CLU_Imaging.IsValidSize(input, maxWidth: 4000, maxHeight: 4000);

// Convert to WebP (stream in, stream out)
using var src = File.OpenRead("photo.jpg");
Result<MemoryStream> webp = CLU_Imaging.ConvertImage(src, ImageFormat.Webp);
if (webp.IsSuccess)
    await File.WriteAllBytesAsync("photo.webp", webp.Value!.ToArray());

// File-path overloads
CLU_Imaging.ResizeImage("in.png", "out.png", 800, 600, maintainAspectRatio: true);
CLU_Imaging.CreateThumbnail("in.png", "thumb.png", thumbnailSize: 128);
Result<(int width, int height)> dims = CLU_Imaging.GetImageDimensions("in.png");
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `ProcessImage(stream, options?)` | `Result<ProcessedImage>` | Safe upload pipeline: pixel limit, EXIF orientation, fit into a box, re-encode (default WebP q85). |
| `FitWithin(width, height, maxWidth, maxHeight)` | `(int, int)` | Size scaled down (never up) to fit a box; 0 = no limit on that side. |
| `IsValidSize(stream, maxWidth, maxHeight)` | `bool` | Whether dimensions are within bounds (header only). |
| `IsValidAspectRatio(stream)` | `bool` | Aspect-ratio sanity check (header only). |
| `ConvertImage(stream, format)` | `Result<MemoryStream>` | Re-encode to JPEG/PNG/WebP (quality 90). |
| `ResizeImage(stream, targetWidth, targetHeight, allowCrop = true)` | `Result<MemoryStream>` | Resize to PNG: `allowCrop` covers and crops to the exact size, otherwise fits inside keeping the aspect ratio. |
| `ResizeImage(stream, targetWidth, targetHeight, allowCrop, format, quality = 90)` | `Result<MemoryStream>` | Resize to another format. |
| `ImageToBase64(imagePath)` | `Result<string>` | Read a file as a Base64 string. |
| `ResizeImage(inputPath, outputPath, width, height, maintainAspectRatio = true)` | `Result` | Resize file → file (`false` covers and crops). |
| `ConvertImageFormat(inputPath, outputPath, format)` | `Result` | Convert file → file. |
| `CreateThumbnail(inputPath, outputPath, thumbnailSize = 100)` | `Result` | Square thumbnail. |
| `GetImageDimensions(imagePath)` | `Result<(int width, int height)>` | Read the stored width/height from the header. |

```csharp
public enum ImageFormat { Jpeg, Png, Webp }
```

`ImageProcessOptions`: `Format` (Webp), `Quality` (85), `MaxWidth`/`MaxHeight` (0 = no limit), `MaxPixels` (`DefaultMaxPixels`), `ApplyOrientation` (true). `ProcessedImage(byte[] Data, int Width, int Height, ImageFormat Format)`.

Error codes: `imaging.decode_failed` (not an image), `imaging.too_large` (over the pixel limit), `imaging.invalid_options`, `imaging.encode_failed`.

## Caching — ICache / MemoryCache

`CL.Common.Caching.MemoryCache` is the one instance type here: a sealed, thread-safe, in-process cache implementing `ICache` and `IDisposable`. A background loop evicts expired entries on the cleanup interval (default 60 seconds).

```csharp
using CL.Common.Caching;

ICache cache = new MemoryCache();                                  // or new MemoryCache(TimeSpan.FromSeconds(30))

await cache.SetAsync("user:1", userObj, TimeSpan.FromMinutes(5));
User? cached = await cache.GetAsync<User>("user:1");               // null if missing/expired
bool  has    = await cache.ExistsAsync("user:1");

await cache.RemoveByPrefixAsync("user:");
int count = await cache.GetCountAsync();
await cache.ClearAsync();
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `SetAsync<T>(key, value, ttl? = null)` | `Task` | Store with optional TTL. |
| `GetAsync<T>(key)` | `Task<T?>` | Read; `null` if absent or expired. |
| `ExistsAsync(key)` | `Task<bool>` | Membership check. |
| `RemoveAsync(key)` | `Task` | Remove one entry. |
| `RemoveByPrefixAsync(prefix)` | `Task` | Remove all entries with a key prefix. |
| `ClearAsync()` | `Task` | Drop everything. |
| `GetCountAsync()` | `Task<int>` | Current entry count. |

The constructor is `MemoryCache(TimeSpan? cleanupInterval = null)`; pass a value to tune how often the eviction loop runs. Dispose the cache to stop the loop.

## File handling — FileSystem

`CL.Common.FileHandling.FileSystem` wraps file I/O so errors come back as `Result` rather than exceptions. The existence checks return plain `bool`.

```csharp
using CL.Common.FileHandling;

Result<string> text = await FileSystem.ReadAllTextAsync("notes.txt");
Result         w    = await FileSystem.WriteAllTextAsync("notes.txt", "hello");
Result         cp   = await FileSystem.CopyAsync("a.txt", "b.txt", overwrite: true);
Result<string[]> files = await FileSystem.GetFilesAsync("logs", "*.log");
Result<long>   size = FileSystem.GetFileSizeBytes("a.txt");

bool exists = FileSystem.FileExists("a.txt");
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `ReadAllTextAsync(path)` / `WriteAllTextAsync(path, content)` | `Task<Result<string>>` / `Task<Result>` | Text I/O. |
| `ReadAllBytesAsync(path)` / `WriteAllBytesAsync(path, data)` | `Task<Result<byte[]>>` / `Task<Result>` | Binary I/O. |
| `CopyAsync(source, dest, overwrite = false)` / `MoveAsync(source, dest)` | `Task<Result>` | Copy / move. |
| `DeleteFile(path)` / `CreateDirectory(path)` | `Result` | Delete file / create directory. |
| `GetFilesAsync(directory, pattern = "*")` | `Task<Result<string[]>>` | List matching files. |
| `GetFileSizeBytes(path)` | `Result<long>` | File size in bytes. |
| `FileExists(path)` / `DirectoryExists(path)` | `bool` | Existence checks. |

## Reflection — AssemblyHelper & ReflectionHelper

The `CL.Common.Assemblies` namespace holds two static helpers. `AssemblyHelper` reads assembly metadata and discovers types; `ReflectionHelper` does dynamic instance and member access. Metadata getters return plain values; loading, discovery, and dynamic access return `Result`.

```csharp
using CL.Common.Assemblies;

// Assembly metadata + discovery
string version = AssemblyHelper.GetVersion(someInstance);
Dictionary<string, string> meta = AssemblyHelper.GetMetadata(someInstance);
IEnumerable<Type> impls = AssemblyHelper.GetImplementors<IPlugin>(asm);
Result<string> res = AssemblyHelper.ReadEmbeddedResource(asm, "MyApp.banner.txt");

// Dynamic instance / member access
Result<MyType> made = ReflectionHelper.CreateInstance<MyType>();
Result<object?> val = ReflectionHelper.GetPropertyValue(obj, "Name");
Dictionary<string, object?> dict = ReflectionHelper.ToDictionary(obj);
bool plugin = ReflectionHelper.Implements<IPlugin>(typeof(MyType));
```

### AssemblyHelper

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetName` / `GetVersion` / `GetDescription` / `GetTitle` / `GetCompany` / `GetProduct` / `GetInformationalVersion(instance)` | `string` | Assembly attribute values. |
| `GetMetadata(instance)` | `Dictionary<string, string>` | All metadata at once. |
| `LoadFrom(filePath)` | `Result<Assembly>` | Load an assembly from disk. |
| `GetFileMetadata(filePath)` | `Result<Dictionary<string, string>>` | Metadata without loading into the AppDomain. |
| `GetImplementors<T>(assembly)` | `IEnumerable<Type>` | Types implementing `T`. |
| `GetTypesWithAttribute<TAttr>(assembly)` | `IEnumerable<Type>` | Types decorated with an attribute. |
| `ReadEmbeddedResource(assembly, name)` | `Result<string>` | Read an embedded text resource. |

### ReflectionHelper

| Member | Returns | Purpose |
|--------|---------|---------|
| `CreateInstance<T>()` | `Result<T>` | Construct via default constructor. |
| `CreateInstance(type, params args)` | `Result<object>` | Construct with arguments. |
| `GetPropertyValue(obj, name)` / `SetPropertyValue(obj, name, value)` | `Result<object?>` / `Result` | Read/write a property by name. |
| `ToDictionary<T>(value)` | `Dictionary<string, object?>` | Object → property map. |
| `InvokeMethod(obj, name, params args)` | `Result<object?>` | Call a method by name. |
| `Implements<TInterface>(type)` | `bool` | Interface check. |
| `HasDefaultConstructor(type)` | `bool` | Whether a no-arg constructor exists. |
| `GetFriendlyName(type)` | `string` | Readable type name (with generics). |

## See also

- [Overview](index.md) — loading, organization, and the full catalog.
- [Security & Data](security-data.md) — encryption, hashing, generators, JSON, conversion, and compression.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Common)
