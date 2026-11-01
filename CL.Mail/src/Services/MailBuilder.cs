using CL.Mail.Models;

namespace CL.Mail.Services;

/// <summary>
/// Fluent builder for constructing <see cref="MailMessage"/> objects.
/// <example>
/// <code>
/// var message = new MailBuilder()
///     .From("sender@example.com", "Sender Name")
///     .To("recipient@example.com")
///     .Subject("Hello!")
///     .HtmlBody("&lt;p&gt;Hello World&lt;/p&gt;")
///     .Build();
/// </code>
/// </example>
/// </summary>
public sealed class MailBuilder
{
    private readonly string? _defaultFrom;
    private readonly string? _defaultFromName;
    private string? _from;
    private string? _fromName;
    private readonly List<string> _to = [];
    private readonly List<string> _replyTo = [];
    private readonly List<string> _cc = [];
    private readonly List<string> _bcc = [];
    private string? _subject;
    private string? _textBody;
    private string? _htmlBody;
    private readonly List<string> _attachments = [];
    private readonly Dictionary<string, string> _headers = [];
    private MailPriority _priority = MailPriority.Normal;

    /// <summary>Creates a builder without a default sender: <see cref="From(string)"/> is required.</summary>
    public MailBuilder() { }

    /// <summary>
    /// Creates a builder that falls back to <paramref name="defaultFromEmail"/> / <paramref name="defaultFromName"/>
    /// when <see cref="From(string)"/> is not called (<c>MailLibrary.CreateMessage()</c> passes
    /// <see cref="MailConfig.DefaultFromEmail"/> and <see cref="MailConfig.DefaultFromName"/>).
    /// </summary>
    /// <param name="defaultFromEmail">Sender address used when none is set; <c>null</c> for none.</param>
    /// <param name="defaultFromName">Sender display name used with the default address.</param>
    public MailBuilder(string? defaultFromEmail, string? defaultFromName = null)
    {
        _defaultFrom = string.IsNullOrWhiteSpace(defaultFromEmail) ? null : defaultFromEmail;
        _defaultFromName = string.IsNullOrWhiteSpace(defaultFromName) ? null : defaultFromName;
    }

    /// <summary>Sets the sender e-mail address.</summary>
    public MailBuilder From(string email) { _from = email; return this; }

    /// <summary>Sets the sender e-mail address and display name.</summary>
    public MailBuilder From(string email, string displayName) { _from = email; _fromName = displayName; return this; }

    /// <summary>Adds a primary recipient.</summary>
    public MailBuilder To(string email) { if (!string.IsNullOrWhiteSpace(email)) _to.Add(email); return this; }

    /// <summary>Adds multiple primary recipients.</summary>
    public MailBuilder To(params string[] emails) { foreach (var e in emails.Where(x => !string.IsNullOrWhiteSpace(x))) _to.Add(e); return this; }

    /// <summary>Adds a Reply-To address.</summary>
    public MailBuilder ReplyTo(string email) { if (!string.IsNullOrWhiteSpace(email)) _replyTo.Add(email); return this; }

    /// <summary>Adds a CC recipient.</summary>
    public MailBuilder Cc(string email) { if (!string.IsNullOrWhiteSpace(email)) _cc.Add(email); return this; }

    /// <summary>Adds multiple CC recipients.</summary>
    public MailBuilder Cc(params string[] emails) { foreach (var e in emails.Where(x => !string.IsNullOrWhiteSpace(x))) _cc.Add(e); return this; }

    /// <summary>Adds a BCC recipient.</summary>
    public MailBuilder Bcc(string email) { if (!string.IsNullOrWhiteSpace(email)) _bcc.Add(email); return this; }

    /// <summary>Adds multiple BCC recipients.</summary>
    public MailBuilder Bcc(params string[] emails) { foreach (var e in emails.Where(x => !string.IsNullOrWhiteSpace(x))) _bcc.Add(e); return this; }

    /// <summary>Sets the subject line.</summary>
    public MailBuilder Subject(string subject) { _subject = subject; return this; }

    /// <summary>Sets the plain-text body.</summary>
    public MailBuilder TextBody(string body) { _textBody = body; return this; }

    /// <summary>Sets the HTML body.</summary>
    public MailBuilder HtmlBody(string body) { _htmlBody = body; return this; }

    /// <summary>Sets both the plain-text and HTML body at once.</summary>
    public MailBuilder Body(string textBody, string htmlBody) { _textBody = textBody; _htmlBody = htmlBody; return this; }

    /// <summary>Adds a file attachment by fully qualified path (checked when the message is sent).</summary>
    public MailBuilder Attach(string filePath) { if (!string.IsNullOrWhiteSpace(filePath)) _attachments.Add(filePath); return this; }

    /// <summary>Adds multiple file attachments.</summary>
    public MailBuilder Attach(params string[] filePaths) { foreach (var p in filePaths.Where(x => !string.IsNullOrWhiteSpace(x))) _attachments.Add(p); return this; }

    /// <summary>Adds a custom message header.</summary>
    public MailBuilder Header(string name, string value) { if (!string.IsNullOrWhiteSpace(name)) _headers[name] = value ?? ""; return this; }

    /// <summary>Sets the message priority.</summary>
    public MailBuilder Priority(MailPriority priority) { _priority = priority; return this; }

    /// <summary>
    /// Builds the immutable <see cref="MailMessage"/>.
    /// </summary>
    /// <remarks>
    /// Without <see cref="From(string)"/>, the default sender passed to the constructor is used (address and
    /// display name).
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when required fields are missing.</exception>
    public MailMessage Build()
    {
        var from = _from;
        var fromName = _fromName;
        if (string.IsNullOrWhiteSpace(from))
        {
            from = _defaultFrom;
            fromName ??= _defaultFromName;
        }

        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException("Sender e-mail must be specified (call From() or set MailConfig.DefaultFromEmail).");

        if (_to.Count == 0)
            throw new InvalidOperationException("At least one recipient must be specified (call To()).");

        if (string.IsNullOrWhiteSpace(_subject))
            throw new InvalidOperationException("Subject must be specified (call Subject()).");

        if (string.IsNullOrWhiteSpace(_textBody) && string.IsNullOrWhiteSpace(_htmlBody))
            throw new InvalidOperationException("At least one body (TextBody or HtmlBody) must be specified.");

        return new MailMessage
        {
            From = from,
            FromName = fromName,
            To = _to.AsReadOnly(),
            ReplyTo = _replyTo.AsReadOnly(),
            Cc = _cc.AsReadOnly(),
            Bcc = _bcc.AsReadOnly(),
            Subject = _subject,
            TextBody = _textBody,
            HtmlBody = _htmlBody,
            Attachments = _attachments.AsReadOnly(),
            Headers = _headers.AsReadOnly(),
            Priority = _priority
        };
    }
}
