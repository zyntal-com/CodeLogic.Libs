using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using System.Formats.Tar;
using System.IO.Compression;

namespace CL.NetUtils.Services;

internal static class GeoIpDatabaseKinds
{
    /// <summary>
    /// The kind of a MaxMind <c>database_type</c> or edition id (<c>GeoLite2-City</c>, <c>GeoIP2-Country</c>,
    /// <c>GeoIP2-Enterprise</c>, <c>GeoLite2-ASN</c>, <c>GeoIP2-ISP</c>, <c>dbip-city-lite</c>, ...), or
    /// <see langword="null"/> for an edition that has no location data (Anonymous-IP, Connection-Type, Domain) or an
    /// unknown one.
    /// </summary>
    internal static GeoIpDatabaseKind? Detect(string? databaseType)
    {
        if (string.IsNullOrWhiteSpace(databaseType)) return null;
        var tokens = databaseType.Split(['-', '_', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        bool Has(string token) => tokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));
        if (Has("Enterprise")) return GeoIpDatabaseKind.Enterprise;
        if (Has("City")) return GeoIpDatabaseKind.City;
        if (Has("Country")) return GeoIpDatabaseKind.Country;
        if (Has("ISP")) return GeoIpDatabaseKind.Isp;
        if (Has("ASN")) return GeoIpDatabaseKind.Asn;
        return null;
    }
}
