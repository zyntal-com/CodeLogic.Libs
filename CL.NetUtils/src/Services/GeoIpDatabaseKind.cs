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

/// <summary>The MaxMind database editions <see cref="GeoIpService"/> can serve.</summary>
internal enum GeoIpDatabaseKind
{
    City,
    Country,
    Enterprise,
    Asn,
    Isp
}
