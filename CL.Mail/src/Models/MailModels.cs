namespace CL.Mail.Models;

// ── Outgoing message ──────────────────────────────────────────────────────────

/// <summary>
/// An immutable outgoing e-mail message. Build one with <c>MailBuilder</c>.
/// </summary>
public sealed record MailMessage
{
    /// <summary>Sender e-mail address (e.g., "sender@example.com").</summary>
    public required string From { get; init; }

    /// <summary>Optional sender display name shown in mail clients.</summary>
    public string? FromName { get; init; }

    /// <summary>Primary recipient addresses.</summary>
    public required IReadOnlyList<string> To { get; init; }

    /// <summary>Carbon-copy recipient addresses.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Blind carbon-copy recipient addresses.</summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>Message subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>Plain-text body. At least one of <see cref="TextBody"/> or <see cref="HtmlBody"/> is required.</summary>
    public string? TextBody { get; init; }

    /// <summary>HTML body. At least one of <see cref="TextBody"/> or <see cref="HtmlBody"/> is required.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Absolute file paths of attachments to include.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>Custom message headers (e.g., X-Mailer).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Message importance / priority.</summary>
    public MailPriority Priority { get; init; } = MailPriority.Normal;

    /// <summary>UTC time when this object was created.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Message importance level.</summary>
public enum MailPriority
{
    /// <summary>Low importance.</summary>
    Low,
    /// <summary>Normal importance (default).</summary>
    Normal,
    /// <summary>High importance.</summary>
    High
}

// ── Received message ──────────────────────────────────────────────────────────

/// <summary>
/// An e-mail message received from an IMAP server.
/// </summary>
public sealed record ReceivedMessage
{
    /// <summary>Value of the Message-ID header.</summary>
    public string? MessageId { get; init; }

    /// <summary>IMAP UID within the folder.</summary>
    public uint Uid { get; init; }

    /// <summary>Sender e-mail address.</summary>
    public string? From { get; init; }

    /// <summary>Sender display name.</summary>
    public string? FromName { get; init; }

    /// <summary>Primary recipient addresses.</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>Carbon-copy recipient addresses.</summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>Message subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Plain-text body. Null if the body was not fetched.</summary>
    public string? TextBody { get; init; }

    /// <summary>HTML body. Null if the body was not fetched.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Date the message was sent (from the Envelope).</summary>
    public DateTimeOffset Date { get; init; }

    /// <summary>IMAP flags currently set on this message.</summary>
    public MessageFlags Flags { get; init; } = MessageFlags.None;

    /// <summary>Full IMAP folder path where this message lives.</summary>
    public string? Folder { get; init; }

    /// <summary>Attachments found on the message.</summary>
    public IReadOnlyList<ReceivedAttachment> Attachments { get; init; } = [];
}

/// <summary>An attachment on a received e-mail message.</summary>
public sealed record ReceivedAttachment
{
    /// <summary>Attachment file name as declared in the MIME part.</summary>
    public string? FileName { get; init; }

    /// <summary>MIME content type (e.g., "application/pdf").</summary>
    public string? ContentType { get; init; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Raw decoded content. Null if not downloaded.</summary>
    public byte[]? Content { get; init; }
}

/// <summary>IMAP message flags.</summary>
[Flags]
public enum MessageFlags
{
    /// <summary>No flags set.</summary>
    None = 0,
    /// <summary>Message has been read.</summary>
    Seen = 1,
    /// <summary>Message is flagged / starred.</summary>
    Flagged = 2,
    /// <summary>Message has been replied to.</summary>
    Answered = 4,
    /// <summary>Message is marked for deletion.</summary>
    Deleted = 8,
    /// <summary>Message is a draft.</summary>
    Draft = 16
}

// ── Folder ────────────────────────────────────────────────────────────────────

/// <summary>Represents an IMAP mailbox folder.</summary>
public sealed record MailFolder
{
    /// <summary>Short display name (e.g., "INBOX").</summary>
    public required string Name { get; init; }

    /// <summary>Full IMAP path (e.g., "INBOX/Sent").</summary>
    public required string FullName { get; init; }

    /// <summary>Total message count.</summary>
    public int MessageCount { get; init; }

    /// <summary>Unread message count.</summary>
    public int UnreadCount { get; init; }

    /// <summary>Whether this folder can be opened for message access.</summary>
    public bool CanSelect { get; init; }
}

// ── Templates ─────────────────────────────────────────────────────────────────

/// <summary>
/// An e-mail template whose subject/body may contain variable placeholders
/// (<c>{{variable}}</c>, <c>${variable}</c>) plus conditionals and loops.
/// </summary>
public sealed record MailTemplate
{
    /// <summary>Unique template identifier used to load it by name.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string? Name { get; init; }

    /// <summary>Optional description of the template's purpose.</summary>
    public string? Description { get; init; }

    /// <summary>Subject line template — may contain variable placeholders.</summary>
    public required string Subject { get; init; }

    /// <summary>Plain-text body template. Null omits text part.</summary>
    public string? TextBody { get; init; }

    /// <summary>HTML body template. Null omits HTML part.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>
    /// Optional ID of a layout template to wrap this template in.
    /// The rendered body is passed to the layout as a <c>{{body}}</c> variable.
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>Variable names expected by this template (informational).</summary>
    public IReadOnlyList<string> Variables { get; init; } = [];

    /// <summary>UTC creation time.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>UTC time of last modification.</summary>
    public DateTime? ModifiedAt { get; init; }

    /// <summary>
    /// Creates a simple template without a layout.
    /// </summary>
    public static MailTemplate Create(
        string id,
        string subject,
        string? textBody = null,
        string? htmlBody = null,
        string? name = null,
        IReadOnlyList<string>? variables = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Subject = subject,
        TextBody = textBody,
        HtmlBody = htmlBody,
        Variables = variables ?? []
    };
}

/// <summary>The rendered output produced by the template engine.</summary>
public sealed record RenderedTemplate
{
    /// <summary>Rendered subject line with all variables substituted.</summary>
    public required string Subject { get; init; }

    /// <summary>Rendered plain-text body, or null when not present in the template.</summary>
    public string? TextBody { get; init; }

    /// <summary>Rendered HTML body, or null when not present in the template.</summary>
    public string? HtmlBody { get; init; }
}

// ── Send receipt ──────────────────────────────────────────────────────────────

/// <summary>
/// The success value of <c>SmtpService.SendAsync</c> and <c>MailLibrary.SendTemplatedAsync</c>.
/// </summary>
/// <param name="MessageId">The Message-ID header of the sent message, or null if none was assigned.</param>
/// <param name="SentAtUtc">UTC time the SMTP server accepted the message.</param>
public sealed record MailSendReceipt(string? MessageId, DateTime SentAtUtc);

// ── Search criteria ───────────────────────────────────────────────────────────

/// <summary>
/// Filter criteria for IMAP message searches.
/// All specified fields are ANDed together.
/// </summary>
public sealed class ImapSearchCriteria
{
    /// <summary>Subject contains filter (case-insensitive substring).</summary>
    public string? Subject { get; set; }

    /// <summary>From address contains filter.</summary>
    public string? From { get; set; }

    /// <summary>To address contains filter.</summary>
    public string? To { get; set; }

    /// <summary>Body contains filter.</summary>
    public string? Body { get; set; }

    /// <summary>Messages sent on or after this date.</summary>
    public DateTime? Since { get; set; }

    /// <summary>Messages sent strictly before this date.</summary>
    public DateTime? Before { get; set; }

    /// <summary>Flags that must be present on matching messages.</summary>
    public MessageFlags? HasFlags { get; set; }

    /// <summary>Flags that must be absent on matching messages.</summary>
    public MessageFlags? NotFlags { get; set; }

    /// <summary>When true, deleted messages are included in results.</summary>
    public bool IncludeDeleted { get; set; } = false;

    /// <summary>Returns criteria that matches every message.</summary>
    public static ImapSearchCriteria All() => new();

    /// <summary>Returns criteria that matches only unread messages.</summary>
    public static ImapSearchCriteria Unread() => new() { NotFlags = MessageFlags.Seen };
}
