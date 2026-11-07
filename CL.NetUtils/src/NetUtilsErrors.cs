using CodeLogic.Core.Results;

namespace CL.NetUtils;

/// <summary>Creates the errors CL.NetUtils reports, with stable <c>netutils.*</c> codes.</summary>
public static class NetUtilsErrors
{
    /// <summary>Stable code for a GeoIP download that was not attempted: no credentials, or an untrusted URL.</summary>
    public const string DownloadRefusedCode = "netutils.download_refused";
    /// <summary>Stable code for a GeoIP download that failed on the network or with an HTTP error.</summary>
    public const string DownloadFailedCode = "netutils.download_failed";
    /// <summary>Stable code for a GeoIP download that did not finish within <c>GeoIp.TimeoutSeconds</c>.</summary>
    public const string DownloadTimeoutCode = "netutils.download_timeout";
    /// <summary>Stable code for a GeoIP download whose body, or whose unpacked database, exceeds <c>GeoIp.MaxDownloadBytes</c>.</summary>
    public const string DownloadTooLargeCode = "netutils.download_too_large";
    /// <summary>Stable code for a GeoIP download whose published SHA-256 could not be fetched or read.</summary>
    public const string ChecksumUnavailableCode = "netutils.checksum_unavailable";
    /// <summary>Stable code for a GeoIP download whose SHA-256 does not match the published one.</summary>
    public const string ChecksumMismatchCode = "netutils.checksum_mismatch";
    /// <summary>Stable code for a downloaded archive that holds no usable database of the configured kind.</summary>
    public const string InvalidDatabaseCode = "netutils.invalid_database";
    /// <summary>Stable code for an operation cancelled by the caller.</summary>
    public const string CancelledCode = "netutils.cancelled";

    /// <summary>A download that was refused before any request (no credentials, untrusted URL, invalid limit).</summary>
    public static Error DownloadRefused(string message) => Error.Validation(DownloadRefusedCode, message);
    /// <summary>A download that failed on the network or with an HTTP status.</summary>
    public static Error DownloadFailed(string message, string details = "") => Error.Unavailable(DownloadFailedCode, message, details);
    /// <summary>A download that ran past <c>GeoIp.TimeoutSeconds</c>.</summary>
    public static Error DownloadTimeout(string message) => Error.Timeout(DownloadTimeoutCode, message);
    /// <summary>A download or its unpacked database over <c>GeoIp.MaxDownloadBytes</c>.</summary>
    public static Error DownloadTooLarge(string message) => Error.Validation(DownloadTooLargeCode, message);
    /// <summary>A published checksum that could not be obtained or parsed.</summary>
    public static Error ChecksumUnavailable(string message) => Error.Unavailable(ChecksumUnavailableCode, message);
    /// <summary>A download whose content does not match its published checksum.</summary>
    public static Error ChecksumMismatch(string message) => Error.Validation(ChecksumMismatchCode, message);
    /// <summary>An archive without a usable database of the expected kind.</summary>
    public static Error InvalidDatabase(string message) => Error.Validation(InvalidDatabaseCode, message);
    /// <summary>The caller cancelled; mapped to an unavailable error as the other CodeLogic libraries do.</summary>
    public static Error Cancelled(string message) => Error.Unavailable(CancelledCode, message);
}
