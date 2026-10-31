# Receiving & Templates

> IMAP mailbox access — folders, fetch, search, manipulate, flags, and RFC 2177 IDLE — plus the JSON template engine and templated send.

This page covers the IMAP side of `CL.Mail` and its template system. For loading, sending, and the `Result` / `MailErrorCodes` model, see the **[Overview & Sending](index.md)** page.

## IMAP access

IMAP is available through `mail.Imap` when the `Imap` section is configured. Guard access with `mail.HasImap` — reading `Imap` when no section is present throws.

```csharp
if (!mail.HasImap)
    return;   // IMAP not configured

var connect = await mail.Imap.ConnectAsync();
if (connect.IsFailure)
    Console.WriteLine($"IMAP connect failed ({connect.Error!.Code}): {connect.Error.Message}");
```

Connections open on demand for the read/search/manipulate operations, so an explicit `ConnectAsync` is optional except when you want to surface connection errors up front or before starting IDLE. `DisconnectAsync()` closes the connection.

### Folders

```csharp
Result<IReadOnlyList<MailFolder>> folders = await mail.Imap.ListFoldersAsync();
foreach (var f in folders.Value!)
    Console.WriteLine($"{f.FullName}: {f.MessageCount} ({f.UnreadCount} unread)");

await mail.Imap.CreateFolderAsync("Projects");
await mail.Imap.RenameFolderAsync("Projects", "Active Projects");
await mail.Imap.DeleteFolderAsync("Active Projects");
```

### Fetch & page messages

`FetchMessagesAsync` returns a page of messages **newest first**. Pass `includeBody: false` (the default) for fast envelope-only listing; set it to `true` to pull bodies. `GetMessageAsync` retrieves a single message by UID with full body and attachment content.

```csharp
var page = await mail.Imap.FetchMessagesAsync(
    folder: "INBOX", offset: 0, count: 20, includeBody: false);

foreach (var msg in page.Value!)
{
    Console.WriteLine($"From:    {msg.FromName} <{msg.From}>");
    Console.WriteLine($"Subject: {msg.Subject}");
}

// Full message — body + attachment bytes — by UID:
var one = await mail.Imap.GetMessageAsync("INBOX", uid);
if (one.IsSuccess)
{
    foreach (var att in one.Value!.Attachments)
        Console.WriteLine($"{att.FileName} ({att.Size} bytes, {att.ContentType})");
}
```

### Search

`SearchAsync` ANDs all supplied criteria together. `ImapSearchCriteria.All()` and `.Unread()` cover the common cases; the rest are optional fields. Results default to a `maxResults` of 50.

```csharp
var hits = await mail.Imap.SearchAsync(
    folder: "INBOX",
    criteria: new ImapSearchCriteria
    {
        From     = "client@bigcorp.com",
        NotFlags = MessageFlags.Seen,            // unread only
        Since    = DateTime.UtcNow.AddDays(-7)
    },
    includeBody: false,
    maxResults: 50);
```

### Manipulate

```csharp
await mail.Imap.MoveMessageAsync("INBOX", uid, "INBOX/Archive");
await mail.Imap.CopyMessageAsync("INBOX", uid, "INBOX/Backup");
await mail.Imap.DeleteMessageAsync("INBOX", uid);   // marks \Deleted and expunges
```

### Flags

`SetMessageFlagsAsync` adds (`add: true`, the default) or removes (`add: false`) one or more `MessageFlags`.

```csharp
// Mark read:
await mail.Imap.SetMessageFlagsAsync("INBOX", uid, MessageFlags.Seen, add: true);

// Star + mark answered in one call (flags are [Flags]):
await mail.Imap.SetMessageFlagsAsync("INBOX", uid,
    MessageFlags.Flagged | MessageFlags.Answered, add: true);

// Un-read:
await mail.Imap.SetMessageFlagsAsync("INBOX", uid, MessageFlags.Seen, add: false);
```

### IDLE push (RFC 2177)

When the server advertises IDLE, `StartIdleAsync` monitors a folder on a background loop and raises `NewMailReceived` when new messages arrive. The connection auto-refreshes every `IdleRefreshMinutes` (RFC 2177 recommends staying under 29). IDLE also stops automatically when the library stops or the service is disposed.

```csharp
mail.Imap.NewMailReceived += (_, e) =>
    Console.WriteLine($"New mail in {e.FolderName} (now {e.MessageCount} messages)");

await mail.Imap.StartIdleAsync("INBOX");
// ... later ...
mail.Imap.StopIdle();
```

Set `Imap.EnableIdle` to `true` in configuration to allow IDLE; `StartIdleAsync` returns a failed `Result` (`MailErrorCodes.ImapError`) if the server does not advertise the capability.

## IMAP models

```csharp
public sealed record ReceivedMessage
{
    public string? MessageId { get; init; }
    public uint Uid { get; init; }
    public string? From { get; init; }
    public string? FromName { get; init; }
    public IReadOnlyList<string> To { get; init; }
    public IReadOnlyList<string> Cc { get; init; }
    public string? Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public DateTimeOffset Date { get; init; }
    public MessageFlags Flags { get; init; }
    public string? Folder { get; init; }
    public IReadOnlyList<ReceivedAttachment> Attachments { get; init; }
}

public sealed record ReceivedAttachment
{
    public string? FileName { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }
    public byte[]? Content { get; init; }   // populated by GetMessageAsync
}

public sealed record MailFolder
{
    public string Name { get; init; }
    public string FullName { get; init; }
    public int MessageCount { get; init; }
    public int UnreadCount { get; init; }
    public bool CanSelect { get; init; }
}

[Flags]
public enum MessageFlags
{
    None = 0, Seen = 1, Flagged = 2, Answered = 4, Deleted = 8, Draft = 16
}
```

`ImapSearchCriteria` is a plain class — set only the fields you need:

```csharp
public sealed class ImapSearchCriteria
{
    public string? Subject { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Body { get; set; }
    public DateTime? Since { get; set; }
    public DateTime? Before { get; set; }
    public MessageFlags? HasFlags { get; set; }
    public MessageFlags? NotFlags { get; set; }
    public bool IncludeDeleted { get; set; } = false;

    public static ImapSearchCriteria All();      // match everything
    public static ImapSearchCriteria Unread();   // NotFlags = Seen
}
```

`NewMailEventArgs` carries `FolderName` and `MessageCount`.

### IMAP service methods

| Method | Returns |
|--------|---------|
| `ConnectAsync(ct)` | `Result` |
| `DisconnectAsync(ct)` | `Result` |
| `ListFoldersAsync(ct)` | `Result<IReadOnlyList<MailFolder>>` |
| `CreateFolderAsync(name, ct)` | `Result` |
| `RenameFolderAsync(oldName, newName, ct)` | `Result` |
| `DeleteFolderAsync(fullName, ct)` | `Result` |
| `FetchMessagesAsync(folder, offset, count, includeBody = false, ct)` | `Result<IReadOnlyList<ReceivedMessage>>` |
| `GetMessageAsync(folder, uid, ct)` | `Result<ReceivedMessage>` |
| `SearchAsync(folder, criteria, includeBody = false, maxResults = 50, ct)` | `Result<IReadOnlyList<ReceivedMessage>>` |
| `MoveMessageAsync(source, uid, destination, ct)` | `Result` |
| `CopyMessageAsync(source, uid, destination, ct)` | `Result` |
| `DeleteMessageAsync(folder, uid, ct)` | `Result` |
| `SetMessageFlagsAsync(folder, uid, flags, add = true, ct)` | `Result` |
| `StartIdleAsync(folder, ct)` | `Result` |
| `StopIdle()` | `void` |

## Templates

Templates are JSON files (`{templateId}.json`) in the configured `TemplateDirectory`, served by the built-in `FileMailTemplateProvider`. Each has a subject and an optional text and/or HTML body. The `SimpleTemplateEngine` (Name `"Simple"`) substitutes variables and processes conditionals, loops, sections, and layouts.

### Syntax

| Syntax | Meaning |
|--------|---------|
| `{{var}}`, `${var}`, `{var}` | Variable substitution (case-insensitive lookup). HTML-encoded in `HtmlBody` and HTML layouts. |
| `{{{var}}}` | Substitution without encoding, for values that are trusted HTML. |
| `{{#if var}}…{{/if}}` | Conditional block, rendered when `var` is truthy. |
| `{{#if var}}…{{#else}}…{{/if}}` | Conditional with an else branch. |
| `{{#each items}}…{{/each}}` | Loop over a collection; item properties become variables inside the block. |
| `{{#section name}}…{{/section}}` | Named section injected into a layout. |

Unknown placeholders are left untouched. A value is **falsy** when it is `false`, `null`, `0`, an empty string, or the string `"false"`; everything else is truthy.

Loop items work for dictionaries and for plain objects (properties are read via reflection). A template may reference another template's id as its `Layout`; the layout receives the child body as `{{body}}` and each named `{{#section name}}…{{/section}}` as `{{name}}` (inserted as rendered, not encoded again; the text layout gets the text body's sections, the HTML layout the HTML body's).

Values are inserted once: placeholders inside a value are never expanded, so user text such as `${token}` stays literal. Subjects and text bodies are never encoded.

`templates/welcome.json`:

```json
{
  "Id": "welcome",
  "Name": "Welcome Email",
  "Subject": "Welcome, {{name}}!",
  "HtmlBody": "<h1>Welcome, {{name}}!</h1>\n<p>Created on {{date}}.</p>\n{{#if isPremium}}<p>You have <strong>premium</strong> access.</p>{{#else}}<p>Upgrade any time.</p>{{/if}}\n<ul>{{#each items}}<li>{{Name}} — {{Price}}</li>{{/each}}</ul>",
  "TextBody": "Welcome, {{name}}! Activate: {{activationUrl}}"
}
```

### Templated send

`SendTemplatedAsync` loads the template by id, renders it with the supplied variables, and sends. The `configure` callback receives the `MailBuilder` with subject and bodies pre-filled, so you set sender and recipients there.

```csharp
var result = await mail.SendTemplatedAsync(
    templateId: "welcome",
    variables: new Dictionary<string, object?>
    {
        ["name"]          = "Alice",
        ["date"]          = DateTime.UtcNow.ToString("MMMM d, yyyy"),
        ["activationUrl"] = "https://myapp.com/activate?token=abc123",
        ["isPremium"]     = true,
        ["items"]         = new[]
        {
            new { Name = "Pro plan", Price = "$9/mo" },
            new { Name = "Add-on",   Price = "$2/mo" },
        }
    },
    configure: builder => builder
        .From("noreply@example.com", "My App")
        .To("alice@example.com"));
```

### Provider & engine

`IMailTemplateProvider` (impl `FileMailTemplateProvider`) loads, lists, and saves templates; `IMailTemplateEngine` (impl `SimpleTemplateEngine`) renders a `MailTemplate` without sending.

```csharp
// Create + save from code:
var template = MailTemplate.Create(
    id: "welcome",
    subject: "Welcome, {{name}}!",
    htmlBody: "<h1>Welcome, {{name}}!</h1>",
    textBody: "Welcome, {{name}}!");
await mail.TemplateProvider.SaveTemplateAsync(template);

// List available ids:
Result<IReadOnlyList<string>> ids = await mail.TemplateProvider.ListTemplatesAsync();

// Load + render without sending:
var load = await mail.TemplateProvider.LoadTemplateAsync("welcome");
if (load.IsSuccess)
{
    Result<RenderedTemplate> rendered = await mail.TemplateEngine.RenderAsync(
        load.Value!,
        new Dictionary<string, object?> { ["name"] = "Alice" });
    // rendered.Value!.Subject / .HtmlBody / .TextBody
}
```

### Template models

```csharp
public sealed record MailTemplate
{
    public string Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public string? Layout { get; init; }                 // id of a layout template
    public IReadOnlyList<string> Variables { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }

    public static MailTemplate Create(
        string id, string subject,
        string? textBody = null, string? htmlBody = null,
        string? name = null, IReadOnlyList<string>? variables = null);
}

public sealed record RenderedTemplate
{
    public string Subject { get; init; }
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
}
```

## IMAP configuration

Add the `Imap` section to `config.mail.json` to enable reading. Omit it entirely to disable IMAP (and `mail.HasImap` reports `false`).

```json
{
  "Imap": {
    "Host": "imap.example.com",
    "Port": 993,
    "Username": "inbox@example.com",
    "Password": "your-password-or-app-password",
    "SecurityMode": "SslTls",
    "TimeoutSeconds": 30,
    "EnableIdle": false,
    "IdleRefreshMinutes": 25
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Imap.Host` | `string` | `imap.example.com` | IMAP server hostname. |
| `Imap.Port` | `int` | `993` | IMAP server port. |
| `Imap.Username` | `string` | `""` | IMAP username. |
| `Imap.Password` | `string` | `""` | IMAP password (use an app password where required). |
| `Imap.SecurityMode` | `MailSecurityMode` | `SslTls` | `None`, `StartTls`, or `SslTls` (implicit TLS, port 993). |
| `Imap.TimeoutSeconds` | `int` | `30` | IMAP connect/operation timeout in seconds. |
| `Imap.EnableIdle` | `bool` | `false` | Enable RFC 2177 IDLE push monitoring. |
| `Imap.IdleRefreshMinutes` | `int` | `25` | IDLE connection refresh interval (1–28; keep under 29). |

## See also

- [Overview & Sending](index.md) — loading, `MailBuilder`, SMTP, and the `Result` / `MailErrorCodes` model.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.Mail)
