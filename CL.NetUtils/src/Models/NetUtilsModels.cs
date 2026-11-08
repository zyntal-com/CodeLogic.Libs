namespace CL.NetUtils.Models;

/// <summary>
/// IP address protocol family.
/// </summary>
public enum IpAddressType
{
    /// <summary>IPv4 address (e.g. 192.168.1.1).</summary>
    IPv4,

    /// <summary>IPv6 address (e.g. 2001:db8::1).</summary>
    IPv6,

    /// <summary>Address type could not be determined.</summary>
    Unknown
}

/// <summary>
/// Verdict of a DNSBL check that ran. A check that could not run (invalid address, DNSBL checking switched off,
/// cancelled, unexpected failure) has no status: it is an error, see <see cref="NetUtilsErrorCodes"/>.
/// </summary>
public enum DnsblCheckStatus
{
    /// <summary>Every queried zone answered and none lists the address (or the address was skipped as local/allowlisted).</summary>
    NotBlacklisted,

    /// <summary>At least one zone lists the address.</summary>
    Blacklisted,

    /// <summary>
    /// No zone lists the address, but at least one zone could not be checked (timeout, resolver failure, or a
    /// DNSBL error answer such as Spamhaus' <c>127.255.255.x</c>). The address is not known to be clean.
    /// </summary>
    Indeterminate
}

/// <summary>
/// A completed DNSBL check of one IP address: the success value of <c>DnsblChecker.CheckIpAsync</c>.
/// </summary>
public sealed record DnsblCheck
{
    /// <summary>The IP address that was checked, as the caller spelled it.</summary>
    public required string IpAddress { get; init; }

    /// <summary>Protocol family of the checked address (an IPv4-mapped IPv6 address counts as IPv4).</summary>
    public required IpAddressType AddressType { get; init; }

    /// <summary>The verdict. Distinguishes a clean result from one where zones could not be checked.</summary>
    public required DnsblCheckStatus Status { get; init; }

    /// <summary>Whether at least one zone lists the address (<see cref="Status"/> is <see cref="DnsblCheckStatus.Blacklisted"/>).</summary>
    public bool IsBlacklisted => Status == DnsblCheckStatus.Blacklisted;

    /// <summary>The DNSBL zone that lists the address, or <see langword="null"/> when not blacklisted.</summary>
    public string? MatchedService { get; init; }

    /// <summary>Zones that could not be checked (timeout, resolver failure, DNSBL error answer). Empty when every zone answered.</summary>
    public IReadOnlyList<string> FailedServices { get; init; } = [];

    /// <summary>UTC timestamp of when the check was performed.</summary>
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;

    internal static DnsblCheck Blacklisted(string ip, IpAddressType type, string service, IReadOnlyList<string> failedServices) =>
        new() { IpAddress = ip, AddressType = type, Status = DnsblCheckStatus.Blacklisted, MatchedService = service, FailedServices = failedServices };

    internal static DnsblCheck NotBlacklisted(string ip, IpAddressType type) =>
        new() { IpAddress = ip, AddressType = type, Status = DnsblCheckStatus.NotBlacklisted };

    internal static DnsblCheck Indeterminate(string ip, IpAddressType type, IReadOnlyList<string> failedServices) =>
        new() { IpAddress = ip, AddressType = type, Status = DnsblCheckStatus.Indeterminate, FailedServices = failedServices };
}

/// <summary>
/// Geolocation data for one IP address: the success value of <c>GeoIpService.LookupIpAsync</c>. Which fields are
/// filled depends on the database edition (<c>GeoIpService.LoadedDatabaseType</c>).
/// </summary>
public sealed record IpLocation
{
    /// <summary>The IP address that was looked up, as the caller spelled it.</summary>
    public required string IpAddress { get; init; }

    /// <summary>Full country name, or <see langword="null"/> when unavailable.</summary>
    public string? CountryName { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code, or <see langword="null"/> when unavailable (ASN and ISP databases).</summary>
    public string? CountryCode { get; init; }

    /// <summary>City name, or <see langword="null"/> when unavailable.</summary>
    public string? CityName { get; init; }

    /// <summary>State / province / subdivision name, or <see langword="null"/> when unavailable.</summary>
    public string? SubdivisionName { get; init; }

    /// <summary>Postal / ZIP code, or <see langword="null"/> when unavailable.</summary>
    public string? PostalCode { get; init; }

    /// <summary>Geographic latitude, or <see langword="null"/> when unavailable.</summary>
    public double? Latitude { get; init; }

    /// <summary>Geographic longitude, or <see langword="null"/> when unavailable.</summary>
    public double? Longitude { get; init; }

    /// <summary>IANA time zone identifier (e.g. <c>Europe/Copenhagen</c>), or <see langword="null"/>.</summary>
    public string? TimeZone { get; init; }

    /// <summary>ISP / organization name, or <see langword="null"/> when unavailable (filled by ISP and Enterprise databases).</summary>
    public string? Isp { get; init; }

    /// <summary>Autonomous system number, or <see langword="null"/> when unavailable (filled by ASN, ISP and Enterprise databases).</summary>
    public long? AutonomousSystemNumber { get; init; }

    /// <summary>Organization of the autonomous system, or <see langword="null"/> when unavailable (ASN, ISP and Enterprise databases).</summary>
    public string? AutonomousSystemOrganization { get; init; }

    /// <summary>UTC timestamp of when the lookup was performed.</summary>
    public DateTime LookedUpAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the entry holds usable data: a country code (City, Country and Enterprise databases) or, for ASN and
    /// ISP databases, which have no country, an autonomous system number or ISP. A lookup whose entry has none is
    /// reported as <see cref="NetUtilsErrorCodes.LocationNotFound"/>.
    /// </summary>
    internal bool HasData =>
        !string.IsNullOrEmpty(CountryCode) || AutonomousSystemNumber is not null || !string.IsNullOrEmpty(Isp);
}
