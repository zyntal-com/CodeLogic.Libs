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
    /// Downloads the MaxMind database archive using the configured credentials and installs its <c>.mmdb</c>.
    /// Refused without a request (<c>netutils.download_refused</c>) when account credentials are absent or
    /// <see cref="GeoIpConfig.DownloadUrl"/> is not <c>https</c> on a host in <see cref="GeoIpConfig.TrustedDownloadHosts"/>.
    /// </summary>
    /// <remarks>
    /// The whole download runs under <see cref="GeoIpConfig.TimeoutSeconds"/> (<c>netutils.download_timeout</c>). The
    /// archive body may not exceed <see cref="GeoIpConfig.MaxDownloadBytes"/>, and neither may the database unpacked
    /// from it (<c>netutils.download_too_large</c>). With <see cref="GeoIpConfig.VerifyChecksum"/> the server's
    /// published SHA-256 (the download URL with <c>.sha256</c> appended to its <c>suffix</c>) is fetched first and the
    /// body must match it (<c>netutils.checksum_mismatch</c>, <c>netutils.checksum_unavailable</c>). The archive is
    /// downloaded and unpacked in a private temporary directory next to the database. Only the <c>.mmdb</c> entry is
    /// extracted (licence and readme files are ignored, and entry paths are never used as output paths); it must be a
    /// usable database of the configured kind (<c>netutils.invalid_database</c>) and is then moved over
    /// <see cref="DatabasePath"/> in one rename, so readers never see a partial file. When the service is already
    /// initialized, the open reader is switched to the new file. On any failure the existing database stays as it is.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the download (<c>netutils.cancelled</c>).</param>
    /// <returns>Success once the database is installed, or a <c>netutils.*</c> error.</returns>
    public async Task<Result> DownloadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        string? workDir = null;
        try
        {
            if (!CanDownload)
            {
                _logger?.Warning("MaxMind credentials not configured — skipping database download.");
                return Result.Failure(NetUtilsErrors.DownloadRefused("MaxMind credentials (GeoIp.AccountId and GeoIp.LicenseKey) are not configured."));
            }

            if (!_config.TryGetTrustedDownloadUri(out var downloadUri, out var urlError))
            {
                _logger?.Error($"GeoIP download refused: {urlError}");
                return Result.Failure(NetUtilsErrors.DownloadRefused(urlError));
            }

            var maxBytes = _config.MaxDownloadBytes;
            if (maxBytes <= 0)
                return Result.Failure(NetUtilsErrors.DownloadRefused("GeoIp.MaxDownloadBytes must be greater than zero."));

            var dbDir = Path.GetDirectoryName(Path.GetFullPath(_databasePath)) ?? string.Empty;
            Directory.CreateDirectory(dbDir);

            workDir = Path.Combine(dbDir, $".geoip-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            var downloadPath = Path.Combine(workDir, "database.tar.gz");

            _logger?.Info($"Downloading GeoIP database from {downloadUri!.GetLeftPart(UriPartial.Path)}...");

            // One clock for the whole download: HttpClient.Timeout would stop at the response headers, and the body
            // (tens of megabytes) is what takes the time.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var ct = linked.Token;

            using var httpClient = _httpHandler is null
                ? new HttpClient()
                : new HttpClient(_httpHandler, disposeHandler: false);
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{_config.AccountId}:{_config.LicenseKey}"));

            byte[]? expectedHash = null;
            if (_config.VerifyChecksum)
            {
                var checksum = await FetchChecksumAsync(httpClient, credentials, ChecksumUri(downloadUri!), ct).ConfigureAwait(false);
                if (checksum.IsFailure)
                {
                    _logger?.Error($"GeoIP download refused: {checksum.Error!.Message}");
                    return Result.Failure(checksum.Error!);
                }
                expectedHash = checksum.Value!;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.Error("GeoIP download returned 404 — verify credentials and URL.");
                return Result.Failure(NetUtilsErrors.DownloadFailed("The GeoIP download returned HTTP 404; verify the credentials and the URL."));
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.Error($"GeoIP download returned HTTP {(int)response.StatusCode}.");
                return Result.Failure(NetUtilsErrors.DownloadFailed($"The GeoIP download returned HTTP {(int)response.StatusCode}."));
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                _logger?.Error($"GeoIP download refused: the server announces {declared:N0} bytes, over the {maxBytes:N0} byte limit.");
                return Result.Failure(NetUtilsErrors.DownloadTooLarge(
                    $"The GeoIP download is {declared:N0} bytes, over the limit of {maxBytes:N0} bytes (GeoIp.MaxDownloadBytes)."));
            }

            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var bounded = new BoundedReadStream(body, maxBytes, "GeoIP download");
                await using (bounded.ConfigureAwait(false))
                {
                    var fileStream = File.Create(downloadPath);
                    await using (fileStream.ConfigureAwait(false))
                    {
                        await bounded.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                    }
                }
            }

            if (expectedHash is not null)
            {
                byte[] actualHash;
                var downloaded = File.OpenRead(downloadPath);
                await using (downloaded.ConfigureAwait(false))
                {
                    actualHash = await SHA256.HashDataAsync(downloaded, ct).ConfigureAwait(false);
                }
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    _logger?.Error("GeoIP download refused: its SHA-256 does not match the published checksum.");
                    return Result.Failure(NetUtilsErrors.ChecksumMismatch(
                        "The GeoIP download's SHA-256 does not match the checksum the server published; nothing was installed."));
                }
            }

            _logger?.Info("GeoIP archive downloaded, extracting...");

            var stagedPath = Path.Combine(workDir, "database.mmdb");
            var expectedKind = GeoIpDatabaseKinds.Detect(_config.DatabaseType);
            await Task.Run(() => ExtractDatabase(downloadPath, stagedPath, $"{_config.DatabaseType}.mmdb", expectedKind, maxBytes, ct), ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            InstallDatabase(stagedPath);

            _logger?.Info("GeoIP database downloaded and installed successfully");
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger?.Warning("GeoIP database download was cancelled");
            return Result.Failure(NetUtilsErrors.Cancelled("The GeoIP database download was cancelled."));
        }
        catch (OperationCanceledException)
        {
            _logger?.Error($"GeoIP database download did not finish within {_config.TimeoutSeconds} s (GeoIp.TimeoutSeconds).");
            return Result.Failure(NetUtilsErrors.DownloadTimeout(
                $"The GeoIP download did not finish within {_config.TimeoutSeconds} seconds (GeoIp.TimeoutSeconds)."));
        }
        catch (DownloadTooLargeException ex)
        {
            _logger?.Error($"GeoIP download refused: {ex.Message}");
            return Result.Failure(NetUtilsErrors.DownloadTooLarge(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            _logger?.Error("Failed to install the GeoIP database", ex);
            return Result.Failure(NetUtilsErrors.InvalidDatabase(ex.Message));
        }
        catch (HttpRequestException ex)
        {
            _logger?.Error("Failed to download GeoIP database: network error", ex);
            return Result.Failure(NetUtilsErrors.DownloadFailed("The GeoIP download failed with a network error.", ex.Message));
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to download GeoIP database", ex);
            return Result.Failure(NetUtilsErrors.DownloadFailed("The GeoIP download failed.", ex.Message));
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

    /// <summary>
    /// Where the server publishes the SHA-256 of <paramref name="downloadUri"/>: MaxMind serves it under the same path
    /// with <c>.sha256</c> appended to the <c>suffix</c> query value (<c>…?suffix=tar.gz.sha256</c>); a URL without a
    /// <c>suffix</c> gets <c>.sha256</c> appended to its path.
    /// </summary>
    internal static Uri ChecksumUri(Uri downloadUri)
    {
        var builder = new UriBuilder(downloadUri);
        var query = builder.Query.TrimStart('?');
        var parameters = query.Length == 0 ? [] : query.Split('&');
        var replaced = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].StartsWith("suffix=", StringComparison.OrdinalIgnoreCase)) continue;
            parameters[i] += ".sha256";
            replaced = true;
        }
        if (replaced) builder.Query = string.Join('&', parameters);
        else builder.Path += ".sha256";
        return builder.Uri;
    }

    /// <summary>
    /// Fetches the published checksum file (<c>&lt;hex sha256&gt;  &lt;file name&gt;</c>, as <c>sha256sum</c> writes it)
    /// and returns the 32 hash bytes. At most 4 KB of it is read.
    /// </summary>
    private async Task<Result<byte[]>> FetchChecksumAsync(HttpClient httpClient, string credentials, Uri checksumUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, checksumUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Result<byte[]>.Failure(NetUtilsErrors.ChecksumUnavailable(
                $"The published checksum could not be fetched (HTTP {(int)response.StatusCode}); set GeoIp.VerifyChecksum to false for a mirror without one."));

        string text;
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var bounded = new BoundedReadStream(body, 4096, "GeoIP checksum file");
            await using (bounded.ConfigureAwait(false))
            {
                using (var reader = new StreamReader(bounded, System.Text.Encoding.ASCII))
                    text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
        }

        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is not { Length: 64 } || !token.All(char.IsAsciiHexDigit))
            return Result<byte[]>.Failure(NetUtilsErrors.ChecksumUnavailable("The published checksum file does not start with a SHA-256 hex digest."));
        return Result<byte[]>.Success(Convert.FromHexString(token));
    }
}
