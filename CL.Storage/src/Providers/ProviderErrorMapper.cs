using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>Shared classification of transport exceptions and HTTP statuses into storage errors.</summary>
internal static partial class ProviderErrorMapper
{
    private const int WindowsDiskFull = unchecked((int)0x80070070);
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027);
    private const int PosixNoSpace = 28;
    private const int PosixQuota = 122;

    /// <summary>Maps an HTTP status to a storage error; returns null for success statuses.</summary>
    public static Error FromHttpStatus(
        int status,
        string operation,
        string service,
        TimeSpan? retryAfter = null,
        string? providerCode = null)
    {
        var details = Details(StorageErrorInfo.HttpStatusKey, status.ToString(CultureInfo.InvariantCulture), providerCode, retryAfter);
        return status switch
        {
            401 => StorageErrors.AuthenticationFailed($"{operation}: {service} rejected the credentials.", details),
            403 => StorageErrors.PermissionDenied($"{operation}: access was denied.", details),
            404 or 410 => StorageErrors.NotFound($"{operation}: item was not found.", details),
            408 or 504 => StorageErrors.Timeout($"{operation}: operation timed out.", details),
            409 or 412 or 423 => StorageErrors.Conflict($"{operation}: {service} conflict.", details),
            413 => StorageErrors.TooLarge($"{operation}: {service} rejected the request size.", details),
            429 or 503 => StorageErrors.ServerBusy($"{operation}: {service} is busy.", details),
            507 => StorageErrors.QuotaExceeded($"{operation}: {service} has insufficient storage.", details),
            >= 500 => StorageErrors.Unavailable($"{operation}: {service} is unavailable.", details),
            _ => StorageErrors.ProviderError($"{operation}: {service} request failed.", details)
        };
    }

    /// <summary>Reads a Retry-After header as a relative delay.</summary>
    public static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Classifies socket, TLS, IO, HTTP transport, and timeout exceptions; returns null for anything else.</summary>
    public static Error? FromTransport(Exception exception, string operation, string service)
    {
        if (exception is ProviderPoolExhaustedException exhausted)
            return StorageErrors.ServerBusy(
                $"{operation}: all {exhausted.MaxSessions} {service} sessions are in use.",
                "reason=session_limit");
        // A handshake failure, or an alert about it (TLS 1.3 refuses a client certificate after the handshake, inside an
        // IOException), is a TLS failure. Any other TLS error on an established connection is a lost connection:
        // transient, with the TLS error as a hint.
        if (Find<AuthenticationException>(exception) is not null || TlsDiagnosis.IsPlatformTlsFailure(exception))
            return TlsDiagnosis.IsHandshakeFailure(exception)
                ? StorageErrors.TlsFailure($"{operation}: the {service} TLS handshake or certificate validation failed.", TlsDiagnosis.Details(exception))
                : StorageErrors.ConnectionLost($"{operation}: the {service} TLS connection was lost.", $"{StorageErrorInfo.TlsReasonKey}={TlsDiagnosis.ConnectionInterrupted}");
        if (IsDiskFull(exception))
            return StorageErrors.QuotaExceeded($"{operation}: insufficient storage space.");
        if (exception is SocketException socket)
            return FromSocket(socket, operation, service, connected: false);
        if (exception is HttpRequestException http)
        {
            if (Find<SocketException>(http) is { } inner)
                return FromSocket(inner, operation, service, connected: false);
            if (http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError)
                return StorageErrors.ConnectionFailed($"{operation}: could not connect to {service}.");
            if (http.HttpRequestError is HttpRequestError.ResponseEnded)
                return StorageErrors.ConnectionLost($"{operation}: the {service} connection was lost.");
            return StorageErrors.Unavailable($"{operation}: {service} is unavailable.");
        }
        if (exception is TimeoutException or TaskCanceledException)
            return StorageErrors.Timeout($"{operation}: operation timed out.");
        if (exception is IOException io && Find<SocketException>(io) is { } ioSocket)
            return FromSocket(ioSocket, operation, service, connected: true);
        return null;
    }

    /// <summary>Returns whether the exception or one of its inner exceptions reports a full disk or exhausted quota.</summary>
    public static bool IsDiskFull(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not IOException io) continue;
            if (io.HResult is WindowsDiskFull or WindowsHandleDiskFull) return true;
            if (!OperatingSystem.IsWindows() && io.HResult is PosixNoSpace or PosixQuota) return true;
        }
        return false;
    }

    /// <summary>Describes an unclassified failure by exception type only; messages may carry paths or secrets.</summary>
    public static string ExceptionDetails(Exception exception)
    {
        var details = $"exception={exception.GetType().Name}";
        if (exception.InnerException is { } inner)
            details += $";inner={inner.GetType().Name}";
        return details;
    }

    /// <summary>
    /// A provider SDK message fit for an error message and logs: the first line only, URL query strings and
    /// credential-looking values (access keys, signatures, tokens, passwords) redacted, at most 300 characters.
    /// Returns an empty string when nothing is left.
    /// </summary>
    public static string SafeProviderMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var text = message.Trim();
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0) text = text[..newline];
        text = UrlQuery().Replace(text, "?[redacted]");
        text = SecretPair().Replace(text, m => m.Groups[1].Value + "=[redacted]");
        text = AccessKeyId().Replace(text, "[redacted]");
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    /// <summary>A value for a <c>key=value</c> detail: no separators, at most 128 characters.</summary>
    public static string SafeDetailValue(string value)
    {
        var text = new string(value.Where(c => !char.IsControl(c) && c is not (';' or '=')).ToArray()).Trim();
        return text.Length <= 128 ? text : text[..128];
    }

    [GeneratedRegex(@"\?[^\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();

    [GeneratedRegex(@"\b(signature|x-amz-signature|x-amz-credential|x-amz-security-token|sig|token|secret|password|passwd|pwd|key|accesskey|secretkey|sessiontoken)\s*[=:]\s*[^\s,;&""']+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretPair();

    [GeneratedRegex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA|ANVA|AIPA)[A-Z0-9]{12,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AccessKeyId();

    /// <summary>Joins sanitized <c>key=value</c> diagnostics.</summary>
    public static string Details(string key, string value, string? providerCode = null, TimeSpan? retryAfter = null)
    {
        var details = $"{key}={value}";
        if (!string.IsNullOrWhiteSpace(providerCode)) details += $";providerCode={providerCode}";
        if (retryAfter is { } delay)
            details += $";{StorageErrorInfo.RetryAfterKey}={(long)Math.Max(0, delay.TotalMilliseconds)}";
        return details;
    }

    /// <summary>Finds the first exception of the requested type in the inner-exception chain.</summary>
    public static T? Find<T>(Exception? exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }

    private static Error FromSocket(SocketException socket, string operation, string service, bool connected)
    {
        var details = $"socketError={socket.SocketErrorCode}";
        return socket.SocketErrorCode switch
        {
            SocketError.TimedOut => StorageErrors.Timeout($"{operation}: operation timed out.", details),
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown or SocketError.NotConnected
                => StorageErrors.ConnectionLost($"{operation}: the {service} connection was lost.", details),
            _ when connected => StorageErrors.ConnectionLost($"{operation}: the {service} connection was lost.", details),
            _ => StorageErrors.ConnectionFailed($"{operation}: could not connect to {service}.", details)
        };
    }
}
