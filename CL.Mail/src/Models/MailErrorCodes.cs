using CodeLogic.Core.Results;

namespace CL.Mail.Models;

/// <summary>
/// Stable <see cref="Error.Code"/> values for every failure CL.Mail reports.
/// Each code always comes with the same <see cref="Error"/> kind, listed on the constant.
/// </summary>
public static class MailErrorCodes
{
    /// <summary>A recipient address is missing or malformed. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidRecipient = "mail.invalid_recipient";

    /// <summary>The sender address is missing or malformed, or the sender name contains a line break. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidSender = "mail.invalid_sender";

    /// <summary>The subject line is missing or contains a line break. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidSubject = "mail.invalid_subject";

    /// <summary>
    /// A custom header has an invalid name (empty, not printable US-ASCII, or containing <c>:</c>), a value with a
    /// line break or NUL, or names a header the service writes itself (From, To, Cc, Bcc, Subject, MIME-Version,
    /// Importance, Content-*).
    /// Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidHeader = "mail.invalid_header";

    /// <summary>The message has neither a text nor an HTML body. Kind: <see cref="ValidationError"/>.</summary>
    public const string InvalidBody = "mail.invalid_body";

    /// <summary>The named template does not exist. Kind: <see cref="NotFoundError"/>.</summary>
    public const string TemplateNotFound = "mail.template_not_found";

    /// <summary>
    /// A template ID is not a file-name-safe identifier (it contains path separators, starts with a dot, ...).
    /// Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidTemplateId = "mail.invalid_template_id";

    /// <summary>A template could not be read or rendered. Kind: <see cref="InternalError"/>.</summary>
    public const string TemplateRenderingFailed = "mail.template_rendering_failed";

    /// <summary>The SMTP configuration is incomplete or invalid. Kind: <see cref="ValidationError"/>.</summary>
    public const string SmtpConfigInvalid = "mail.smtp_config_invalid";

    /// <summary>The SMTP server rejected the credentials. Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string SmtpAuthenticationFailed = "mail.smtp_authentication_failed";

    /// <summary>The SMTP operation timed out. Kind: <see cref="TimeoutError"/>.</summary>
    public const string SmtpTimeout = "mail.smtp_timeout";

    /// <summary>The SMTP server could not be reached (DNS, refused, unreachable). Kind: <see cref="UnavailableError"/>.</summary>
    public const string SmtpConnectionFailed = "mail.smtp_connection_failed";

    /// <summary>
    /// The SMTP server rejected a command, typically the sender or a recipient.
    /// Kind: <see cref="ValidationError"/>; <see cref="Error.Details"/> carries the SMTP status code.
    /// </summary>
    public const string SmtpRejected = "mail.smtp_rejected";

    /// <summary>A general SMTP protocol error. Kind: <see cref="InternalError"/>.</summary>
    public const string SmtpError = "mail.smtp_error";

    /// <summary>An attachment file does not exist on disk. Kind: <see cref="NotFoundError"/>.</summary>
    public const string AttachmentNotFound = "mail.attachment_not_found";

    /// <summary>
    /// An attachment path is not a fully qualified path, or lies outside
    /// <see cref="SmtpConfig.AttachmentBaseDirectory"/>. Kind: <see cref="ValidationError"/>.
    /// </summary>
    public const string InvalidAttachment = "mail.invalid_attachment";

    /// <summary>The IMAP configuration is incomplete or invalid. Kind: <see cref="ValidationError"/>.</summary>
    public const string ImapConfigInvalid = "mail.imap_config_invalid";

    /// <summary>The IMAP server rejected the credentials. Kind: <see cref="UnauthorizedError"/>.</summary>
    public const string ImapAuthenticationFailed = "mail.imap_authentication_failed";

    /// <summary>The IMAP operation timed out. Kind: <see cref="TimeoutError"/>.</summary>
    public const string ImapTimeout = "mail.imap_timeout";

    /// <summary>The IMAP server could not be reached (DNS, refused, unreachable). Kind: <see cref="UnavailableError"/>.</summary>
    public const string ImapConnectionFailed = "mail.imap_connection_failed";

    /// <summary>A general IMAP protocol or state error. Kind: <see cref="InternalError"/>.</summary>
    public const string ImapError = "mail.imap_error";

    /// <summary>
    /// The IMAP server refused a command (a <c>NO</c> or <c>BAD</c> answer, e.g. creating a folder that exists).
    /// Kind: <see cref="ValidationError"/>; <see cref="Error.Details"/> carries the IMAP response.
    /// </summary>
    public const string ImapRejected = "mail.imap_rejected";

    /// <summary>The requested IMAP folder does not exist. Kind: <see cref="NotFoundError"/>.</summary>
    public const string FolderNotFound = "mail.folder_not_found";

    /// <summary>The requested IMAP message (by UID) does not exist. Kind: <see cref="NotFoundError"/>.</summary>
    public const string MessageNotFound = "mail.message_not_found";

    /// <summary>The operation was cancelled by the caller. Kind: <see cref="UnavailableError"/>.</summary>
    public const string Cancelled = "mail.cancelled";

    /// <summary>An unexpected error. Kind: <see cref="InternalError"/>.</summary>
    public const string Unknown = "mail.unknown";
}

/// <summary>Builds CL.Mail errors so every code is always paired with the same kind.</summary>
internal static class MailErrors
{
    public static Error Validation(string code, string message, string? details = null) =>
        Error.Validation(code, message, details);

    public static Error NotFound(string code, string message) => Error.NotFound(code, message);

    public static Error Unauthorized(string code, string message) => Error.Unauthorized(code, message);

    public static Error Timeout(string code, string message) => Error.Timeout(code, message);

    public static Error Unavailable(string code, string message, Exception? ex = null) =>
        Error.Unavailable(code, message, ex?.Message);

    public static Error Cancelled() => Error.Unavailable(MailErrorCodes.Cancelled, "Operation cancelled");

    /// <summary>An <see cref="InternalError"/> describing <paramref name="ex"/>.</summary>
    public static Error Internal(string code, Exception ex) =>
        Error.Internal(code, ex.Message, ex.GetType().FullName);

    /// <summary>An <see cref="InternalError"/> with a fixed message.</summary>
    public static Error Internal(string code, string message) => Error.Internal(code, message);
}
