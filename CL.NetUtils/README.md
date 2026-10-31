# CodeLogic.NetUtils

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.NetUtils)](https://www.nuget.org/packages/CodeLogic.NetUtils)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

> DNSBL blacklist checking and MaxMind GeoIP geolocation for [CodeLogic 5](https://github.com/zyntal-com/CodeLogic) — screen abusive IPs and locate visitors with one library.

Bundles two network-reputation tools behind a single `NetUtilsLibrary`. The **DNSBL checker** queries multiple blacklist zones (IPv4 and IPv6, primary plus fallback) in parallel and short-circuits private/loopback addresses without a DNS query. The **GeoIP service** does city-level lookups against a [MaxMind GeoLite2/GeoIP2](https://www.maxmind.com/) `.mmdb` database — loaded from disk or auto-downloaded with your MaxMind credentials.

## Install

```bash
dotnet add package CodeLogic.NetUtils
```

## Quick start

```csharp
using CL.NetUtils;

await Libraries.LoadAsync<NetUtilsLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var net = Libraries.Get<NetUtilsLibrary>();

// 1. Is this IP on a blacklist? (uses the zones from config.netutils.json)
DnsblCheckResult check = await net.Dnsbl.CheckIpAsync("203.0.113.1");
if (check.IsBlacklisted)
    Console.WriteLine($"Listed by {check.MatchedService}");

// 2. Where is this IP? Guard with HasGeoIp — GeoIP needs a MaxMind database.
if (net.HasGeoIp)
{
    IpLocationResult geo = await net.GeoIp.LookupIpAsync("203.0.113.1");
    if (geo.IsSuccessful)
        Console.WriteLine($"{geo.CityName}, {geo.CountryName} ({geo.CountryCode})");
}
```

Local and private addresses (loopback, RFC 1918, IPv6 link/site-local and unique-local) are returned as **not blacklisted** without any DNS query.

## Features

- **DNSBL checking** — query multiple blacklist zones per address family with parallel lookups and primary/fallback providers.
- **Per-call overrides** — `CheckIpAsync(ip, DnsblCheckRequest)` supplies your own zones, timeout, and an async allowlist predicate.
- **Private-address short-circuit** — loopback and RFC 1918 / IPv6 local ranges skip DNS and return not blacklisted.
- **IP geolocation** — city-level country/city/coordinates/time-zone lookups via MaxMind GeoLite2/GeoIP2.
- **Auto-download** — fetch and extract the `.mmdb` on startup using your MaxMind `AccountId` + `LicenseKey`.
- **Own result records** — `DnsblCheckResult` and `IpLocationResult`, each with success/error factories.

## Configuration

Auto-generated on first run as `config.netutils.json` (section `netutils`):

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

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch for the library. |
| `Dnsbl.Enabled` | `true` | Enable the DNSBL checker. |
| `Dnsbl.Ipv4Services` | `["zen.spamhaus.org","dnsbl.sorbs.net"]` | Primary IPv4 blacklist zones. |
| `Dnsbl.Ipv4FallbackServices` | `["b.barracudacentral.org"]` | Tried when primaries fail. |
| `Dnsbl.Ipv6Services` | `["zen.spamhaus.org","dnsbl6.sorbs.net"]` | Primary IPv6 blacklist zones. |
| `Dnsbl.Ipv6FallbackServices` | `["v6.b.barracudacentral.org"]` | IPv6 fallback zones. |
| `Dnsbl.TimeoutSeconds` | `5` | Per-check DNS timeout. |
| `Dnsbl.ParallelQueries` | `true` | Query zones concurrently. |
| `Dnsbl.DetailedLogging` | `false` | Log each zone query. |
| `GeoIp.Enabled` | `true` | Enable the GeoIP service. |
| `GeoIp.DatabasePath` | `""` | Explicit `.mmdb` path; blank auto-resolves to `{dataDir}/geoip/{DatabaseType}.mmdb`. |
| `GeoIp.AutoUpdate` | `false` | Download/refresh the database on startup. |
| `GeoIp.AccountId` | `0` | MaxMind account id (required for download). |
| `GeoIp.LicenseKey` | `""` | MaxMind license key (secret; required for download). |
| `GeoIp.DownloadUrl` | MaxMind GeoLite2-City | Download endpoint (`tar.gz`). |
| `GeoIp.DatabaseType` | `GeoLite2-City` | Database/edition id. |
| `GeoIp.TimeoutSeconds` | `30` | Download timeout. |

## Documentation

Full guide: **[CL.NetUtils documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/netutils.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- MaxMind.GeoIP2 5.x · SharpCompress 0.x
- A MaxMind GeoLite2/GeoIP2 `.mmdb` for GeoIP (on disk, or auto-downloaded with credentials)

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
