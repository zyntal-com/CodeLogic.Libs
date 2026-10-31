# CL.NetUtils

> DNSBL blacklist checking and MaxMind GeoIP geolocation — screen abusive IPs and locate visitors with one library.

`CL.NetUtils` adds two network-reputation tools to a CodeLogic application behind a single `NetUtilsLibrary` surface. The **DNSBL checker** queries blacklist zones (IPv4 and IPv6, with primary and fallback providers) to decide whether an address is listed as a known abuser. The **GeoIP service** resolves an address to a country, city, coordinates, and time zone using a [MaxMind GeoLite2/GeoIP2](https://www.maxmind.com/) `.mmdb` database, which can be supplied on disk or auto-downloaded with your MaxMind credentials.

There is no standalone DNS-lookup service — DNS resolution is an internal detail of the DNSBL checker.

| | |
|---|---|
| **Package** | [`CodeLogic.NetUtils`](https://www.nuget.org/packages/CodeLogic.NetUtils) |
| **Library class** | `CL.NetUtils.NetUtilsLibrary` |
| **Config file** | `config.netutils.json` (section `netutils`) |
| **Dependencies** | MaxMind.GeoIP2 6.x |

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

switch (result.Status)
{
    case DnsblCheckStatus.Blacklisted:
        Console.WriteLine($"Listed by {result.MatchedService}");
        break;
    case DnsblCheckStatus.Indeterminate:
        // No zone that answered lists it, but these zones could not be checked.
        Console.WriteLine($"Unknown: {string.Join(", ", result.FailedServices)} did not answer");
        break;
    case DnsblCheckStatus.Error:
        Console.WriteLine($"Check failed: {result.ErrorMessage}");
        break;
    case DnsblCheckStatus.NotBlacklisted:
        Console.WriteLine("Clean.");
        break;
}
```

The default blacklist zones are:

| Address family | Primary | Fallback |
|----------------|---------|----------|
| IPv4 | `zen.spamhaus.org`, `dnsbl.sorbs.net` | `b.barracudacentral.org` |
| IPv6 | `zen.spamhaus.org`, `dnsbl6.sorbs.net` | `v6.b.barracudacentral.org` |

The checker picks the IPv4 or IPv6 zone set based on the address it is given, then:

1. Queries the primary zones. With `ParallelQueries` (the default) they are queried concurrently and every zone's answer is collected; with `ParallelQueries` off they are queried one at a time and the check stops at the first listing.
2. Queries the fallback zones the same way, but only when no primary zone lists the address (whether the primaries answered or failed).
3. Reports `Blacklisted` with the first listing zone (in list order) if any zone lists the address. Otherwise, if any zone could not be checked, it reports `Indeterminate` with those zones in `FailedServices`; only when every queried zone answered "not listed" is the result `NotBlacklisted`.

A zone counts as answered "not listed" on NXDOMAIN or no A record, or when it only returns addresses outside `127.0.0.0/8` (a parked or wildcarded dead zone). It counts as failed when the lookup times out (`TimeoutSeconds`, per zone), the resolver fails (SERVFAIL, network error), or it returns a DNSBL error code such as Spamhaus' `127.255.255.x` (public resolver blocked, query limit). Any other `127.x.x.x` answer is a listing.

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
| `ParallelQueries` | `bool` | `true` | Query the zones of a list concurrently; `false` queries them one at a time and stops at the first listing. |
| `DetailedLogging` | `bool` | `false` | Log each zone query. |

### `DnsblCheckResult`

```csharp
public sealed record DnsblCheckResult
{
    public string IpAddress { get; init; }
    public DnsblCheckStatus Status { get; init; }   // NotBlacklisted | Blacklisted | Indeterminate | Error
    public bool IsBlacklisted { get; init; }
    public string? MatchedService { get; init; }   // the zone that matched, or null
    public IReadOnlyList<string> FailedServices { get; init; } // zones that could not be checked
    public IpAddressType AddressType { get; init; } // IPv4 | IPv6 | Unknown
    public string? ErrorMessage { get; init; }      // set for Error and Indeterminate
    public DateTime CheckedAt { get; init; }        // UTC
}
```

The record exposes `Blacklisted`, `NotBlacklisted`, `Indeterminate`, and `Error` factory methods used internally to build each outcome. `IsBlacklisted` is `false` for `Indeterminate`, so code that only checks `IsBlacklisted` treats an unchecked address as allowed; check `Status` to decide yourself.

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

When `GeoIp.AutoUpdate` is `true`, `DownloadDatabaseAsync` runs during initialization. It is a **no-op** if `AccountId` is `0` or `LicenseKey` is empty. Otherwise it downloads the archive from `DownloadUrl` using HTTP Basic Auth (`AccountId` : `LicenseKey`) and installs the `.mmdb` at the resolved path.

- **Trusted URL only.** The credentials go to `DownloadUrl`, so it must be an `https` URL whose host is listed in `TrustedDownloadHosts` (default: `download.maxmind.com` only). Anything else is refused before a request is sent, and also fails configuration validation. Add a host only for a mirror you trust with the license key.
- **Selective, atomic install.** The archive (`tar.gz` or `zip`) is unpacked in a private temporary directory next to the database. Only the `.mmdb` entry (preferring `{DatabaseType}.mmdb`) is extracted, by content only, so licence files and entry paths never reach the data directory. The file must carry the MaxMind metadata marker; then it replaces the database in one rename, and an open reader is switched to it. A failed or invalid download leaves the existing database untouched.

To download automatically, set your MaxMind credentials:

```json
{
  "geoIp": {
    "enabled": true,
    "autoUpdate": true,
    "accountId": 123456,
    "licenseKey": "your-maxmind-license-key",
    "databaseType": "GeoLite2-City"
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

The library writes `config.netutils.json` (section `netutils`) with defaults on first run. Keys are camelCase and matched case-sensitively; a PascalCase key is ignored and its default stays active.

```json
{
  "enabled": true,
  "dnsbl": {
    "enabled": true,
    "ipv4Services": ["zen.spamhaus.org", "dnsbl.sorbs.net"],
    "ipv4FallbackServices": ["b.barracudacentral.org"],
    "ipv6Services": ["zen.spamhaus.org", "dnsbl6.sorbs.net"],
    "ipv6FallbackServices": ["v6.b.barracudacentral.org"],
    "timeoutSeconds": 5,
    "parallelQueries": true,
    "detailedLogging": false
  },
  "geoIp": {
    "enabled": true,
    "databasePath": "",
    "autoUpdate": false,
    "accountId": 0,
    "licenseKey": "",
    "downloadUrl": "https://download.maxmind.com/geoip/databases/GeoLite2-City/download?suffix=tar.gz",
    "trustedDownloadHosts": ["download.maxmind.com"],
    "databaseType": "GeoLite2-City",
    "timeoutSeconds": 30
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Master switch; when `false` the services aren't created and health reports *disabled*. |
| `Dnsbl.Enabled` | `bool` | `true` | Enable the DNSBL checker. |
| `Dnsbl.Ipv4Services` | `string[]` | `["zen.spamhaus.org","dnsbl.sorbs.net"]` | Primary IPv4 blacklist zones. |
| `Dnsbl.Ipv4FallbackServices` | `string[]` | `["b.barracudacentral.org"]` | IPv4 zones queried when no primary zone lists the address. |
| `Dnsbl.Ipv6Services` | `string[]` | `["zen.spamhaus.org","dnsbl6.sorbs.net"]` | Primary IPv6 blacklist zones. |
| `Dnsbl.Ipv6FallbackServices` | `string[]` | `["v6.b.barracudacentral.org"]` | IPv6 fallback zones. |
| `Dnsbl.TimeoutSeconds` | `int` | `5` | Per-zone DNS timeout in seconds; a zone that times out makes the result `Indeterminate` unless another zone lists the address. |
| `Dnsbl.ParallelQueries` | `bool` | `true` | Query the zones of a list concurrently; `false` queries them one at a time and stops at the first listing. |
| `Dnsbl.DetailedLogging` | `bool` | `false` | Log each individual zone query. |
| `GeoIp.Enabled` | `bool` | `true` | Enable the GeoIP service. |
| `GeoIp.DatabasePath` | `string` | `""` | Explicit `.mmdb` path; blank auto-resolves to `{dataDir}/geoip/{DatabaseType}.mmdb`. |
| `GeoIp.AutoUpdate` | `bool` | `false` | Download/refresh the database on startup. |
| `GeoIp.AccountId` | `int` | `0` | MaxMind account id; required for download. |
| `GeoIp.LicenseKey` | `string` | `""` | MaxMind license key (secret); required for download. |
| `GeoIp.DownloadUrl` | `string` | MaxMind GeoLite2-City | Download endpoint returning a `tar.gz` or `zip`. Must be `https` on a host in `TrustedDownloadHosts`. |
| `GeoIp.TrustedDownloadHosts` | `string[]` | `["download.maxmind.com"]` | Hosts `DownloadUrl` may use (exact, case-insensitive). |
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
