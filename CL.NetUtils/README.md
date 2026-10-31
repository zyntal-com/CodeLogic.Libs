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
else if (check.Status == DnsblCheckStatus.Indeterminate)
    Console.WriteLine($"Not listed by the zones that answered; unchecked: {string.Join(", ", check.FailedServices)}");

// 2. Where is this IP? Guard with HasGeoIp — GeoIP needs a MaxMind database.
if (net.HasGeoIp)
{
    IpLocationResult geo = await net.GeoIp.LookupIpAsync("203.0.113.1");
    if (geo.IsSuccessful)
        Console.WriteLine($"{geo.CityName}, {geo.CountryName} ({geo.CountryCode})");
}
```

Local and private addresses (loopback, RFC 1918, link-local, CGNAT, documentation/reserved ranges, IPv6 link/site-local and unique-local) are returned as **not blacklisted** without any DNS query.

## Features

- **DNSBL checking** — query multiple blacklist zones per address family, concurrently or one at a time (`ParallelQueries`), with primary/fallback providers. A check where zones could not be queried is `Indeterminate`, never reported as clean.
- **Per-call overrides** — `CheckIpAsync(ip, DnsblCheckRequest)` supplies your own zones, timeout, and an async allowlist predicate.
- **Private-address short-circuit** — loopback and RFC 1918 / IPv6 local ranges skip DNS and return not blacklisted.
- **IP geolocation** — city-level country/city/coordinates/time-zone lookups via MaxMind GeoLite2/GeoIP2.
- **Auto-download** — fetch the `.mmdb` when it is missing, using your MaxMind `AccountId` + `LicenseKey`, only from an `https` URL on a trusted host; only the `.mmdb` is extracted and it replaces the database in one rename.
- **Own result records** — `DnsblCheckResult` and `IpLocationResult`, each with success/error factories.

## Configuration

Auto-generated on first run as `config.netutils.json` (section `netutils`). Keys are camelCase and matched case-sensitively, so a PascalCase key is ignored and its default stays active:

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

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch for the library. |
| `Dnsbl.Enabled` | `true` | Enable the DNSBL checker. |
| `Dnsbl.Ipv4Services` | `["zen.spamhaus.org","dnsbl.sorbs.net"]` | Primary IPv4 blacklist zones. |
| `Dnsbl.Ipv4FallbackServices` | `["b.barracudacentral.org"]` | Queried when no primary zone lists the address. |
| `Dnsbl.Ipv6Services` | `["zen.spamhaus.org","dnsbl6.sorbs.net"]` | Primary IPv6 blacklist zones. |
| `Dnsbl.Ipv6FallbackServices` | `["v6.b.barracudacentral.org"]` | IPv6 fallback zones. |
| `Dnsbl.TimeoutSeconds` | `5` | Per-zone DNS timeout; a zone that times out is reported in `FailedServices`. |
| `Dnsbl.ParallelQueries` | `true` | Query the zones of a list concurrently; `false` queries one at a time and stops at the first listing. |
| `Dnsbl.DetailedLogging` | `false` | Log each zone query. |
| `GeoIp.Enabled` | `true` | Enable the GeoIP service. |
| `GeoIp.DatabasePath` | `""` | Explicit `.mmdb` path; blank auto-resolves to `{dataDir}/geoip/{DatabaseType}.mmdb`. |
| `GeoIp.AutoUpdate` | `false` | Refresh the database on startup when credentials are set (a missing database is always downloaded; a failed refresh keeps the old one). |
| `GeoIp.AccountId` | `0` | MaxMind account id (required for download). |
| `GeoIp.LicenseKey` | `""` | MaxMind license key (secret; required for download). |
| `GeoIp.DownloadUrl` | MaxMind GeoLite2-City | Download endpoint (`tar.gz` or `zip`). Must be `https` on a host in `TrustedDownloadHosts`, because the credentials are sent to it. |
| `GeoIp.TrustedDownloadHosts` | `["download.maxmind.com"]` | Hosts `DownloadUrl` may use (exact match). |
| `GeoIp.DatabaseType` | `GeoLite2-City` | Database/edition id. |
| `GeoIp.TimeoutSeconds` | `30` | Download timeout. |

## Documentation

Full guide: **[CL.NetUtils documentation](https://zyntal-com.github.io/CodeLogic.Libs/libs/netutils.html)**

## Requirements

- [CodeLogic 5.0+](https://github.com/zyntal-com/CodeLogic) · .NET 11
- MaxMind.GeoIP2 6.x
- A MaxMind GeoLite2/GeoIP2 `.mmdb` for GeoIP (on disk, or auto-downloaded with credentials)

## License

MIT — see [LICENSE](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE).
