# CL.NetUtils

> DNSBL blacklist checking and MaxMind GeoIP geolocation — screen abusive IPs and locate visitors with one library.

`CL.NetUtils` adds two network-reputation tools to a CodeLogic application behind a single `NetUtilsLibrary` surface. The **DNSBL checker** queries blacklist zones (IPv4 and IPv6, with primary and fallback providers) to decide whether an address is listed as a known abuser. The **GeoIP service** resolves an address to a country, city, coordinates, and time zone using a [MaxMind GeoLite2/GeoIP2](https://www.maxmind.com/) `.mmdb` database, which can be supplied on disk or auto-downloaded with your MaxMind credentials.

There is no standalone DNS-lookup service — DNS resolution is an internal detail of the DNSBL checker.

| | |
|---|---|
| **Package** | [`CodeLogic.NetUtils`](https://www.nuget.org/packages/CodeLogic.NetUtils) |
| **Library class** | `CL.NetUtils.NetUtilsLibrary` |
| **Config file** | `config.netutils.json` (section `netutils`) |
| **Dependencies** | MaxMind.GeoIP2 5.x · SharpCompress 0.x |

## Install & load

```bash
dotnet add package CodeLogic.NetUtils
```

```csharp
using CL.NetUtils;

await Libraries.LoadAsync<NetUtilsLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var net = Libraries.Get<NetUtilsLibrary>();
```

The library exposes three members:

| Member | Type | Notes |
|--------|------|-------|
| `Dnsbl` | `DnsblChecker` | Always available when the library is enabled. |
| `GeoIp` | `GeoIpService` | **Throws** if accessed when no database is available — guard with `HasGeoIp`. |
| `HasGeoIp` | `bool` | `true` only when a GeoIP database loaded successfully on startup. |

## DNSBL checking

`DnsblChecker.CheckIpAsync` resolves the address against one or more DNSBL zones and returns a `DnsblCheckResult`. Two overloads are available.

### Using the configured zones

The no-request overload uses the zones from `config.netutils.json`:

```csharp
DnsblCheckResult result = await net.Dnsbl.CheckIpAsync("203.0.113.1");

if (result.IsBlacklisted)
    Console.WriteLine($"Listed by {result.MatchedService}");
else if (result.ErrorMessage is not null)
    Console.WriteLine($"Check failed: {result.ErrorMessage}");
else
    Console.WriteLine("Clean.");
```

The default blacklist zones are:

| Address family | Primary | Fallback |
|----------------|---------|----------|
| IPv4 | `zen.spamhaus.org`, `dnsbl.sorbs.net` | `b.barracudacentral.org` |
| IPv6 | `zen.spamhaus.org`, `dnsbl6.sorbs.net` | `v6.b.barracudacentral.org` |

The checker picks the IPv4 or IPv6 zone set based on the address it is given. Fallback zones are consulted only when the primary zones fail to answer. When `ParallelQueries` is `true`, zones in a set are queried concurrently.

> **Private and local addresses are never queried.** Loopback, RFC 1918 (`10/8`, `172.16/12`, `192.168/16`), and IPv6 link-local / site-local / unique-local ranges are short-circuited and returned as **not blacklisted** without any DNS lookup.

### Per-call overrides with `DnsblCheckRequest`

Apps that store their own DNSBL zones or IP allowlists (e.g. in a database) can bypass the static config with the request overload. Supply per-call service lists, a timeout, and an optional async allowlist predicate — returning `true` from `IsAllowedAsync` skips all DNSBL queries for that IP and returns **not blacklisted**.

```csharp
var request = new DnsblCheckRequest
{
    Ipv4Services         = ["zen.spamhaus.org"],
    Ipv4FallbackServices = ["b.barracudacentral.org"],
    Ipv6Services         = ["zen.spamhaus.org"],
    Ipv6FallbackServices = ["v6.b.barracudacentral.org"],
    Timeout              = TimeSpan.FromSeconds(5),
    DetailedLogging      = false,
    IsAllowedAsync       = async (ip, ct) => await myAllowlist.ContainsAsync(ip, ct),
};

DnsblCheckResult result = await net.Dnsbl.CheckIpAsync("203.0.113.1", request);
```

`DnsblCheckRequest` (sealed class) fields:

| Member | Type | Default | Description |
|--------|------|---------|-------------|
| `Ipv4Services` | `IReadOnlyList<string>` | empty | Primary IPv4 zones for this call. |
| `Ipv4FallbackServices` | `IReadOnlyList<string>` | empty | IPv4 fallback zones. |
| `Ipv6Services` | `IReadOnlyList<string>` | empty | Primary IPv6 zones. |
| `Ipv6FallbackServices` | `IReadOnlyList<string>` | empty | IPv6 fallback zones. |
| `IsAllowedAsync` | `Func<string, CancellationToken, Task<bool>>?` | `null` | Allowlist predicate; `true` skips all queries. |
| `Timeout` | `TimeSpan` | `5s` | Per-zone DNS timeout. |
| `DetailedLogging` | `bool` | `false` | Log each zone query. |

### `DnsblCheckResult`

```csharp
public sealed record DnsblCheckResult
{
    public string IpAddress { get; init; }
    public bool IsBlacklisted { get; init; }
    public string? MatchedService { get; init; }   // the zone that matched, or null
    public IpAddressType AddressType { get; init; } // IPv4 | IPv6 | Unknown
    public string? ErrorMessage { get; init; }      // non-null when the check itself failed
    public DateTime CheckedAt { get; init; }        // UTC
}
```

The record exposes `Blacklisted`, `NotBlacklisted`, and `Error` factory methods used internally to build each outcome.

## GeoIP

`GeoIpService` resolves an address against a MaxMind `.mmdb` database. Because the service requires a database, **always guard access with `HasGeoIp`** — reading `net.GeoIp` when no database is loaded throws.

```csharp
if (net.HasGeoIp)
{
    IpLocationResult geo = await net.GeoIp.LookupIpAsync("203.0.113.1");
    if (geo.IsSuccessful)
        Console.WriteLine($"{geo.CityName}, {geo.CountryName} ({geo.CountryCode}) " +
                          $"[{geo.Latitude}, {geo.Longitude}] {geo.TimeZone}");
}
```

### Where the database comes from

On startup the library calls `GeoIpService.InitializeAsync`, which loads (or downloads) the database and throws if none can be obtained — leaving `HasGeoIp` `false`. The database path is:

- `GeoIp.DatabasePath` if set, otherwise
- `{dataDir}/geoip/{DatabaseType}.mmdb` (default `GeoLite2-City.mmdb`).

When `GeoIp.AutoUpdate` is `true`, `DownloadDatabaseAsync` runs during initialization. It is a **no-op** if `AccountId` is `0` or `LicenseKey` is empty. Otherwise it downloads the `tar.gz` from `DownloadUrl` using HTTP Basic Auth (`AccountId` : `LicenseKey`) and extracts the `.mmdb` to the resolved path.

To download automatically, set your MaxMind credentials:

```json
{
  "GeoIp": {
    "Enabled": true,
    "AutoUpdate": true,
    "AccountId": 123456,
    "LicenseKey": "your-maxmind-license-key",
    "DatabaseType": "GeoLite2-City"
  }
}
```

### `IpLocationResult`

```csharp
public sealed record IpLocationResult
{
    public string IpAddress { get; init; }
    public string? CountryName { get; init; }
    public string? CountryCode { get; init; }     // ISO alpha-2
    public string? CityName { get; init; }
    public string? SubdivisionName { get; init; }
    public string? PostalCode { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? TimeZone { get; init; }         // IANA, e.g. "Europe/Copenhagen"
    public string? Isp { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsSuccessful { get; }              // ErrorMessage == null && CountryCode set
    public DateTime LookedUpAt { get; init; }      // UTC
}
```

`IsSuccessful` is the canonical success test — it is `true` only when there is no error **and** a country code was resolved. The record exposes `NotFound` and `Error` factory methods for the failure outcomes.

## Configuration

The library writes `config.netutils.json` (section `netutils`) with defaults on first run.

```json
{
  "Enabled": true,
  "Dnsbl": {
    "Enabled": true,
    "Ipv4Services": ["zen.spamhaus.org", "dnsbl.sorbs.net"],
    "Ipv4FallbackServices": ["b.barracudacentral.org"],
    "Ipv6Services": ["zen.spamhaus.org", "dnsbl6.sorbs.net"],
    "Ipv6FallbackServices": ["v6.b.barracudacentral.org"],
    "TimeoutSeconds": 5,
    "ParallelQueries": true,
    "DetailedLogging": false
  },
  "GeoIp": {
    "Enabled": true,
    "DatabasePath": "",
    "AutoUpdate": false,
    "AccountId": 0,
    "LicenseKey": "",
    "DownloadUrl": "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz",
    "DatabaseType": "GeoLite2-City",
    "TimeoutSeconds": 30
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the services aren't created and health reports *disabled*. |
| `Dnsbl.Enabled` | `bool` | `true` | Enable the DNSBL checker. |
| `Dnsbl.Ipv4Services` | `string[]` | `["zen.spamhaus.org","dnsbl.sorbs.net"]` | Primary IPv4 blacklist zones. |
| `Dnsbl.Ipv4FallbackServices` | `string[]` | `["b.barracudacentral.org"]` | IPv4 zones tried when primaries fail. |
| `Dnsbl.Ipv6Services` | `string[]` | `["zen.spamhaus.org","dnsbl6.sorbs.net"]` | Primary IPv6 blacklist zones. |
| `Dnsbl.Ipv6FallbackServices` | `string[]` | `["v6.b.barracudacentral.org"]` | IPv6 fallback zones. |
| `Dnsbl.TimeoutSeconds` | `int` | `5` | Per-check DNS timeout in seconds. |
| `Dnsbl.ParallelQueries` | `bool` | `true` | Query zones concurrently rather than sequentially. |
| `Dnsbl.DetailedLogging` | `bool` | `false` | Log each individual zone query. |
| `GeoIp.Enabled` | `bool` | `true` | Enable the GeoIP service. |
| `GeoIp.DatabasePath` | `string` | `""` | Explicit `.mmdb` path; blank auto-resolves to `{dataDir}/geoip/{DatabaseType}.mmdb`. |
| `GeoIp.AutoUpdate` | `bool` | `false` | Download/refresh the database on startup. |
| `GeoIp.AccountId` | `int` | `0` | MaxMind account id; required for download. |
| `GeoIp.LicenseKey` | `string` | `""` | MaxMind license key (secret); required for download. |
| `GeoIp.DownloadUrl` | `string` | MaxMind GeoLite2-City | Download endpoint returning a `tar.gz`. |
| `GeoIp.DatabaseType` | `string` | `GeoLite2-City` | Database/edition id; also the default file name. |
| `GeoIp.TimeoutSeconds` | `int` | `30` | Download timeout in seconds. |

## Health check

`HealthCheckAsync()` reports the library status, with a message such as `DNSBL on, GeoIP not configured`.

| Situation | Status |
|-----------|--------|
| `Enabled` is `false` | Healthy (*disabled*) |
| A service is switched off, or GeoIP has neither a database file nor MaxMind credentials (*not configured*) | Healthy |
| GeoIP is set up (database file or credentials) but failed to start — download failed, corrupt file | Degraded (`GeoIP failed: <reason>`) |
| Called before initialization | Unhealthy |

`HasGeoIp` tells callers whether lookups are available.

```csharp
var status = await net.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.NetUtils)
