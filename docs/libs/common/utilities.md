# CL.Common — Utilities

> Strings, time, web, networking, cron, resilience, scheduling, caching, fast hashing, threading, file handling, and reflection.

This page covers the everyday utility helpers in `CL.Common`. Most are **static classes**; the resilience, scheduling, caching and threading types are instances you create once and share. Pure transforms and predicates return plain values; anything involving I/O, parsing, or the network returns a framework `Result` / `Result<T>`.

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
| `GetJsonAsync<T>(client, url, typeInfo, headers?)`, `PostJsonAsync(…, bodyTypeInfo[, responseTypeInfo], …)`, `PutJsonAsync(…, bodyTypeInfo, …)` | as above | Trim/Native-AOT-safe overloads taking source-generated `JsonTypeInfo<T>`. The overloads without it use reflection and are marked `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`. |

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

// Fields evaluated in a time zone; returns a UTC instant
Result<DateTimeOffset> local = CronParser.GetNextOccurrence("0 9 * * 1-5", DateTimeOffset.UtcNow,
    TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"));
```

| Member | Returns | Purpose |
|--------|---------|---------|
| `Parse(expression)` | `Result<CronExpression>` | Parse into fields. |
| `IsValid(expression)` | `bool` | Quick validity check. |
| `GetNextOccurrence(expression, from? = null)` | `Result<DateTime>` | Next UTC fire time at/after `from`. |
| `GetNextOccurrence(expression, from, timeZone)` | `Result<DateTimeOffset>` | Next fire time strictly after `from`, with the fields read in `timeZone` (null = UTC). Used by `JobScheduler`. |

```csharp
public record CronExpression(string Raw, string Minutes, string Hours,
                             string DayOfMonth, string Month, string DayOfWeek);
```

## Resilience — RetryPolicy & CircuitBreaker

`CL.Common.Resilience` protects calls to flaky dependencies. Both types work on `Result` / `Result<T>`, take an
optional `TimeProvider` (so tests can drive delays and break durations with `FakeTimeProvider`), are thread-safe and
are meant to be created once and shared.

### RetryPolicy

Retries while a failure is *transient*, with backoff and jitter:

```csharp
using CL.Common.Resilience;

var retry = new RetryPolicy(new RetryOptions
{
    MaxAttempts = 4,                         // total, including the first
    BaseDelay = TimeSpan.FromMilliseconds(100),
    MaxDelay = TimeSpan.FromSeconds(5),
    Backoff = BackoffKind.Exponential,       // Constant | Linear | Exponential | DecorrelatedJitter
    JitterFactor = 0.2,                      // ±20 %
});
retry.Retrying += a => log.Warning($"attempt {a.Attempt} failed ({a.Error?.Code}); retrying in {a.Delay}");

Result<User> user = await retry.ExecuteAsync(
    (attempt, ct) => repository.GetAsync(id, ct),
    isTransient: e => e is TimeoutError or UnavailableError,
    ct);
```

- A failure `isTransient` rejects comes back unchanged, at once.
- When the last attempt fails transiently: `resilience.retries_exhausted` (unavailable) with the last error as
  `InnerError`.
- Once the token is cancelled no further attempt starts: `resilience.cancelled` (unavailable).
- An action that throws is treated as failing with `resilience.unhandled_exception`, which `isTransient` sees too;
  `RetryAttempt.Exception` carries the exception.
- `GetDelay(attempt)` exposes the schedule. A second constructor takes a `Random` for reproducible jitter.

### CircuitBreaker

Stops calling a failing dependency for `BreakDuration`, then lets trial calls through:

```csharp
var breaker = new CircuitBreaker("orders-db", new CircuitBreakerOptions
{
    FailureThreshold = 5,                    // consecutive failures that open the circuit
    // FailureRatio = 0.5, MinimumThroughput = 20, SamplingWindow = TimeSpan.FromSeconds(30),  // or: ratio-based
    BreakDuration = TimeSpan.FromSeconds(30),
    HalfOpenMaxCalls = 1,
});
breaker.StateChanged += c => log.Warning($"{c.Name}: {c.From} → {c.To}");

Result<Order> order = await breaker.ExecuteAsync(
    ct => repository.GetOrderAsync(id, ct),
    countsAsFailure: e => e is TimeoutError or UnavailableError,   // not-found etc. don't trip it
    ct);
// open → resilience.circuit_open (unavailable), Details "retry_after=…s"
```

Without a delegate: `if (breaker.TryAcquire(out var lease)) { …; lease.Success(); /* or lease.Failure() / lease.Ignore() */ }`.
`TryAcquire` and the lease don't allocate. `Isolate()` forces the circuit open until `Reset()`.

| State | Behaviour |
|---|---|
| `Closed` | Calls flow; failures counted (consecutive, or the ratio over a 10-bucket sliding window). |
| `Open` | Calls refused until `BreakDuration` has passed (or until `Reset()` after `Isolate()`). |
| `HalfOpen` | Up to `HalfOpenMaxCalls` trial calls; the first success closes, the first failure re-opens. |

Combine them by running the breaker inside the retry: `retry.ExecuteAsync((_, ct) => breaker.ExecuteAsync(..., ct), e => e.Code != ResilienceErrors.CircuitOpenCode && …)`.

Error codes (`ResilienceErrors` / `CommonErrors`): `resilience.circuit_open`, `resilience.retries_exhausted`,
`resilience.cancelled`, `resilience.unhandled_exception`.

## Scheduling — JobScheduler

`CL.Common.Scheduling.JobScheduler` runs named background jobs on an interval, a cron expression or once. Timers come
from the `TimeProvider`, so `FakeTimeProvider.Advance` drives it deterministically in tests.

```csharp
using CL.Common.Scheduling;

await using var scheduler = new JobScheduler(maxConcurrency: 4);   // 0 = unlimited
scheduler.JobCompleted += r =>
{
    if (r.Error is not null) log.Error($"{r.Name} failed after {r.Duration}: {r.Error}");
};

IJobHandle cleanup = scheduler.Schedule("cleanup",
    new JobSchedule.Interval(TimeSpan.FromMinutes(5)),
    async ct => await sessions.PurgeExpiredAsync(ct),
    new JobOptions { Timeout = TimeSpan.FromMinutes(1) });

scheduler.Schedule("nightly-report",
    new JobSchedule.Cron("0 3 * * *", TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen")),
    ct => reports.BuildAsync(ct));

scheduler.Schedule("warm-up", new JobSchedule.Once(DateTimeOffset.UtcNow.AddSeconds(10)), ct => cache.WarmAsync(ct));

cleanup.Pause();                                  // skip scheduled runs
Result run = await scheduler.TriggerAsync("cleanup");   // run now and wait (works while paused)
cleanup.Dispose();                                // unschedule
IReadOnlyList<JobInfo> jobs = scheduler.Jobs;     // last run, duration, error, next run, run count, …
await scheduler.StopAsync(drainTimeout: TimeSpan.FromSeconds(10));
```

| Schedule | Behaviour |
|---|---|
| `Interval(Period, InitialDelay?)` | Fixed rate from the scheduled times; missed slots are skipped, not run in a burst. |
| `Cron(Expression, TimeZone?)` | 5-field cron (see `CronParser`), evaluated in the zone (UTC by default), to the minute. |
| `Once(At)` | One run; immediately if `At` has passed. |

- **Failures never stop the scheduler.** A job that throws reports `scheduling.job_failed` in
  `JobRunCompleted.Error` and `JobInfo.LastError`; the next run happens as scheduled.
- **Overlap:** by default a due run is skipped while the previous run is still going, and `TriggerAsync` returns
  `scheduling.already_running`. Set `JobOptions.AllowOverlap` to run them side by side.
- **Timeout:** `JobOptions.Timeout` cancels the job's token; the run reports `scheduling.job_timeout`.
- **Jitter:** `JobOptions.Jitter` (0..1) adds a random fraction of the interval to each run.
- **Stop:** `StopAsync(drainTimeout)` starts no new runs, gives running jobs the drain time (default 30 s), then
  cancels their tokens (`scheduling.cancelled`). `DisposeAsync` calls it.
- Invalid schedules or a duplicate name throw `ArgumentException` (programming errors).

## Caching — ICache&lt;TKey&gt; / MemoryCache

`CL.Common.Caching.MemoryCache<TKey>` is a thread-safe in-process cache; `MemoryCache` is the string-keyed version.
Both take an optional `TimeProvider`, so expiration is testable with a fake clock.

```csharp
using CL.Common.Caching;

using var cache = new MemoryCache(new MemoryCacheOptions
{
    SizeLimit = 50_000,                         // in your unit (bytes, rows, …); null = unbounded
    MaxEntries = 10_000,                        // null = unbounded
    CompactionPercentage = 0.1,                 // evict to 10 % below the limit
    ExpirationScanInterval = TimeSpan.FromMinutes(1),
});

cache.Set("user:1", user, new CacheEntryOptions
{
    AbsoluteExpiration = TimeSpan.FromMinutes(10),
    SlidingExpiration = TimeSpan.FromMinutes(2),
    Tags = ["users", "tenant:42"],
    Size = 1,
    Priority = CachePriority.Normal,            // Low | Normal | High | NeverRemove
});

if (cache.TryGet("user:1", out User? hit)) { /* … */ }
if (cache.TryGet(keySpan, out User? fromSpan)) { /* span key, no string allocated */ }

// Stampede-protected: 100 concurrent callers → one factory run.
User u = await cache.GetOrCreateAsync("user:2", (key, ct) => repository.LoadUserAsync(key, ct),
    new CacheEntryOptions { AbsoluteExpiration = TimeSpan.FromMinutes(5) }, ct);

cache.RemoveByTag("tenant:42");
cache.RemoveByPrefix("user:");
CacheStatistics stats = cache.GetStatistics();  // Hits, Misses, Evictions, Count, Size

// Any key type, never boxed — e.g. 128-bit hashes:
using var plans = new MemoryCache<UInt128>();
plans.Set(FastHash.XxHash128(sql.AsSpan()), plan);
```

| Member | Purpose |
|---|---|
| `TryGet<T>(key, out value)` | Read a live entry; an entry of another type is a miss. |
| `GetOrCreateAsync<T>(key, factory, options?, ct)` | Read or create; one factory run per key at a time. A throwing factory caches nothing. |
| `Set<T>(key, value, options?)` | Store or replace. An entry larger than `SizeLimit` is not stored. |
| `Remove(key)` / `RemoveByTag(tag)` / `Clear()` | Explicit removal (tags use a reverse index). |
| `RemoveByPrefix(prefix)`, `TryGet<T>(ReadOnlySpan<char>, …)` | `ICache` (string keys) only. |
| `RemoveExpired()` | Sweep now (the periodic timer calls it). |
| `GetStatistics()` | Counters snapshot. |

**Eviction:** when a limit is exceeded, expired entries go first, then the lowest priority, least recently used
entries (approximate LRU) until the cache is `CompactionPercentage` below the limit. `NeverRemove` entries are only
removed by expiry or explicitly. Expired entries are removed when read and by the periodic sweep.

## Hashing — FastHash

`CL.Common.Hashing.FastHash` is fast **non-cryptographic** hashing (XXH3 / XXH128 via `System.IO.Hashing`) for cache
keys, deduplication and change detection. For passwords and signatures use `Security.Hashing`.

```csharp
using CL.Common.Hashing;

ulong   h64  = FastHash.XxHash3(bytes);
UInt128 h128 = FastHash.XxHash128(bytes, seed: 0);
UInt128 key  = FastHash.XxHash128(sql.AsSpan());       // UTF-16 bytes, no allocation
string  hex  = FastHash.ToHex(key);                    // 32 lowercase chars

var hasher = FastHasher.Create();                      // incremental
hasher.Append(sql.AsSpan());
hasher.Append(parameterCount);                         // any unmanaged value
hasher.Append(dialectBytes);
UInt128 combined = hasher.GetHash128();
```

Hashes are stable across processes and machines for the same input and seed. `FastHasher` is a mutable struct:
create it with `Create()` and don't copy it while appending.

## Threading — AsyncKeyedLock&lt;TKey&gt;

`CL.Common.Threading.AsyncKeyedLock<TKey>` is an async lock per key: different keys run in parallel, the same key one
at a time. Per-key state exists only while held and is pooled, so it doesn't grow with the number of keys seen.

```csharp
using CL.Common.Threading;

var locks = new AsyncKeyedLock<string>();

using (await locks.LockAsync(accountId, ct))
{
    // one caller per account at a time
}

if (locks.TryLock(accountId, out var releaser))
    using (releaser) { /* got it without waiting */ }
```

Not reentrant; dispose each releaser once. A cancelled wait throws `OperationCanceledException` and takes nothing.

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

These helpers are reflection by nature. They carry trimming annotations: `CreateInstance`, `ToDictionary` and
`HasDefaultConstructor` declare what they need with `[DynamicallyAccessedMembers]` (safe when the type is known
statically); name-based member access (`GetPropertyValue`, `SetPropertyValue`, `InvokeMethod`), `LoadFrom`,
`GetFileMetadata` and type discovery are `[RequiresUnreferencedCode]`, so a trimmed or Native AOT app gets a warning
where it calls them.

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
- [CL.Imaging](../imaging/index.md) — image processing, which moved out of CL.Common in 5.0.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Common)
