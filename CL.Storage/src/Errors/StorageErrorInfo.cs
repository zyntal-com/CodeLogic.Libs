using System.Globalization;
using CodeLogic.Core.Results;

namespace CL.Storage.Errors;

/// <summary>Inspects storage errors for retry decisions and provider diagnostics.</summary>
public static class StorageErrorInfo
{
    /// <summary>Details key carrying a server-requested retry delay in milliseconds.</summary>
    public const string RetryAfterKey = "retryAfterMs";
    /// <summary>Details key carrying the FTP reply code.</summary>
    public const string FtpReplyKey = "ftpReply";
    /// <summary>Details key carrying the SFTP status code.</summary>
    public const string SftpStatusKey = "sftpStatus";
    /// <summary>Details key carrying the HTTP status code.</summary>
    public const string HttpStatusKey = "httpStatus";
    /// <summary>
    /// Details key on <c>storage.tls_failure</c> explaining the failure: <c>server_certificate_rejected</c>
    /// (the server's certificate is not trusted; see <see cref="PresentedCertificateKey"/>),
    /// <c>client_certificate_rejected</c> (the server refused or required our certificate — a credential
    /// problem), <c>protocol_mismatch</c> (no common TLS version or cipher), or <c>handshake_failed</c>.
    /// </summary>
    public const string TlsReasonKey = "tlsReason";
    /// <summary>Details key with the SHA-256 of a refused server certificate, as <c>TrustedCertificateSha256</c> expects.</summary>
    public const string PresentedCertificateKey = "presentedCertificateSha256";
    /// <summary>Details key with the SHA-256 public-key pin of a refused server certificate, as <c>TrustedPublicKeySha256</c> expects.</summary>
    public const string PresentedPublicKeyKey = "presentedPublicKeySha256";
    /// <summary>Details key with the fingerprint of a refused SSH host key.</summary>
    public const string PresentedFingerprintKey = "presentedFingerprint";
    /// <summary>
    /// Details key saying what state a failed mutation left the destination in. <c>complete</c> means the
    /// destination was committed before the failure (for example a backup or staging object could not be
    /// removed afterwards); such an error must be treated as a committed destination, never rolled back.
    /// </summary>
    public const string DestinationStateKey = "destinationState";
    /// <summary>Details key naming an internal object (a provider's own backup or staging copy) a mutation left behind.</summary>
    public const string LeftBehindKey = "leftBehind";

    private static readonly HashSet<string> TransientCodes =
    [
        with(StringComparer.Ordinal),
        StorageErrors.TimeoutCode,
        StorageErrors.UnavailableCode,
        StorageErrors.ConnectionFailedCode,
        StorageErrors.ConnectionLostCode,
        StorageErrors.ServerBusyCode
    ];

    /// <summary>Returns whether repeating the operation later may succeed without caller changes.</summary>
    /// <param name="error">Error to classify.</param>
    /// <returns><see langword="true"/> for timeouts, dropped or refused connections, and busy servers.</returns>
    public static bool IsTransient(Error? error) => error is not null && TransientCodes.Contains(error.Code);

    /// <summary>Returns whether the error means the connection itself is unusable and should be replaced.</summary>
    /// <param name="error">Error to classify.</param>
    /// <returns><see langword="true"/> for lost connections and TLS failures.</returns>
    public static bool IsConnectionFault(Error? error) =>
        error is not null && (error.Code == StorageErrors.ConnectionLostCode || error.Code == StorageErrors.TlsFailureCode);

    /// <summary>Reads a server-requested retry delay from the error details.</summary>
    /// <param name="error">Error to inspect.</param>
    /// <param name="delay">Receives the requested delay when present.</param>
    /// <returns><see langword="true"/> when the error carries a retry delay.</returns>
    public static bool TryGetRetryAfter(Error? error, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!TryGetDetail(error, RetryAfterKey, out var raw) ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return false;
        delay = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    /// <summary>Returns whether the error was raised after the destination had already been committed.</summary>
    /// <param name="error">Error to inspect.</param>
    /// <returns><see langword="true"/> when the details carry <c>destinationState=complete</c>.</returns>
    public static bool DestinationCommitted(Error? error) =>
        TryGetDetail(error, DestinationStateKey, out var state) && string.Equals(state, "complete", StringComparison.Ordinal);

    /// <summary>Reads a <c>key=value</c> entry from the error details.</summary>
    /// <param name="error">Error to inspect.</param>
    /// <param name="key">Details key such as <see cref="FtpReplyKey"/>.</param>
    /// <param name="value">Receives the value when present.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public static bool TryGetDetail(Error? error, string key, out string value)
    {
        value = string.Empty;
        if (error?.Details is not { Length: > 0 } details)
            return false;
        foreach (var part in details.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part.AsSpan(0, separator).Equals(key, StringComparison.Ordinal))
            {
                value = part[(separator + 1)..];
                return true;
            }
        }
        return false;
    }
}
