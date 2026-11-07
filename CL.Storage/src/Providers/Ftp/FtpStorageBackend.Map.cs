using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CL.Storage.Providers.Ftp;

public sealed partial class FtpStorageBackend
{

    internal static Error Map(Exception exception, string operation)
    {
        // FluentFTP wraps the server's reply in a generic FtpException ("see InnerException").
        if (exception is FtpException { InnerException: FtpCommandException or FtpInvalidCertificateException or IOException or System.Net.Sockets.SocketException } wrapped
            && exception is not FtpCommandException)
            return Map(wrapped.InnerException!, operation);
        switch (exception)
        {
            case FtpAuthenticationException auth:
                return StorageErrors.AuthenticationFailed($"{operation}: the FTP server rejected the credentials.", ReplyDetails(auth));
            case FtpCommandException command:
                return MapReply(command, operation);
            case FtpInvalidCertificateException:
                return StorageErrors.TlsFailure($"{operation}: the FTP server certificate was not trusted.",
                    $"{StorageErrorInfo.TlsReasonKey}={TlsDiagnosis.ServerCertificateRejected}");
            case FtpMissingObjectException:
                return StorageErrors.NotFound($"{operation}: item was not found.");
            case FtpProxyException:
                return StorageErrors.ConnectionFailed($"{operation}: could not connect through the proxy.");
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "FTP") is { } transport)
            return transport;
        if (exception is IOException or ObjectDisposedException)
            return StorageErrors.ConnectionLost($"{operation}: the FTP connection was lost.");
        return StorageErrors.ProviderError($"{operation}: FTP provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static Error MapReply(FtpCommandException command, string operation)
    {
        var details = ReplyDetails(command);
        var message = command.Message ?? string.Empty;
        return command.CompletionCode switch
        {
            "421" when ContainsAny(message, "too many", "maximum", "max ", "limit", "busy", "try again")
                => StorageErrors.ServerBusy($"{operation}: the FTP server has too many sessions.", details),
            "421" => StorageErrors.ConnectionLost($"{operation}: the FTP server closed the connection.", details),
            "425" or "426" => StorageErrors.ConnectionLost($"{operation}: the FTP data connection failed.", details),
            "430" or "530" or "532" => StorageErrors.AuthenticationFailed($"{operation}: the FTP server rejected the credentials.", details),
            "434" => StorageErrors.ConnectionFailed($"{operation}: the FTP host is unavailable.", details),
            "450" or "550" when ContainsAny(message, "permission", "denied", "not allowed", "forbidden", "access")
                => StorageErrors.PermissionDenied($"{operation}: access was denied.", details),
            "450" or "550" when ContainsAny(message, "exists")
                => StorageErrors.Conflict($"{operation}: the item already exists.", details),
            "450" or "550" when ContainsAny(message, "not empty")
                => StorageErrors.Conflict($"{operation}: the directory is not empty.", details),
            "450" => StorageErrors.Unavailable($"{operation}: the FTP file is temporarily unavailable.", details),
            "550" => StorageErrors.NotFound($"{operation}: item was not found or is unavailable.", details),
            "452" or "552" => StorageErrors.QuotaExceeded($"{operation}: the FTP server has insufficient storage.", details),
            "553" when ContainsAny(message, "permission", "denied", "could not create", "cannot create", "read-only")
                => StorageErrors.PermissionDenied($"{operation}: the FTP server refused to create the file.", details),
            "501" or "553" => StorageErrors.InvalidPath($"{operation}: the FTP server rejected the path.", details),
            "502" or "504" => StorageErrors.Unsupported($"{operation}: the FTP server does not support this command.", details),
            "534" or "535" => StorageErrors.TlsFailure($"{operation}: the FTP server rejected the TLS request.", details),
            _ when command.CompletionCode is { Length: > 0 } code && code[0] == '4'
                => StorageErrors.Unavailable($"{operation}: the FTP server reported a transient failure.", details),
            _ => StorageErrors.ProviderError($"{operation}: FTP command failed.", details)
        };
    }

    private static string ReplyDetails(FtpCommandException command) =>
        $"{StorageErrorInfo.FtpReplyKey}={command.CompletionCode}";

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
