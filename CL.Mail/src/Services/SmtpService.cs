using CL.Mail.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net.Sockets;

namespace CL.Mail.Services;

/// <summary>
/// Sends e-mail via SMTP using <see href="https://github.com/jstedfast/MailKit">MailKit</see>.
/// A new connection is opened per <see cref="SendAsync"/> call — no connection pooling.
/// </summary>
public sealed class SmtpService : IDisposable
{
    private readonly SmtpConfig _config;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>Initialises the service with SMTP configuration.</summary>
    /// <param name="config">SMTP server settings.</param>
    /// <param name="logger">Optional logger.</param>
    public SmtpService(SmtpConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>
    /// Sends <paramref name="message"/> through the configured SMTP server.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MailSendReceipt"/> carrying the Message-ID on success; otherwise an
    /// <see cref="Error"/> whose <see cref="Error.Code"/> is one of <see cref="MailErrorCodes"/>.
    /// </returns>
    public async Task<Result<MailSendReceipt>> SendAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (ValidateConfiguration() is { } configError) return configError;
        if (ValidateMessage(message) is { } messageError) return messageError;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mime = BuildMimeMessage(message);

            using var client = new SmtpClient { Timeout = _config.TimeoutSeconds * 1000 };
            var security = MapSecurity(_config.SecurityMode);

            await client.ConnectAsync(_config.Host, _config.Port, security, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(_config.Username))
                await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken).ConfigureAwait(false);

            await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
            var sentAt = DateTime.UtcNow;
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            _logger?.Info($"Email sent to {string.Join(", ", message.To)} (id={mime.MessageId})");
            return new MailSendReceipt(mime.MessageId, sentAt);
        }
        catch (SmtpCommandException ex)
        {
            // The server refused something about this message (sender, recipient, size, content):
            // a problem with the input, not with the service, so it is a validation error.
            _logger?.Error("SMTP command rejected", ex);
            return MailErrors.Validation(MailErrorCodes.SmtpRejected, ex.Message,
                $"SMTP status {(int)ex.StatusCode} ({ex.StatusCode}), {ex.ErrorCode}");
        }
        catch (SmtpProtocolException ex)
        {
            _logger?.Error("SMTP protocol error", ex);
            return MailErrors.Internal(MailErrorCodes.SmtpError, ex);
        }
        catch (AuthenticationException ex)
        {
            _logger?.Error("SMTP authentication failed", ex);
            return MailErrors.Unauthorized(MailErrorCodes.SmtpAuthenticationFailed, "Authentication failed");
        }
        catch (OperationCanceledException)
        {
            _logger?.Warning("Email send cancelled");
            return MailErrors.Cancelled();
        }
        catch (TimeoutException)
        {
            _logger?.Error("SMTP connection timed out");
            return MailErrors.Timeout(MailErrorCodes.SmtpTimeout, "SMTP timeout");
        }
        catch (SocketException ex)
        {
            _logger?.Error($"SMTP server {_config.Host}:{_config.Port} unreachable", ex);
            return MailErrors.Unavailable(MailErrorCodes.SmtpConnectionFailed,
                $"Could not connect to SMTP server {_config.Host}:{_config.Port}", ex);
        }
        catch (Exception ex)
        {
            _logger?.Error("Unexpected error sending email", ex);
            return MailErrors.Internal(MailErrorCodes.Unknown, ex);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private Error? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_config.Host))
            return MailErrors.Validation(MailErrorCodes.SmtpConfigInvalid, "SMTP host not configured");

        if (_config.Port is < 1 or > 65535)
            return MailErrors.Validation(MailErrorCodes.SmtpConfigInvalid, "SMTP port is invalid");

        // Fail closed: an unknown mode is a configuration error, never a fallback to opportunistic TLS.
        if (!Enum.IsDefined(_config.SecurityMode))
            return MailErrors.Validation(MailErrorCodes.SmtpConfigInvalid, $"SMTP security mode '{_config.SecurityMode}' is not known");

        if (!string.IsNullOrWhiteSpace(_config.AttachmentBaseDirectory) && !Path.IsPathFullyQualified(_config.AttachmentBaseDirectory))
            return MailErrors.Validation(MailErrorCodes.SmtpConfigInvalid, "Smtp.AttachmentBaseDirectory must be a fully qualified path");

        return null;
    }

    private Error? ValidateMessage(MailMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.From))
            return MailErrors.Validation(MailErrorCodes.InvalidSender, "Sender not specified");

        if (!IsValidAddress(message.From))
            return MailErrors.Validation(MailErrorCodes.InvalidSender, $"Sender address is malformed: '{message.From}'");

        if (message.To is null || message.To.Count == 0)
            return MailErrors.Validation(MailErrorCodes.InvalidRecipient, "No recipients specified");

        foreach (var address in message.To.Concat(message.Cc).Concat(message.Bcc))
            if (!IsValidAddress(address))
                return MailErrors.Validation(MailErrorCodes.InvalidRecipient, $"Recipient address is malformed: '{address}'");

        foreach (var address in message.ReplyTo)
            if (!IsValidAddress(address))
                return MailErrors.Validation(MailErrorCodes.InvalidRecipient, $"Reply-To address is malformed: '{address}'");

        if (HasLineBreak(message.FromName))
            return MailErrors.Validation(MailErrorCodes.InvalidSender, "Sender name cannot contain a line break");

        if (string.IsNullOrWhiteSpace(message.Subject))
            return MailErrors.Validation(MailErrorCodes.InvalidSubject, "Subject not specified");

        if (HasLineBreak(message.Subject))
            return MailErrors.Validation(MailErrorCodes.InvalidSubject, "Subject cannot contain a line break");

        foreach (var (name, value) in message.Headers)
            if (ValidateHeader(name, value) is { } headerError)
                return MailErrors.Validation(MailErrorCodes.InvalidHeader, headerError);

        if (string.IsNullOrWhiteSpace(message.TextBody) && string.IsNullOrWhiteSpace(message.HtmlBody))
            return MailErrors.Validation(MailErrorCodes.InvalidBody, "Message body is empty");

        foreach (var path in message.Attachments)
            if (ValidateAttachment(path, _config.AttachmentBaseDirectory) is { } attachmentError)
                return attachmentError;

        return null;
    }

    /// <summary>
    /// An attachment must be a fully qualified path to an existing file, and with a base directory configured, the
    /// file (and the target of a link) must lie inside it.
    /// </summary>
    internal static Error? ValidateAttachment(string? path, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
            return MailErrors.Validation(MailErrorCodes.InvalidAttachment, $"Attachment path must be a fully qualified file path: '{path}'");

        var full = Path.GetFullPath(path);
        string? root = null;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) + Path.DirectorySeparatorChar;
            if (!IsInside(full, root))
                return MailErrors.Validation(MailErrorCodes.InvalidAttachment, $"Attachment is outside the allowed directory: '{path}'");
        }

        var file = new FileInfo(full);
        if (!file.Exists)
            return MailErrors.NotFound(MailErrorCodes.AttachmentNotFound, $"Attachment not found: '{path}'");

        if (root is not null && file.LinkTarget is not null)
        {
            var target = file.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsInside(Path.GetFullPath(target.FullName), root))
                return MailErrors.Validation(MailErrorCodes.InvalidAttachment, $"Attachment links outside the allowed directory: '{path}'");
        }

        return null;
    }

    private static bool IsInside(string fullPath, string rootWithSeparator) =>
        fullPath.StartsWith(rootWithSeparator,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool HasLineBreak(string? value) =>
        value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    // Headers the service writes from the message itself, or that identify the sender or the message; a custom copy
    // would duplicate or contradict them (a spoofed Sender/Reply-To, a forged Message-Id or Date, fake trace fields).
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "Sender", "Reply-To", "To", "Cc", "Bcc", "Subject", "Date", "Message-Id", "MIME-Version",
        "Importance", "Return-Path", "Received",
    };

    /// <summary>Returns why a custom header is not acceptable, or null when it is.</summary>
    private static string? ValidateHeader(string? name, string? value)
    {
        // RFC 5322 field-name: one or more printable US-ASCII characters except ':'.
        if (string.IsNullOrEmpty(name))
            return "A custom header name is empty";
        foreach (var c in name)
            if (c is < '!' or > '~' or ':')
                return $"Custom header name '{name}' must be printable US-ASCII without spaces or ':'";
        if (ReservedHeaders.Contains(name)
            || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Resent-", StringComparison.OrdinalIgnoreCase))
            return $"Custom header '{name}' is set by the mail service and cannot be supplied";
        if (value is null)
            return $"Custom header '{name}' has no value";
        if (value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            return $"Custom header '{name}' cannot contain a line break or NUL";
        return null;
    }

    internal static bool IsValidAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address)
        && MailboxAddress.TryParse(address, out var mailbox)
        && mailbox.Address.Contains('@', StringComparison.Ordinal);

    // ── MimeMessage builder ───────────────────────────────────────────────────

    private static MimeMessage BuildMimeMessage(MailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.FromName ?? message.From, message.From));

        foreach (var to in message.To)  mime.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in message.Cc)  mime.Cc.Add(MailboxAddress.Parse(cc));
        foreach (var bcc in message.Bcc) mime.Bcc.Add(MailboxAddress.Parse(bcc));
        foreach (var replyTo in message.ReplyTo) mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));

        mime.Subject = message.Subject;
        mime.Importance = message.Priority switch
        {
            MailPriority.Low  => MessageImportance.Low,
            MailPriority.High => MessageImportance.High,
            _                 => MessageImportance.Normal
        };

        foreach (var header in message.Headers)
            mime.Headers.Add(header.Key, header.Value);

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(message.TextBody)) builder.TextBody = message.TextBody;
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) builder.HtmlBody = message.HtmlBody;

        foreach (var path in message.Attachments)
            builder.Attachments.Add(Path.GetFullPath(path));

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SecureSocketOptions MapSecurity(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.None     => SecureSocketOptions.None,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailSecurityMode.SslTls   => SecureSocketOptions.SslOnConnect,
        // Unreachable: ValidateConfiguration refuses unknown modes. Never fall back to opportunistic TLS.
        _                         => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown mail security mode")
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
