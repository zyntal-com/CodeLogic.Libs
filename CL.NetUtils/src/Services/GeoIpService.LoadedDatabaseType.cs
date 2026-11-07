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

public partial class GeoIpService
{

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
            var download = await DownloadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (download.IsFailure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"Failed to obtain GeoIP database at {_databasePath}: {download.Error!.Message}");
            }
        }
        else if (_config.AutoUpdate && CanDownload)
        {
            _logger?.Info("GeoIP auto-update: refreshing the database...");
            var refresh = await DownloadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (refresh.IsFailure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger?.Warning($"GeoIP auto-update failed; keeping the existing database: {refresh.Error!.Message}");
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

        DatabaseReader? previous;
        lock (_lock)
        {
            previous = _databaseReader;
            _databaseReader = reader;
            _kind = kind;
        }
        RetireLater(previous);

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

            // Snapshot under the lock, look up outside it: lookups run concurrently and a swap never waits for them.
            // A reader replaced meanwhile stays open for RetireGracePeriod, so this lookup finishes on it.
            DatabaseReader? reader;
            GeoIpDatabaseKind kind;
            lock (_lock)
            {
                reader = _databaseReader;
                kind = _kind;
            }

            if (reader is null)
            {
                _logger?.Warning("GeoIP database reader not initialized");
                return Task.FromResult(IpLocationResult.Error(ipAddress, strings.GeoIpNotInitialized));
            }

            return Task.FromResult(Lookup(reader, kind, ip, ipAddress));
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
}
