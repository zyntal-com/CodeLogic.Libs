using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>Server details read from a live session.</summary>
internal sealed record StorageServerDetails(
    string? System,
    string? Software,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, string> Negotiated);

/// <summary>Implemented by backends that can describe the server and their sessions.</summary>
internal interface IStorageDiagnosticsSource
{
    /// <summary>Gets the certificate or host key the server presented most recently, trusted or not.</summary>
    StorageServerIdentity? PresentedIdentity { get; }

    /// <summary>Gets session pool counters, when the backend pools sessions.</summary>
    StorageSessionPoolStats? PoolStats { get; }

    /// <summary>Reads server details from a live session.</summary>
    Task<Result<StorageServerDetails>> GetServerDetailsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Remembers the last certificate or host key a server presented. Validation callbacks record into it,
/// including rejections, so a failed connection can still say what the server offered.
/// </summary>
internal sealed class ServerIdentityRecorder
{
    /// <summary>The identity and when it was recorded, replaced together so a reader never pairs one with the other's time.</summary>
    private sealed record Recorded(StorageServerIdentity Identity, DateTimeOffset At);

    private Recorded? _last;

    public StorageServerIdentity? Last => Volatile.Read(ref _last)?.Identity;

    /// <summary>Gets when <see cref="Last"/> was recorded.</summary>
    public DateTimeOffset? LastRecordedAt => Volatile.Read(ref _last)?.At;

    private void Set(StorageServerIdentity identity) => Volatile.Write(ref _last, new Recorded(identity, DateTimeOffset.UtcNow));

    public void RecordCertificate(X509Certificate? certificate, bool trusted)
    {
        if (certificate is null) return;
        try
        {
            using var parsed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            Set(new StorageServerIdentity(
                "tls-certificate",
                Convert.ToHexString(SHA256.HashData(parsed.RawData)),
                TlsPins.PublicKeyPin(parsed),
                null,
                parsed.Subject,
                parsed.Issuer,
                new DateTimeOffset(parsed.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                trusted));
        }
        catch (CryptographicException)
        {
            // A certificate that cannot be parsed is still rejected by the validation callback.
        }
    }

    private long _clientCertificateRequestTicks;

    /// <summary>Gets when a server last asked for a client certificate during a TLS handshake.</summary>
    public DateTimeOffset? ClientCertificateRequestedAt =>
        Interlocked.Read(ref _clientCertificateRequestTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Records that a server asked a connection for a client certificate (it sent its certificate or the issuers it
    /// accepts). Only a hint: servers that request but do not require one ask on every handshake.
    /// </summary>
    public void RecordClientCertificateRequest() => Interlocked.Exchange(ref _clientCertificateRequestTicks, DateTimeOffset.UtcNow.UtcTicks);

    private long _clientCertificateRefusalTicks;

    /// <summary>Gets when a connection asked for a client certificate last failed before the server accepted our reply.</summary>
    public DateTimeOffset? ClientCertificateRefusedAt =>
        Interlocked.Read(ref _clientCertificateRefusalTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Records that a connection the server asked for a client certificate failed after our reply and before the server
    /// sent anything but an alert: the server refused the certificate (see <see cref="TlsConnectionWatch"/>).
    /// </summary>
    public void RecordClientCertificateRefusal() => Interlocked.Exchange(ref _clientCertificateRefusalTicks, DateTimeOffset.UtcNow.UtcTicks);

    public void RecordHostKey(string? algorithm, string fingerprintSha256, bool trusted) =>
        Set(new StorageServerIdentity(
            "ssh-host-key",
            fingerprintSha256.StartsWith("SHA256:", StringComparison.Ordinal) ? fingerprintSha256 : $"SHA256:{fingerprintSha256}",
            null,
            algorithm,
            null,
            null,
            null,
            trusted));
}

/// <summary>Reads the server address and transport security from a connection configuration.</summary>
internal static class StorageEndpoints
{
    public static (string? Host, int? Port, StorageTransportSecurity Security) Describe(StorageConnectionConfigBase configuration) => configuration switch
    {
        FtpConnectionConfig ftp => (ftp.Host, ftp.Port,
            ftp.EncryptionMode == StorageFtpEncryptionMode.None ? StorageTransportSecurity.None : StorageTransportSecurity.Tls),
        SftpConnectionConfig sftp => (sftp.Host, sftp.Port, StorageTransportSecurity.Ssh),
        WebDavConnectionConfig webDav => FromUrl(webDav.Endpoint),
        S3ConnectionConfig s3 => FromUrl(string.IsNullOrWhiteSpace(s3.ServiceUrl) ? $"https://s3.{s3.Region}.amazonaws.com" : s3.ServiceUrl),
        AzureBlobConnectionConfig azure => FromAzure(azure),
        GoogleCloudConnectionConfig gcs => FromUrl(string.IsNullOrWhiteSpace(gcs.ServiceUrl) ? "https://storage.googleapis.com" : gcs.ServiceUrl),
        SwiftConnectionConfig swift => FromUrl(swift.StorageUrl ?? swift.AuthenticationUrl),
        // No default arm: StorageConnectionConfigBase is closed, so a new provider type fails to compile here.
    };

    private static (string?, int?, StorageTransportSecurity) FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (null, null, StorageTransportSecurity.Unknown);
        var security = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? StorageTransportSecurity.Tls
            : StorageTransportSecurity.None;
        return (uri.Host, uri.Port, security);
    }

    private static (string?, int?, StorageTransportSecurity) FromAzure(AzureBlobConnectionConfig azure)
    {
        if (!string.IsNullOrWhiteSpace(azure.ServiceUri))
            return FromUrl(azure.ServiceUri);
        if (!string.IsNullOrWhiteSpace(azure.ConnectionString))
        {
            // Only address parts are read; keys and SAS tokens in the string are never touched.
            var parts = azure.ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);
            if (parts.TryGetValue("BlobEndpoint", out var endpoint))
                return FromUrl(endpoint);
            if (parts.TryGetValue("UseDevelopmentStorage", out var development) && bool.TryParse(development, out var isDevelopment) && isDevelopment)
                return ("127.0.0.1", 10000, StorageTransportSecurity.None);
            if (parts.TryGetValue("AccountName", out var account))
            {
                var scheme = parts.TryGetValue("DefaultEndpointsProtocol", out var protocol) ? protocol : "https";
                var suffix = parts.TryGetValue("EndpointSuffix", out var configuredSuffix) ? configuredSuffix : "core.windows.net";
                return FromUrl($"{scheme}://{account}.blob.{suffix}");
            }
        }
        return string.IsNullOrWhiteSpace(azure.AccountName)
            ? (null, null, StorageTransportSecurity.Unknown)
            : FromUrl($"https://{azure.AccountName}.blob.core.windows.net");
    }

    public static StorageSessionPoolStats ToPublic(ProviderPoolStats stats) =>
        new(stats.Idle, stats.InUse, stats.Opened, stats.Destroyed, stats.ProbeFailures);
}
