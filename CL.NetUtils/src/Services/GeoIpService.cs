using System.Net;
using System.Net.Http.Headers;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
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
    private readonly Lock _lock = new();
    private readonly HttpMessageHandler? _httpHandler;

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

        _databasePath = !string.IsNullOrWhiteSpace(config.DatabasePath)
            ? config.DatabasePath
            : Path.Combine(dataDirectory, "geoip", $"{config.DatabaseType}.mmdb");
    }

    /// <summary>The <c>.mmdb</c> file this service reads (from <see cref="GeoIpConfig.DatabasePath"/> or the library data directory).</summary>
    public string DatabasePath => _databasePath;

    /// <summary><see langword="true"/> when MaxMind credentials are set, so a missing database can be downloaded.</summary>
    public bool CanDownload => _config.AccountId != 0 && !string.IsNullOrWhiteSpace(_config.LicenseKey);

    /// <summary>
    /// Ensures the database file exists (downloading it when needed and permitted),
    /// then opens the <see cref="DatabaseReader"/>.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the database file is absent and could not be obtained.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            _logger?.Info("GeoIP database not found, downloading...");
            await DownloadDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(_databasePath))
            throw new InvalidOperationException($"Failed to obtain GeoIP database at {_databasePath}");

        lock (_lock)
        {
            _databaseReader = new DatabaseReader(_databasePath);
        }

        _logger?.Info($"GeoIP service initialized with database: {_databasePath}");
    }

    /// <summary>
    /// Looks up geolocation information for the given IP address.
    /// </summary>
    /// <param name="ipAddress">IPv4 or IPv6 address string.</param>
    /// <param name="cancellationToken">Optional cancellation token (reserved for future async lookup).</param>
    /// <returns>An <see cref="IpLocationResult"/> with location data or an error description.</returns>
    public Task<IpLocationResult> LookupIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger?.Warning($"Invalid IP address: {ipAddress}");
                return Task.FromResult(IpLocationResult.Error(ipAddress, "Invalid IP address format"));
            }

            IpLocationResult result;

            lock (_lock)
            {
                if (_databaseReader is null)
                {
                    _logger?.Warning("GeoIP database reader not initialized");
                    return Task.FromResult(IpLocationResult.Error(ipAddress, "GeoIP service not initialized"));
                }

                var response = _databaseReader.City(ip);

                result = new IpLocationResult
                {
                    IpAddress = ipAddress,
                    CountryName = response?.Country?.Name,
                    CountryCode = response?.Country?.IsoCode,
                    CityName = response?.City?.Name,
                    SubdivisionName = response?.MostSpecificSubdivision?.Name,
                    PostalCode = response?.Postal?.Code,
                    Latitude = response?.Location?.Latitude,
                    Longitude = response?.Location?.Longitude,
                    TimeZone = response?.Location?.TimeZone,
                    Isp = response?.Traits?.Isp
                };
            }

            return Task.FromResult(result);
        }
        catch (AddressNotFoundException)
        {
            _logger?.Debug($"IP address '{ipAddress}' not found in GeoIP database");
            return Task.FromResult(IpLocationResult.NotFound(ipAddress));
        }
        catch (GeoIP2Exception ex)
        {
            _logger?.Warning($"GeoIP2 error for {ipAddress}: {ex.Message}");
            return Task.FromResult(IpLocationResult.Error(ipAddress, ex.Message));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error during GeoIP lookup for {ipAddress}", ex);
            return Task.FromResult(IpLocationResult.Error(ipAddress, ex.Message));
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
            await Task.Run(() => ExtractDatabase(downloadPath, stagedPath, $"{_config.DatabaseType}.mmdb"), cancellationToken)
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
    /// Moves the staged database over <see cref="DatabasePath"/> in one rename and, when a reader is open,
    /// reopens it on the new file. The old reader is closed first because a memory-mapped file cannot be
    /// replaced on Windows; lookups wait on the lock meanwhile.
    /// </summary>
    private void InstallDatabase(string stagedPath)
    {
        lock (_lock)
        {
            var wasOpen = _databaseReader is not null;
            _databaseReader?.Dispose();
            _databaseReader = null;

            File.Move(stagedPath, _databasePath, overwrite: true);

            if (wasOpen)
                _databaseReader = new DatabaseReader(_databasePath);
        }
    }

    /// <summary>The marker that starts the metadata section at the end of every MaxMind DB file.</summary>
    private static ReadOnlySpan<byte> MetadataMarker =>
        [0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m'];

    /// <summary>
    /// Extracts the <c>.mmdb</c> entry of a <c>tar.gz</c> or <c>zip</c> archive to <paramref name="outputPath"/>.
    /// Prefers an entry named <paramref name="preferredName"/>, else the first <c>.mmdb</c>; only the entry's
    /// content is used, never its path, and no other entry is written anywhere.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The archive format is not supported, it holds no <c>.mmdb</c>, or the file is not a MaxMind database.
    /// </exception>
    internal static void ExtractDatabase(string archivePath, string outputPath, string preferredName)
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

        // MaxMind DB metadata sits within the last 128 KiB, after a fixed marker.
        using var check = File.OpenRead(outputPath);
        var tail = (int)Math.Min(check.Length, 128 * 1024);
        var buffer = new byte[tail];
        check.Seek(-tail, SeekOrigin.End);
        check.ReadExactly(buffer);
        if (buffer.AsSpan().LastIndexOf(MetadataMarker) < 0)
            throw new InvalidDataException("The extracted file is not a MaxMind database");
    }
}
