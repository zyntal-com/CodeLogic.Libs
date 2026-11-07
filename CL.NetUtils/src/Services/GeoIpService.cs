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

/// <summary>
/// Provides IP geolocation using the MaxMind GeoIP2 / GeoLite2 database.
/// Supports automatic download of the database when credentials are configured.
/// </summary>
public partial class GeoIpService : IDisposable
{
    private readonly GeoIpConfig _config;
    private readonly ILogger? _logger;
    private readonly string _databasePath;

    private DatabaseReader? _databaseReader;
    private GeoIpDatabaseKind _kind;
    private readonly Lock _lock = new();
    private readonly HttpMessageHandler? _httpHandler;

    /// <summary>Supplies the (localized) messages; set by <see cref="NetUtilsLibrary"/>.</summary>
    internal Func<NetUtilsStrings> Strings { get; set; } = static () => InputText.DefaultStrings;

    /// <summary>
    /// How long a reader replaced by a newer database stays open after the swap. Lookups take a snapshot of the
    /// reader outside the lock, so one that started just before a swap still finishes on the old reader.
    /// </summary>
    internal TimeSpan RetireGracePeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes a new <see cref="GeoIpService"/>.
    /// </summary>
    /// <param name="config">GeoIP configuration.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="dataDirectory">
    /// Library data directory used to resolve the database path when
    /// <see cref="GeoIpConfig.DatabasePath"/> is not set.
    /// </param>
    public GeoIpService(GeoIpConfig config, ILogger? logger = null, string dataDirectory = "")
        : this(config, logger, dataDirectory, httpHandler: null)
    {
    }

    /// <summary>Test seam: downloads through <paramref name="httpHandler"/>.</summary>
    internal GeoIpService(GeoIpConfig config, ILogger? logger, string dataDirectory, HttpMessageHandler? httpHandler)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
        _httpHandler = httpHandler;

        if (string.IsNullOrWhiteSpace(config.DatabasePath))
        {
            // DatabaseType becomes a file name; a value such as "../../x" must not choose where the file goes.
            if (!GeoIpConfig.IsValidDatabaseType(config.DatabaseType))
                throw new ArgumentException($"GeoIp.DatabaseType '{InputText.ForLog(config.DatabaseType)}' is not a valid edition id", nameof(config));
            _databasePath = Path.Combine(dataDirectory, "geoip", $"{config.DatabaseType}.mmdb");
        }
        else
        {
            _databasePath = config.DatabasePath;
        }
    }
}
