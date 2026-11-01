using CodeLogic.Core.Localization;

namespace CL.NetUtils.Localization;

/// <summary>
/// Localized string resources for the CL.NetUtils library: the library's log messages and the error messages it puts
/// in <see cref="Models.DnsblCheckResult.ErrorMessage"/> and <see cref="Models.IpLocationResult.ErrorMessage"/>.
/// Templates use <see cref="string.Format(string, object?[])"/> placeholders.
/// </summary>
[LocalizationSection("netutils")]
public class NetUtilsStrings : LocalizationModelBase
{
    [LocalizedString(Description = "Library initialized")]
    public string LibraryInitialized { get; set; } = "NetUtils library initialized";

    [LocalizedString(Description = "Library started")]
    public string LibraryStarted { get; set; } = "NetUtils library started";

    [LocalizedString(Description = "Library stopped")]
    public string LibraryStopped { get; set; } = "NetUtils library stopped";

    [LocalizedString(Description = "DNSBL check result: blacklisted. {0} = IP, {1} = zone")]
    public string IpBlacklisted { get; set; } = "IP {0} is blacklisted by {1}";

    [LocalizedString(Description = "DNSBL check result: clean. {0} = IP")]
    public string IpClean { get; set; } = "IP {0} is not blacklisted";

    [LocalizedString(Description = "GeoIP database not found")]
    public string GeoIpDatabaseNotFound { get; set; } = "GeoIP database not found, downloading...";

    [LocalizedString(Description = "GeoIP initialized. {0} = database path, {1} = database type")]
    public string GeoIpInitialized { get; set; } = "GeoIP service initialized with database: {0} ({1})";

    [LocalizedString(Description = "GeoIP unavailable. {0} = reason")]
    public string GeoIpUnavailable { get; set; } = "GeoIP service unavailable: {0}";

    [LocalizedString(Description = "Invalid IP address (log). {0} = the rejected input")]
    public string InvalidIpAddress { get; set; } = "Invalid IP address: {0}";

    [LocalizedString(Description = "Error message of a check or lookup given text that is not an IP address")]
    public string InvalidIpAddressFormat { get; set; } = "Invalid IP address format";

    [LocalizedString(Description = "Error message of a DNSBL check while DNSBL checking is disabled")]
    public string DnsblDisabled { get; set; } = "DNSBL checking is disabled";

    [LocalizedString(Description = "Error message of a GeoIP lookup before a database is open")]
    public string GeoIpNotInitialized { get; set; } = "GeoIP service not initialized";

    [LocalizedString(Description = "Error message of a GeoIP lookup for an address the database does not contain")]
    public string LocationNotFound { get; set; } = "Location not found";
}
