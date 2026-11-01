using CodeLogic.Core.Configuration;

namespace CL.NetUtils.Models;

/// <summary>
/// Main configuration model for the CL.NetUtils library.
/// Serialized as <c>config.netutils.json</c> in the library's config directory.
/// </summary>
[ConfigSection("netutils")]
public class NetUtilsConfig : ConfigModelBase
{
    /// <summary>Whether the NetUtils library is enabled.</summary>
    [ConfigField(Label = "Enabled", Description = "Master switch for NetUtils (DNSBL + GeoIP).", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>DNSBL blacklist-checking configuration.</summary>
    public DnsblConfig Dnsbl { get; set; } = new();

    /// <summary>IP geolocation (GeoIP) configuration.</summary>
    public GeoIpConfig GeoIp { get; set; } = new();

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Dnsbl.TimeoutSeconds <= 0)
            errors.Add("DNSBL timeout must be > 0");

        if (!GeoIpConfig.IsValidDatabaseType(GeoIp.DatabaseType))
            errors.Add("GeoIp.DatabaseType must be a MaxMind edition id: letters, digits, '-', '_' or '.', at most 64 characters (e.g. GeoLite2-City)");
        else if (!GeoIp.TryGetTrustedDownloadUri(out _, out var urlError))
            errors.Add(urlError);

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Configuration for DNS blacklist (DNSBL) checking.
/// </summary>
public class DnsblConfig
{
    /// <summary>Whether DNSBL checking is enabled.</summary>
    [ConfigField(Label = "DNSBL Enabled", Group = "DNSBL", Order = 10)]
    public bool Enabled { get; set; } = true;

    /// <summary>Primary DNSBL services to query for IPv4 addresses.</summary>
    [ConfigField(Label = "IPv4 Services", InputType = ConfigInputType.Textarea,
        Description = "One DNSBL zone per line. Queried first for IPv4 addresses.",
        Group = "DNSBL", Order = 11)]
    public List<string> Ipv4Services { get; set; } = ["zen.spamhaus.org", "dnsbl.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv4 addresses.</summary>
    [ConfigField(Label = "IPv4 Fallback Services", InputType = ConfigInputType.Textarea,
        Description = "Consulted only when primary services don't return a match.",
        Group = "DNSBL", Order = 12, Collapsed = true)]
    public List<string> Ipv4FallbackServices { get; set; } = ["b.barracudacentral.org"];

    /// <summary>Primary DNSBL services to query for IPv6 addresses.</summary>
    [ConfigField(Label = "IPv6 Services", InputType = ConfigInputType.Textarea,
        Description = "One DNSBL zone per line. Queried first for IPv6 addresses.",
        Group = "DNSBL", Order = 13)]
    public List<string> Ipv6Services { get; set; } = ["zen.spamhaus.org", "dnsbl6.sorbs.net"];

    /// <summary>Fallback DNSBL services for IPv6 addresses.</summary>
    [ConfigField(Label = "IPv6 Fallback Services", InputType = ConfigInputType.Textarea,
        Group = "DNSBL", Order = 14, Collapsed = true)]
    public List<string> Ipv6FallbackServices { get; set; } = ["v6.b.barracudacentral.org"];

    /// <summary>DNS query timeout in seconds.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 60, Group = "DNSBL", Order = 15)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Whether to query the zones of a list concurrently (primaries together, then fallbacks together).</summary>
    [ConfigField(Label = "Parallel Queries", Description = "Query the zones of each list concurrently. Off: one at a time, stopping at the first listing.",
        Group = "DNSBL", Order = 16, Collapsed = true)]
    public bool ParallelQueries { get; set; } = true;

    /// <summary>Whether to enable detailed per-service logging.</summary>
    [ConfigField(Label = "Detailed Logging", Description = "Log every zone lookup — noisy.",
        Group = "DNSBL", Order = 17, Collapsed = true)]
    public bool DetailedLogging { get; set; } = false;
}

/// <summary>
/// Configuration for IP geolocation using the MaxMind GeoIP2 database.
/// </summary>
public class GeoIpConfig
{
    /// <summary>Whether the GeoIP service is enabled.</summary>
    [ConfigField(Label = "GeoIP Enabled", Group = "GeoIP", Order = 20)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Explicit path to the <c>.mmdb</c> database file.
    /// When empty the path is resolved automatically from the library data directory.
    /// </summary>
    [ConfigField(Label = "Database Path", Description = "Override the .mmdb path. Blank = use library data directory.",
        RequiresRestart = true, Group = "GeoIP", Order = 21)]
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Refresh the database on startup when credentials are set. A missing database is downloaded regardless; a failed
    /// refresh keeps the existing one.
    /// </summary>
    [ConfigField(Label = "Auto Update", Description = "Refresh the GeoIP database on startup (a missing database is always downloaded).",
        Group = "GeoIP", Order = 22)]
    public bool AutoUpdate { get; set; } = false;

    /// <summary>MaxMind account ID used for authenticated downloads.</summary>
    [ConfigField(Label = "MaxMind Account ID", Min = 0,
        Description = "Required for authenticated downloads.",
        Group = "GeoIP", Order = 23, Collapsed = true)]
    public int AccountId { get; set; } = 0;

    /// <summary>MaxMind license key used for authenticated downloads.</summary>
    [ConfigField(Label = "MaxMind License Key", InputType = ConfigInputType.Password, Secret = true,
        Description = "Required for authenticated GeoIP downloads.",
        Group = "GeoIP", Order = 24, Collapsed = true)]
    public string LicenseKey { get; set; } = string.Empty;

    /// <summary>The placeholder in <see cref="DownloadUrl"/> that is replaced with <see cref="DatabaseType"/>.</summary>
    public const string DatabaseTypePlaceholder = "{DatabaseType}";

    /// <summary>
    /// URL used to download the GeoIP database archive. The MaxMind credentials are sent to it, so it must be
    /// <c>https</c> and its host must be listed in <see cref="TrustedDownloadHosts"/>. <c>{DatabaseType}</c> is replaced
    /// with <see cref="DatabaseType"/>, so the default URL downloads whichever edition is configured.
    /// </summary>
    [ConfigField(Label = "Download URL", InputType = ConfigInputType.Url,
        Description = "Must be https and on a trusted host: the MaxMind credentials are sent to it.",
        Group = "GeoIP", Order = 25, Collapsed = true)]
    public string DownloadUrl { get; set; } =
        "https://download.maxmind.com/geoip/databases/{DatabaseType}/download?suffix=tar.gz";

    /// <summary>
    /// Hosts that <see cref="DownloadUrl"/> may point at (exact, case-insensitive host names). Default: MaxMind's
    /// download host only. Add a host only if you run a mirror you trust with your MaxMind license key.
    /// </summary>
    [ConfigField(Label = "Trusted Download Hosts", InputType = ConfigInputType.Textarea,
        Description = "One host per line. The download URL must use one of these hosts.",
        Group = "GeoIP", Order = 26, Collapsed = true)]
    public List<string> TrustedDownloadHosts { get; set; } = ["download.maxmind.com"];

    /// <summary>
    /// MaxMind edition id: a City, Country, Enterprise, ASN or ISP edition such as <c>GeoLite2-City</c>,
    /// <c>GeoLite2-Country</c>, <c>GeoLite2-ASN</c>, <c>GeoIP2-City</c>, <c>GeoIP2-Enterprise</c> or <c>GeoIP2-ISP</c>.
    /// It names the default database file (<c>{DatabaseType}.mmdb</c>), fills <c>{DatabaseType}</c> in
    /// <see cref="DownloadUrl"/>, and a downloaded database must be of the same kind.
    /// </summary>
    [ConfigField(Label = "Database Type", Description = "MaxMind edition id, e.g. GeoLite2-City, GeoLite2-Country or GeoLite2-ASN.",
        Group = "GeoIP", Order = 27, Collapsed = true)]
    public string DatabaseType { get; set; } = "GeoLite2-City";

    /// <summary>
    /// Whether <paramref name="databaseType"/> can be an edition id: 1 to 64 letters, digits, <c>-</c>, <c>_</c> or
    /// <c>.</c>, not starting with <c>.</c>. It becomes a file name and a URL path segment, so separators and
    /// <c>..</c> are refused.
    /// </summary>
    public static bool IsValidDatabaseType(string? databaseType) =>
        !string.IsNullOrEmpty(databaseType)
        && databaseType.Length <= 64
        && databaseType[0] != '.'
        && !databaseType.Contains("..", StringComparison.Ordinal)
        && databaseType.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>HTTP download timeout in seconds.</summary>
    [ConfigField(Label = "Download Timeout (s)", Min = 1, Max = 600,
        Group = "GeoIP", Order = 28, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Checks that <see cref="DownloadUrl"/> is an absolute <c>https</c> URL whose host is in
    /// <see cref="TrustedDownloadHosts"/>.
    /// </summary>
    /// <param name="uri">The parsed URL when it is trusted.</param>
    /// <param name="error">Why the URL is rejected; empty when it is trusted.</param>
    public bool TryGetTrustedDownloadUri(out Uri? uri, out string error)
    {
        uri = null;
        var url = DownloadUrl ?? string.Empty;
        if (url.Contains(DatabaseTypePlaceholder, StringComparison.Ordinal))
        {
            if (!IsValidDatabaseType(DatabaseType))
            {
                error = "GeoIp.DatabaseType is not a valid edition id, so it cannot fill {DatabaseType} in GeoIp.DownloadUrl";
                return false;
            }
            url = url.Replace(DatabaseTypePlaceholder, Uri.EscapeDataString(DatabaseType), StringComparison.Ordinal);
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = "GeoIp.DownloadUrl must be an absolute URL";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "GeoIp.DownloadUrl must use https (the MaxMind credentials are sent to it)";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "GeoIp.DownloadUrl must not contain user info";
            return false;
        }
        var trusted = TrustedDownloadHosts ?? [];
        if (!trusted.Any(h => string.Equals(h?.Trim().TrimEnd('.'), parsed.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
        {
            error = $"GeoIp.DownloadUrl host '{parsed.Host}' is not in GeoIp.TrustedDownloadHosts";
            return false;
        }
        uri = parsed;
        error = string.Empty;
        return true;
    }
}
