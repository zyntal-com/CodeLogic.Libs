using CodeLogic.Core.Configuration;
using System.ComponentModel.DataAnnotations;

namespace CL.Mail.Models;

/// <summary>
/// Root configuration for <c>CL.Mail</c>.
/// Serialized to / from <c>config.mail.json</c> in the library's config directory.
/// </summary>
[ConfigSection("mail")]
public sealed class MailConfig : ConfigModelBase
{
    /// <summary>Whether the library is active. When false, all operations are no-ops.</summary>
    [ConfigField(Label = "Enabled", Description = "Master switch for mail delivery and inbox polling.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SMTP server settings used for sending mail.
    /// </summary>
    public SmtpConfig Smtp { get; set; } = new();

    /// <summary>
    /// IMAP server settings used for receiving mail.
    /// Null disables IMAP support entirely.
    /// </summary>
    public ImapConfig? Imap { get; set; }

    /// <summary>
    /// Default sender e-mail address used when a message does not specify one explicitly: the builders returned by
    /// <c>MailLibrary.CreateMessage()</c> and used by <c>SendTemplatedAsync</c> fall back to it in
    /// <see cref="Services.MailBuilder.Build"/>.
    /// </summary>
    [ConfigField(Label = "Default From Email", InputType = ConfigInputType.Email,
        Description = "Sender address used when a message doesn't set one explicitly.",
        Placeholder = "noreply@example.com", Group = "Defaults", Order = 10)]
    public string? DefaultFromEmail { get; set; }

    /// <summary>
    /// Default sender display name, used together with <see cref="DefaultFromEmail"/> when a message does not set a
    /// sender.
    /// </summary>
    [ConfigField(Label = "Default From Name", Placeholder = "Example Team", Group = "Defaults", Order = 11)]
    public string? DefaultFromName { get; set; }

    /// <summary>
    /// Directory (relative to the library's data directory) where template files are stored.
    /// </summary>
    [ConfigField(Label = "Template Directory", Description = "Folder (relative to the library's data directory) holding mail templates.",
        Group = "Defaults", Order = 12)]
    public string TemplateDirectory { get; set; } = "templates";

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (!Enabled) return ConfigValidationResult.Valid();

        if (string.IsNullOrWhiteSpace(Smtp.Host))
            errors.Add("Smtp.Host is required.");

        if (Smtp.Port is < 1 or > 65535)
            errors.Add("Smtp.Port must be between 1 and 65535.");

        if (!Enum.IsDefined(Smtp.SecurityMode))
            errors.Add($"Smtp.SecurityMode '{Smtp.SecurityMode}' is not a known security mode.");

        if (!string.IsNullOrWhiteSpace(Smtp.AttachmentBaseDirectory) && !Path.IsPathFullyQualified(Smtp.AttachmentBaseDirectory))
            errors.Add("Smtp.AttachmentBaseDirectory must be a fully qualified path.");

        if (!string.IsNullOrWhiteSpace(DefaultFromEmail) && !Services.SmtpService.IsValidAddress(DefaultFromEmail))
            errors.Add("DefaultFromEmail is not a valid e-mail address.");

        if (DefaultFromName is not null && DefaultFromName.AsSpan().IndexOfAny('\r', '\n') >= 0)
            errors.Add("DefaultFromName cannot contain a line break.");

        if (Imap is not null)
        {
            if (string.IsNullOrWhiteSpace(Imap.Host))
                errors.Add("Imap.Host is required.");

            if (Imap.Port is < 1 or > 65535)
                errors.Add("Imap.Port must be between 1 and 65535.");

            if (!Enum.IsDefined(Imap.SecurityMode))
                errors.Add($"Imap.SecurityMode '{Imap.SecurityMode}' is not a known security mode.");
        }

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// SMTP server connection settings.
/// </summary>
public sealed class SmtpConfig
{
    /// <summary>SMTP server hostname or IP (e.g., "smtp.gmail.com").</summary>
    [ConfigField(Label = "SMTP Host", Required = true, Placeholder = "smtp.gmail.com",
        RequiresRestart = true, Group = "SMTP", Order = 20)]
    public string Host { get; set; } = "smtp.example.com";

    /// <summary>SMTP server port. Common values: 25 (plain), 465 (SSL), 587 (STARTTLS).</summary>
    [Range(1, 65535)]
    [ConfigField(Label = "SMTP Port", Min = 1, Max = 65535,
        Description = "Common values: 587 (STARTTLS), 465 (SSL/TLS), 25 (plain).",
        RequiresRestart = true, Group = "SMTP", Order = 21)]
    public int Port { get; set; } = 587;

    /// <summary>SMTP authentication username.</summary>
    [ConfigField(Label = "SMTP Username", Group = "SMTP", Order = 22)]
    public string Username { get; set; } = "";

    /// <summary>SMTP authentication password or App Password.</summary>
    [ConfigField(Label = "SMTP Password", InputType = ConfigInputType.Password, Secret = true,
        Description = "Use an app password for Gmail/Office365 accounts with 2FA.",
        Group = "SMTP", Order = 23)]
    public string Password { get; set; } = "";

    /// <summary>Transport security mode.</summary>
    [ConfigField(Label = "SMTP Security", Description = "Transport encryption: None / StartTls (upgrade in-band) / SslTls (implicit TLS).",
        Group = "SMTP", Order = 24)]
    public MailSecurityMode SecurityMode { get; set; } = MailSecurityMode.StartTls;

    /// <summary>Socket-level operation timeout in seconds.</summary>
    [Range(1, 300)]
    [ConfigField(Label = "SMTP Timeout (s)", Min = 1, Max = 300, Group = "SMTP", Order = 25, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// When set (a fully qualified directory), attachments must be files inside it; a path (or link target) outside
    /// fails with <see cref="MailErrorCodes.InvalidAttachment"/>. Set it when attachment paths can be influenced by
    /// users. Default: <c>null</c> (any fully qualified path).
    /// </summary>
    [ConfigField(Label = "Attachment Base Directory",
        Description = "If set, attachments must be files inside this directory.",
        Group = "SMTP", Order = 26, Collapsed = true)]
    public string? AttachmentBaseDirectory { get; set; }
}

/// <summary>
/// IMAP server connection settings.
/// </summary>
public sealed class ImapConfig
{
    /// <summary>IMAP server hostname or IP (e.g., "imap.gmail.com").</summary>
    [ConfigField(Label = "IMAP Host", Required = true, Placeholder = "imap.gmail.com",
        RequiresRestart = true, Group = "IMAP", Order = 30)]
    public string Host { get; set; } = "imap.example.com";

    /// <summary>IMAP server port. Common values: 143 (plain/STARTTLS), 993 (SSL).</summary>
    [Range(1, 65535)]
    [ConfigField(Label = "IMAP Port", Min = 1, Max = 65535,
        Description = "Common values: 993 (SSL/TLS), 143 (plain/STARTTLS).",
        RequiresRestart = true, Group = "IMAP", Order = 31)]
    public int Port { get; set; } = 993;

    /// <summary>IMAP authentication username.</summary>
    [ConfigField(Label = "IMAP Username", Group = "IMAP", Order = 32)]
    public string Username { get; set; } = "";

    /// <summary>IMAP authentication password or App Password.</summary>
    [ConfigField(Label = "IMAP Password", InputType = ConfigInputType.Password, Secret = true,
        Group = "IMAP", Order = 33)]
    public string Password { get; set; } = "";

    /// <summary>Transport security mode.</summary>
    [ConfigField(Label = "IMAP Security", Description = "Transport encryption: None / StartTls / SslTls.",
        Group = "IMAP", Order = 34)]
    public MailSecurityMode SecurityMode { get; set; } = MailSecurityMode.SslTls;

    /// <summary>Socket-level operation timeout in seconds.</summary>
    [Range(1, 300)]
    [ConfigField(Label = "IMAP Timeout (s)", Min = 1, Max = 300, Group = "IMAP", Order = 35, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether RFC 2177 IDLE push notifications are allowed. <c>ImapService.StartIdleAsync</c> fails with
    /// <see cref="MailErrorCodes.ImapConfigInvalid"/> while this is <c>false</c>.
    /// Requires the IMAP server to advertise the IDLE capability.
    /// </summary>
    [ConfigField(Label = "Enable IDLE", Description = "RFC 2177 push notifications. Requires server IDLE support.",
        Group = "IMAP", Order = 36)]
    public bool EnableIdle { get; set; } = false;

    /// <summary>
    /// How often (minutes) to re-send the IDLE command to keep the connection alive.
    /// RFC 2177 recommends less than 29 minutes.
    /// </summary>
    [Range(1, 28)]
    [ConfigField(Label = "IDLE Refresh (min)", Min = 1, Max = 28,
        Description = "How often to re-send IDLE to keep the connection alive.",
        Group = "IMAP", Order = 37, Collapsed = true)]
    public int IdleRefreshMinutes { get; set; } = 25;
}

/// <summary>
/// Transport security mode for SMTP and IMAP connections.
/// </summary>
public enum MailSecurityMode
{
    /// <summary>Unencrypted connection (not recommended).</summary>
    None,

    /// <summary>Upgrade to TLS in-band using the STARTTLS command.</summary>
    StartTls,

    /// <summary>Connect over an implicit TLS/SSL socket from the start.</summary>
    SslTls
}
