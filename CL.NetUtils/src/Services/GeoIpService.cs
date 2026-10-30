using System.Net;
using System.Net.Http.Headers;
using CL.NetUtils.Models;
using CodeLogic.Core.Logging;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using SharpCompress.Archives;
using SharpCompress.Common;

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
    private readonly object _lock = new();

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
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

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

            if (_databaseReader is null)
            {
                _logger?.Warning("GeoIP database reader not initialized");
                return Task.FromResult(IpLocationResult.Error(ipAddress, "GeoIP service not initialized"));
            }

            IpLocationResult result;

            lock (_lock)
            {
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
    /// Downloads and extracts the MaxMind database archive using the configured credentials.
    /// Does nothing (logs a warning) when account credentials are absent.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public async Task DownloadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!CanDownload)
            {
                _logger?.Warning("MaxMind credentials not configured — skipping database download.");
                return;
            }

            var dbDir = Path.GetDirectoryName(_databasePath) ?? string.Empty;
            Directory.CreateDirectory(dbDir);

            var tmpDir = Path.Combine(dbDir, "tmp");
            Directory.CreateDirectory(tmpDir);

            var downloadPath = Path.Combine(tmpDir, "database.tar.gz");

            _logger?.Info($"Downloading GeoIP database from {_config.DownloadUrl}...");

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
            };

            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{_config.AccountId}:{_config.LicenseKey}"));

            using var request = new HttpRequestMessage(HttpMethod.Get, _config.DownloadUrl);
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

            await Task.Run(() => ExtractDatabase(downloadPath, dbDir), cancellationToken)
                .ConfigureAwait(false);

            // Clean up temporary files
            try
            {
                if (File.Exists(downloadPath))
                    File.Delete(downloadPath);

                var extractTmpDir = Path.Combine(dbDir, _config.DatabaseType);
                if (Directory.Exists(extractTmpDir))
                    Directory.Delete(extractTmpDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger?.Warning($"Failed to clean up temporary GeoIP files: {ex.Message}");
            }

            _logger?.Info("GeoIP database downloaded and extracted successfully");
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

    private static void ExtractDatabase(string archivePath, string extractionPath)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.IsDirectory)
            {
                var options = new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                };
                entry.WriteToDirectory(extractionPath, options);
            }
        }
    }
}
