using CodeLogic.Core.Results;

namespace CL.NetUtils.Models;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.NetUtils reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant.
/// </summary>
public static class NetUtilsErrorCodes
{
    /// <summary>The text given to a DNSBL check or a GeoIP lookup is not an IP address. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidIpAddress = "netutils.invalid_ip_address";

    /// <summary>
    /// DNSBL checking is switched off (<c>netutils.enabled</c> or <c>netutils.dnsbl.enabled</c> is <see langword="false"/>),
    /// so no zone was queried. Kind: <see cref="UnavailableError"/>.
    /// </summary>
    public const string DnsblDisabled = "netutils.dnsbl_disabled";

    /// <summary>
    /// A DNSBL check failed on an unexpected exception (for example a throwing allowlist predicate). Kind:
    /// <see cref="InternalError"/>. The message is generic and <see cref="Error.Details"/> is the exception type; the
    /// exception itself is logged, never returned.
    /// </summary>
    public const string DnsblCheckFailed = "netutils.dnsbl_check_failed";

    /// <summary>The GeoIP service has no open database (it was not initialized or has been disposed). Kind: <see cref="UnavailableError"/>.</summary>
    public const string GeoIpUnavailable = "netutils.geoip_unavailable";

    /// <summary>
    /// The GeoIP database has no entry for the address, or the entry holds no usable data (no country code, and for
    /// ASN and ISP databases no autonomous system number or ISP). Kind: <see cref="NotFoundError"/>.
    /// </summary>
    public const string LocationNotFound = "netutils.location_not_found";

    /// <summary>
    /// A GeoIP lookup failed inside the MaxMind reader (corrupt database, unexpected exception). Kind:
    /// <see cref="InternalError"/>. The message is generic and <see cref="Error.Details"/> is the exception type.
    /// </summary>
    public const string GeoIpLookupFailed = "netutils.geoip_lookup_failed";

    /// <summary>A GeoIP download that was not attempted: no credentials, an untrusted URL, or an invalid limit. Kind: <see cref="ValidationError"/>.</summary>
    public const string DownloadRefused = "netutils.download_refused";

    /// <summary>A GeoIP download that failed on the network or with an HTTP error. Kind: <see cref="UnavailableError"/>.</summary>
    public const string DownloadFailed = "netutils.download_failed";

    /// <summary>A GeoIP download that did not finish within <c>GeoIp.TimeoutSeconds</c>. Kind: <see cref="TimeoutError"/>.</summary>
    public const string DownloadTimeout = "netutils.download_timeout";

    /// <summary>A GeoIP download whose body, or whose unpacked database, exceeds <c>GeoIp.MaxDownloadBytes</c>. Kind: <see cref="ValidationError"/>.</summary>
    public const string DownloadTooLarge = "netutils.download_too_large";

    /// <summary>A GeoIP download whose published SHA-256 could not be fetched or read. Kind: <see cref="UnavailableError"/>.</summary>
    public const string ChecksumUnavailable = "netutils.checksum_unavailable";

    /// <summary>A GeoIP download whose SHA-256 does not match the published one. Kind: <see cref="ValidationError"/>.</summary>
    public const string ChecksumMismatch = "netutils.checksum_mismatch";

    /// <summary>A downloaded archive that holds no usable database of the configured kind. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidDatabase = "netutils.invalid_database";

    /// <summary>
    /// The operation was cancelled by the caller (a DNSBL check or a GeoIP download). Nothing is known about the
    /// address. Kind: <see cref="UnavailableError"/>.
    /// </summary>
    public const string Cancelled = "netutils.cancelled";
}

/// <summary>Builds CL.NetUtils errors so every code is always paired with the same kind.</summary>
internal static class NetUtilsErrors
{
    public static Error InvalidIpAddress(string message) => Error.Validation(NetUtilsErrorCodes.InvalidIpAddress, message);

    public static Error DnsblDisabled(string message) => Error.Unavailable(NetUtilsErrorCodes.DnsblDisabled, message);

    /// <summary>An unexpected DNSBL failure: a fixed message, and only the exception's type name as details.</summary>
    public static Error DnsblCheckFailed(string message, Exception ex) =>
        Error.Internal(NetUtilsErrorCodes.DnsblCheckFailed, message, ex.GetType().Name);

    public static Error GeoIpUnavailable(string message) => Error.Unavailable(NetUtilsErrorCodes.GeoIpUnavailable, message);

    public static Error LocationNotFound(string message) => Error.NotFound(NetUtilsErrorCodes.LocationNotFound, message);

    /// <summary>An unexpected GeoIP failure: a fixed message, and only the exception's type name as details.</summary>
    public static Error GeoIpLookupFailed(string message, Exception ex) =>
        Error.Internal(NetUtilsErrorCodes.GeoIpLookupFailed, message, ex.GetType().Name);

    public static Error DownloadRefused(string message) => Error.Validation(NetUtilsErrorCodes.DownloadRefused, message);

    public static Error DownloadFailed(string message, string details = "") => Error.Unavailable(NetUtilsErrorCodes.DownloadFailed, message, details);

    public static Error DownloadTimeout(string message) => Error.Timeout(NetUtilsErrorCodes.DownloadTimeout, message);

    public static Error DownloadTooLarge(string message) => Error.Validation(NetUtilsErrorCodes.DownloadTooLarge, message);

    public static Error ChecksumUnavailable(string message) => Error.Unavailable(NetUtilsErrorCodes.ChecksumUnavailable, message);

    public static Error ChecksumMismatch(string message) => Error.Validation(NetUtilsErrorCodes.ChecksumMismatch, message);

    public static Error InvalidDatabase(string message) => Error.Validation(NetUtilsErrorCodes.InvalidDatabase, message);

    /// <summary>The caller cancelled; mapped to an unavailable error as the other CodeLogic libraries do.</summary>
    public static Error Cancelled(string message) => Error.Unavailable(NetUtilsErrorCodes.Cancelled, message);
}
