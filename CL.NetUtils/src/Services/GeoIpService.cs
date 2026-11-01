using System.Net;
using System.Net.Http.Headers;
using CL.NetUtils.Localization;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
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
public class GeoIpService : IDisposable
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

    /// <summary>
    /// The <c>database_type</c> of the open database (e.g. <c>GeoLite2-City</c>, <c>GeoLite2-Country</c>,
    /// <c>GeoLite2-ASN</c>), or <see langword="null"/> before <see cref="InitializeAsync"/>. It decides which
    /// <see cref="IpLocationResult"/> fields a lookup fills.
    /// </summary>
    public string? LoadedDatabaseType
    {
        get
        {
            lock (_lock)
                return _databaseReader?.Metadata.DatabaseType;
        }
    }

    /// <summary>The <c>.mmdb</c> file this service reads (from <see cref="GeoIpConfig.DatabasePath"/> or the library data directory).</summary>
    public string DatabasePath => _databasePath;

    /// <summary>English description of the database the open reader serves, or null before initialization (tests).</summary>
    internal string? OpenDatabaseDescription
    {
        get
        {
            lock (_lock)
                return _databaseReader?.Metadata.Description.TryGetValue("en", out var text) == true ? text : null;
        }
    }

    /// <summary><see langword="true"/> when MaxMind credentials are set, so a missing database can be downloaded.</summary>
    public bool CanDownload => _config.AccountId != 0 && !string.IsNullOrWhiteSpace(_config.LicenseKey);

    /// <summary>
    /// Makes sure a database file is in place: downloads it when missing, and refreshes it when
    /// <see cref="GeoIpConfig.AutoUpdate"/> is on and credentials are set. A failed refresh keeps the existing
    /// database (it is only replaced by a verified download); a missing database that cannot be obtained throws.
    /// </summary>
    internal async Task EnsureDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            _logger?.Info(Strings().GeoIpDatabaseNotFound);
            await DownloadDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_config.AutoUpdate && CanDownload)
        {
            _logger?.Info("GeoIP auto-update: refreshing the database...");
            try
            {
                await DownloadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warning($"GeoIP auto-update failed; keeping the existing database: {ex.Message}");
            }
        }

        if (!File.Exists(_databasePath))
            throw new InvalidOperationException($"Failed to obtain GeoIP database at {_databasePath}");
    }

    /// <summary>
    /// Ensures the database file exists (downloading it when missing, refreshing it when
    /// <see cref="GeoIpConfig.AutoUpdate"/> is on), then opens the <see cref="DatabaseReader"/>.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the database file is absent and could not be obtained.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseAsync(cancellationToken).ConfigureAwait(false);

        var reader = OpenReader(_databasePath);
        GeoIpDatabaseKind kind;
        try
        {
            kind = GeoIpDatabaseKinds.Detect(reader.Metadata.DatabaseType)
                ?? throw new InvalidDataException(
                    $"The GeoIP database type '{reader.Metadata.DatabaseType}' is not supported (City, Country, Enterprise, ASN or ISP editions are)");
        }
        catch
        {
            reader.Dispose();
            throw;
        }

        lock (_lock)
        {
            var previous = _databaseReader;
            _databaseReader = reader;
            _kind = kind;
            previous?.Dispose();
        }

        _logger?.Info(InputText.Format(Strings().GeoIpInitialized, _databasePath, reader.Metadata.DatabaseType));
    }

    /// <summary>
    /// Looks up the given IP address in the open database. Which fields are filled depends on the edition
    /// (<see cref="LoadedDatabaseType"/>): City and Enterprise fill the location fields, Country only the country,
    /// ASN the autonomous-system fields, ISP the ISP and autonomous-system fields.
    /// </summary>
    /// <param name="ipAddress">IPv4 or IPv6 address string.</param>
    /// <param name="cancellationToken">Optional cancellation token (reserved for future async lookup).</param>
    /// <returns>An <see cref="IpLocationResult"/> with location data or an error description.</returns>
    public Task<IpLocationResult> LookupIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var strings = Strings();
        var shown = InputText.ForLog(ipAddress);
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger?.Warning(InputText.Format(strings.InvalidIpAddress, shown));
                return Task.FromResult(IpLocationResult.Error(ipAddress, strings.InvalidIpAddressFormat));
            }

            IpLocationResult result;

            lock (_lock)
            {
                if (_databaseReader is null)
                {
                    _logger?.Warning("GeoIP database reader not initialized");
                    return Task.FromResult(IpLocationResult.Error(ipAddress, strings.GeoIpNotInitialized));
                }

                result = Lookup(_databaseReader, _kind, ip, ipAddress);
            }

            return Task.FromResult(result);
        }
        catch (AddressNotFoundException)
        {
            _logger?.Debug($"IP address '{shown}' not found in GeoIP database");
            return Task.FromResult(IpLocationResult.NotFound(ipAddress, strings.LocationNotFound));
        }
        catch (GeoIP2Exception ex)
        {
            _logger?.Warning($"GeoIP2 error for {shown}: {ex.Message}");
            return Task.FromResult(IpLocationResult.Error(ipAddress, ex.Message));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error during GeoIP lookup for {shown}", ex);
            return Task.FromResult(IpLocationResult.Error(ipAddress, ex.Message));
        }
    }

    /// <summary>Runs the lookup that matches the edition and maps the response.</summary>
    private static IpLocationResult Lookup(DatabaseReader reader, GeoIpDatabaseKind kind, IPAddress ip, string ipAddress)
    {
        switch (kind)
        {
            case GeoIpDatabaseKind.City:
            {
                var r = reader.City(ip);
                return new IpLocationResult
                {
                    IpAddress = ipAddress,
                    CountryName = r.Country?.Name,
                    CountryCode = r.Country?.IsoCode,
                    CityName = r.City?.Name,
                    SubdivisionName = r.MostSpecificSubdivision?.Name,
                    PostalCode = r.Postal?.Code,
                    Latitude = r.Location?.Latitude,
                    Longitude = r.Location?.Longitude,
                    TimeZone = r.Location?.TimeZone,
                    Isp = r.Traits?.Isp,
                    AutonomousSystemNumber = r.Traits?.AutonomousSystemNumber,
                    AutonomousSystemOrganization = r.Traits?.AutonomousSystemOrganization
                };
            }
            case GeoIpDatabaseKind.Enterprise:
            {
                var r = reader.Enterprise(ip);
                return new IpLocationResult
                {
                    IpAddress = ipAddress,
                    CountryName = r.Country?.Name,
                    CountryCode = r.Country?.IsoCode,
                    CityName = r.City?.Name,
                    SubdivisionName = r.MostSpecificSubdivision?.Name,
                    PostalCode = r.Postal?.Code,
                    Latitude = r.Location?.Latitude,
                    Longitude = r.Location?.Longitude,
                    TimeZone = r.Location?.TimeZone,
                    Isp = r.Traits?.Isp,
                    AutonomousSystemNumber = r.Traits?.AutonomousSystemNumber,
                    AutonomousSystemOrganization = r.Traits?.AutonomousSystemOrganization
                };
            }
            case GeoIpDatabaseKind.Country:
            {
                var r = reader.Country(ip);
                return new IpLocationResult
                {
                    IpAddress = ipAddress,
                    CountryName = r.Country?.Name,
                    CountryCode = r.Country?.IsoCode
                };
            }
            case GeoIpDatabaseKind.Asn:
            {
                var r = reader.Asn(ip);
                return new IpLocationResult
                {
                    IpAddress = ipAddress,
                    AutonomousSystemNumber = r.AutonomousSystemNumber,
                    AutonomousSystemOrganization = r.AutonomousSystemOrganization
                };
            }
            case GeoIpDatabaseKind.Isp:
            {
                var r = reader.Isp(ip);
                return new IpLocationResult
                {
                    IpAddress = ipAddress,
                    Isp = r.Isp,
                    AutonomousSystemNumber = r.AutonomousSystemNumber,
                    AutonomousSystemOrganization = r.AutonomousSystemOrganization
                };
            }
            default:
                throw new InvalidOperationException($"Unsupported GeoIP database kind {kind}");
        }
    }

    /// <summary>
    /// Downloads the MaxMind database archive using the configured credentials and installs its <c>.mmdb</c>.
    /// Does nothing (logs a warning) when account credentials are absent, and refuses (logs an error, sends
    /// nothing) when <see cref="GeoIpConfig.DownloadUrl"/> is not <c>https</c> on a host in
    /// <see cref="GeoIpConfig.TrustedDownloadHosts"/>.
    /// </summary>
    /// <remarks>
    /// The archive is downloaded and unpacked in a private temporary directory next to the database. Only the
    /// <c>.mmdb</c> entry is extracted (licence and readme files are ignored, and entry paths are never used as
    /// output paths); it is checked for the MaxMind metadata marker and then moved over
    /// <see cref="DatabasePath"/> in one rename, so readers never see a partial file. When the service is
    /// already initialized, the open reader is switched to the new file.
    /// </remarks>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task DownloadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        string? workDir = null;
        try
        {
            if (!CanDownload)
            {
                _logger?.Warning("MaxMind credentials not configured — skipping database download.");
                return;
            }

            if (!_config.TryGetTrustedDownloadUri(out var downloadUri, out var urlError))
            {
                _logger?.Error($"GeoIP download refused: {urlError}");
                return;
            }

            var dbDir = Path.GetDirectoryName(Path.GetFullPath(_databasePath)) ?? string.Empty;
            Directory.CreateDirectory(dbDir);

            workDir = Path.Combine(dbDir, $".geoip-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            var downloadPath = Path.Combine(workDir, "database.tar.gz");

            _logger?.Info($"Downloading GeoIP database from {downloadUri!.GetLeftPart(UriPartial.Path)}...");

            using var httpClient = _httpHandler is null
                ? new HttpClient()
                : new HttpClient(_httpHandler, disposeHandler: false);
            httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{_config.AccountId}:{_config.LicenseKey}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.Error("GeoIP download returned 404 — verify credentials and URL.");
                return;
            }

            response.EnsureSuccessStatusCode();

            await using (var fileStream = File.Create(downloadPath))
            {
                await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            _logger?.Info("GeoIP archive downloaded, extracting...");

            var stagedPath = Path.Combine(workDir, "database.mmdb");
            var expectedKind = GeoIpDatabaseKinds.Detect(_config.DatabaseType);
            await Task.Run(() => ExtractDatabase(downloadPath, stagedPath, $"{_config.DatabaseType}.mmdb", expectedKind), cancellationToken)
                .ConfigureAwait(false);

            InstallDatabase(stagedPath);

            _logger?.Info("GeoIP database downloaded and installed successfully");
        }
        catch (OperationCanceledException)
        {
            _logger?.Warning("GeoIP database download was cancelled");
        }
        catch (HttpRequestException ex)
        {
            _logger?.Error("Failed to download GeoIP database: network error", ex);
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to download GeoIP database", ex);
        }
        finally
        {
            if (workDir is not null)
            {
                try
                {
                    if (Directory.Exists(workDir))
                        Directory.Delete(workDir, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"Failed to clean up temporary GeoIP files: {ex.Message}");
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _databaseReader?.Dispose();
            _databaseReader = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a reader that loads the whole database into memory, so the file is never held open and can be replaced
    /// while lookups continue (a memory-mapped reader locks the file on Windows).
    /// </summary>
    private static DatabaseReader OpenReader(string path) => new(path, FileAccessMode.Memory);

    /// <summary>
    /// Installs a validated staged database without a gap and without risking the working one: the replacement reader
    /// is opened first, then the file is renamed over <see cref="DatabasePath"/>, then the readers are swapped. If any
    /// step fails, the previous file and reader stay in use.
    /// </summary>
    private void InstallDatabase(string stagedPath)
    {
        var replacement = OpenReader(stagedPath);
        GeoIpDatabaseKind kind;
        try
        {
            kind = GeoIpDatabaseKinds.Detect(replacement.Metadata.DatabaseType)
                ?? throw new InvalidDataException($"The GeoIP database type '{replacement.Metadata.DatabaseType}' is not supported");
            File.Move(stagedPath, _databasePath, overwrite: true);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        DatabaseReader? previous;
        lock (_lock)
        {
            previous = _databaseReader;
            // Only an initialized service gets the new reader; otherwise InitializeAsync opens the installed file.
            _databaseReader = previous is null ? null : replacement;
            if (previous is not null) _kind = kind;
        }
        previous?.Dispose();
        if (previous is null) replacement.Dispose();
    }

    /// <summary>
    /// Proves that <paramref name="path"/> is a usable MaxMind database before it may replace the working one: the
    /// metadata must parse, the edition must be one <see cref="LookupIpAsync"/> supports (City, Country, Enterprise, ASN,
    /// ISP) and, when <paramref name="expectedKind"/> is given (from <see cref="GeoIpConfig.DatabaseType"/>), the same
    /// kind, and a lookup must walk the search tree. A marker alone is not enough; a corrupt file can contain it.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a usable database of the expected kind.</exception>
    internal static void ValidateDatabase(string path, GeoIpDatabaseKind? expectedKind = null)
    {
        try
        {
            using var reader = OpenReader(path);
            var type = reader.Metadata.DatabaseType ?? "";
            var kind = GeoIpDatabaseKinds.Detect(type)
                ?? throw new InvalidDataException($"The GeoIP database type '{type}' is not supported (City, Country, Enterprise, ASN or ISP editions are)");
            if (expectedKind is { } expected && kind != expected)
                throw new InvalidDataException($"The GeoIP database type '{type}' is a {kind} database, but GeoIp.DatabaseType asks for {expected}");
            foreach (var probe in reader.Metadata.IPVersion == 6 ? new[] { "8.8.8.8", "2001:4860:4860::8888" } : ["8.8.8.8"])
                Probe(reader, kind, IPAddress.Parse(probe));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"The extracted file is not a usable MaxMind database: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts the <c>.mmdb</c> entry of a <c>tar.gz</c> or <c>zip</c> archive to <paramref name="outputPath"/>.
    /// Prefers an entry named <paramref name="preferredName"/>, else the first <c>.mmdb</c>; only the entry's
    /// content is used, never its path, and no other entry is written anywhere.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The archive format is not supported, it holds no <c>.mmdb</c>, or the file is not a MaxMind database.
    /// </exception>
    internal static void ExtractDatabase(string archivePath, string outputPath, string preferredName, GeoIpDatabaseKind? expectedKind = null)
    {
        static bool IsMmdb(string name) => name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase);
        bool IsPreferred(string name) =>
            string.Equals(name.Replace('\\', '/').Split('/')[^1], preferredName, StringComparison.OrdinalIgnoreCase);

        static void Write(Stream source, string path)
        {
            using var target = File.Create(path);
            source.CopyTo(target);
        }

        var found = false;
        using (var file = File.OpenRead(archivePath))
        {
            Span<byte> magic = stackalloc byte[4];
            var read = file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false);
            file.Position = 0;

            if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
            {
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                        || entry.DataStream is null || !IsMmdb(entry.Name))
                        continue;
                    var preferred = IsPreferred(entry.Name);
                    if (found && !preferred)
                        continue;
                    Write(entry.DataStream, outputPath);
                    found = true;
                    if (preferred)
                        break;
                }
            }
            else if (read >= 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K')
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                var candidates = zip.Entries.Where(e => !e.FullName.EndsWith('/') && IsMmdb(e.FullName)).ToList();
                var entry = candidates.FirstOrDefault(e => IsPreferred(e.FullName)) ?? candidates.FirstOrDefault();
                if (entry is not null)
                {
                    using var source = entry.Open();
                    Write(source, outputPath);
                    found = true;
                }
            }
            else
            {
                throw new InvalidDataException("The GeoIP download is not a tar.gz or zip archive");
            }
        }

        if (!found)
            throw new InvalidDataException("The GeoIP archive contains no .mmdb file");

        ValidateDatabase(outputPath, expectedKind);
    }

    /// <summary>A lookup of the edition's kind; walks the search tree (and throws on a corrupt one).</summary>
    private static void Probe(DatabaseReader reader, GeoIpDatabaseKind kind, IPAddress ip)
    {
        _ = kind switch
        {
            GeoIpDatabaseKind.City => reader.TryCity(ip, out _),
            GeoIpDatabaseKind.Enterprise => reader.TryEnterprise(ip, out _),
            GeoIpDatabaseKind.Country => reader.TryCountry(ip, out _),
            GeoIpDatabaseKind.Asn => reader.TryAsn(ip, out _),
            GeoIpDatabaseKind.Isp => reader.TryIsp(ip, out _),
            _ => throw new InvalidOperationException($"Unsupported GeoIP database kind {kind}")
        };
    }
}

/// <summary>The MaxMind database editions <see cref="GeoIpService"/> can serve.</summary>
internal enum GeoIpDatabaseKind
{
    City,
    Country,
    Enterprise,
    Asn,
    Isp
}

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
