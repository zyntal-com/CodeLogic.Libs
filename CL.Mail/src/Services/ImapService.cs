using CL.Mail.Models;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using System.Net.Sockets;
using MkFlags  = MailKit.MessageFlags;
using MkFolder = MailKit.MailFolder;
// Our model types that collide with MailKit names
using ModelMailFolder  = CL.Mail.Models.MailFolder;
using ModelMsgFlags    = CL.Mail.Models.MessageFlags;

namespace CL.Mail.Services;

/// <summary>
/// Event arguments raised when new mail arrives during IMAP IDLE monitoring.
/// </summary>
public sealed class NewMailEventArgs : EventArgs
{
    /// <summary>Full IMAP path of the folder where new mail arrived.</summary>
    public required string FolderName { get; init; }

    /// <summary>Total message count in the folder after the new messages.</summary>
    public int MessageCount { get; init; }
}

/// <summary>
/// Reads and manages e-mail via IMAP using <see href="https://github.com/jstedfast/MailKit">MailKit</see>.
/// Supports message fetching, search, flag operations, folder management, and RFC 2177 IDLE push.
/// </summary>
public sealed class ImapService : IDisposable
{
    private readonly ImapConfig _config;
    private readonly ILogger? _logger;
    private ImapClient? _client;
    private bool _disposed;

    private CancellationTokenSource? _idleCts;
    private Task? _idleTask;

    /// <summary>Fired when new mail is detected during IDLE monitoring.</summary>
    public event EventHandler<NewMailEventArgs>? NewMailReceived;

    /// <summary>Initialises the IMAP service with the given configuration.</summary>
    /// <param name="config">IMAP server settings.</param>
    /// <param name="logger">Optional logger.</param>
    public ImapService(ImapConfig config, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Connects and authenticates to the IMAP server.</summary>
    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _client = new ImapClient { Timeout = _config.TimeoutSeconds * 1000 };
            await _client.ConnectAsync(_config.Host, _config.Port, MapSecurity(_config.SecurityMode), cancellationToken).ConfigureAwait(false);
            await _client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Connected to IMAP {_config.Host}:{_config.Port}");
            return Result.Success();
        }
        catch (AuthenticationException ex)
        {
            _logger?.Error("IMAP authentication failed", ex);
            return MailErrors.Unauthorized(MailErrorCodes.ImapAuthenticationFailed, "Authentication failed");
        }
        catch (TimeoutException ex)
        {
            _logger?.Error("IMAP connection timed out", ex);
            return MailErrors.Timeout(MailErrorCodes.ImapTimeout, "Connection timed out");
        }
        catch (SocketException ex)
        {
            _logger?.Error($"IMAP server {_config.Host}:{_config.Port} unreachable", ex);
            return MailErrors.Unavailable(MailErrorCodes.ImapConnectionFailed,
                $"Could not connect to IMAP server {_config.Host}:{_config.Port}", ex);
        }
        catch (Exception ex)
        {
            _logger?.Error("IMAP connection error", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Gracefully disconnects from the IMAP server and stops IDLE if running.</summary>
    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StopIdle();
            if (_client is { IsConnected: true })
            {
                await _client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
                _logger?.Info("Disconnected from IMAP");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error("Error disconnecting from IMAP", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    /// <summary>Lists all selectable folders on the server including INBOX.</summary>
    public async Task<Result<IReadOnlyList<ModelMailFolder>>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var personal = _client!.GetFolder(_client.PersonalNamespaces[0]);
            var subfolders = await personal.GetSubfoldersAsync(true, cancellationToken).ConfigureAwait(false);
            var folders = new List<ModelMailFolder>();

            // INBOX
            var inbox = _client.Inbox;
            if (inbox is null) throw new InvalidOperationException("IMAP client Inbox is null");
            await inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, cancellationToken).ConfigureAwait(false);
            folders.Add(new ModelMailFolder { Name = inbox.Name, FullName = inbox.FullName, MessageCount = inbox.Count, UnreadCount = inbox.Unread, CanSelect = true });

            foreach (var f in subfolders)
            {
                try
                {
                    var canSelect = !f.Attributes.HasFlag(FolderAttributes.NoSelect);
                    var msgCount = 0; var unread = 0;
                    if (canSelect)
                    {
                        await f.StatusAsync(StatusItems.Count | StatusItems.Unread, cancellationToken).ConfigureAwait(false);
                        msgCount = f.Count; unread = f.Unread;
                    }
                    folders.Add(new ModelMailFolder { Name = f.Name, FullName = f.FullName, MessageCount = msgCount, UnreadCount = unread, CanSelect = canSelect });
                }
                catch (Exception ex) { _logger?.Warning($"Could not stat folder '{f.FullName}': {ex.Message}"); }
            }

            return Result<IReadOnlyList<ModelMailFolder>>.Success(folders.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger?.Error("Error listing folders", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Creates a new mail folder by name.</summary>
    public async Task<Result> CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var personal = _client!.GetFolder(_client.PersonalNamespaces[0]);
            await personal.CreateAsync(name, true, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Created folder: {name}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error creating folder '{name}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Deletes a folder by its full IMAP path.</summary>
    public async Task<Result> DeleteFolderAsync(string fullName, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var folder = await GetImapFolderAsync(fullName, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (folder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{fullName}' not found");
            await folder.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Deleted folder: {fullName}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error deleting folder '{fullName}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Renames a folder.</summary>
    public async Task<Result> RenameFolderAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var folder = await GetImapFolderAsync(oldName, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (folder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{oldName}' not found");
            if (folder.ParentFolder is null) return MailErrors.Internal(MailErrorCodes.ImapError, $"Folder '{oldName}' has no parent folder");
            await folder.RenameAsync(folder.ParentFolder, newName, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Renamed folder: {oldName} → {newName}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error renaming folder '{oldName}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    // ── Messages ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches a page of messages from a folder, newest first.
    /// </summary>
    /// <param name="folder">IMAP folder path (e.g., "INBOX").</param>
    /// <param name="offset">Zero-based page offset (0 = most recent messages).</param>
    /// <param name="count">Number of messages per page.</param>
    /// <param name="includeBody">When true, fetches the full body and attachments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result<IReadOnlyList<ReceivedMessage>>> FetchMessagesAsync(
        string folder, int offset, int count, bool includeBody = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null)
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            var total = imapFolder.Count;
            if (total == 0 || offset >= total)
                return Result<IReadOnlyList<ReceivedMessage>>.Success(Array.Empty<ReceivedMessage>());

            var startIdx = Math.Max(0, total - offset - count);
            var endIdx   = Math.Max(0, total - offset - 1);

            var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags;
            if (includeBody) items |= MessageSummaryItems.BodyStructure;

            var summaries = await imapFolder.FetchAsync(startIdx, endIdx, items, cancellationToken).ConfigureAwait(false);
            var messages = new List<ReceivedMessage>();

            foreach (var summary in summaries.Reverse())
                messages.Add(await ConvertSummaryAsync(imapFolder, summary, includeBody, cancellationToken).ConfigureAwait(false));

            return Result<IReadOnlyList<ReceivedMessage>>.Success(messages.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error fetching messages from '{folder}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Fetches a single message by UID with full body and attachment content.</summary>
    public async Task<Result<ReceivedMessage>> GetMessageAsync(
        string folder, uint uid, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null)
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            var uniqueId = new UniqueId(uid);
            var message = await imapFolder.GetMessageAsync(uniqueId, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return MailErrors.NotFound(MailErrorCodes.MessageNotFound, $"Message UID {uid} not found");

            var summaries = await imapFolder.FetchAsync(
                new[] { uniqueId }, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
                cancellationToken).ConfigureAwait(false);

            var flags = ConvertFlags(summaries.FirstOrDefault()?.Flags ?? MkFlags.None);

            var attachments = new List<ReceivedAttachment>();
            foreach (var attachment in message.Attachments)
            {
                if (attachment is MimePart part)
                {
                    using var stream = new MemoryStream();
                    await part.Content!.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
                    attachments.Add(new ReceivedAttachment
                    {
                        FileName = part.FileName,
                        ContentType = part.ContentType.MimeType,
                        Size = stream.Length,
                        Content = stream.ToArray()
                    });
                }
            }

            return new ReceivedMessage
            {
                MessageId = message.MessageId,
                Uid = uid,
                From = message.From.Mailboxes.FirstOrDefault()?.Address,
                FromName = message.From.Mailboxes.FirstOrDefault()?.Name,
                To = message.To.Mailboxes.Select(m => m.Address).ToList(),
                Cc = message.Cc.Mailboxes.Select(m => m.Address).ToList(),
                Subject = message.Subject,
                TextBody = message.TextBody,
                HtmlBody = message.HtmlBody,
                Date = message.Date,
                Flags = flags,
                Folder = folder,
                Attachments = attachments.AsReadOnly()
            };
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error getting message UID {uid} from '{folder}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Searches for messages matching the given criteria.</summary>
    /// <param name="folder">IMAP folder to search.</param>
    /// <param name="criteria">Search criteria (all fields ANDed).</param>
    /// <param name="includeBody">When true, fetches full body for each result.</param>
    /// <param name="maxResults">Maximum number of results to return (newest first).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result<IReadOnlyList<ReceivedMessage>>> SearchAsync(
        string folder, ImapSearchCriteria criteria,
        bool includeBody = false, int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null)
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            var query = BuildQuery(criteria);
            var uids = await imapFolder.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var limited = uids.Reverse().Take(maxResults).ToList();

            if (limited.Count == 0)
                return Result<IReadOnlyList<ReceivedMessage>>.Success(Array.Empty<ReceivedMessage>());

            var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags;
            if (includeBody) items |= MessageSummaryItems.BodyStructure;

            var summaries = await imapFolder.FetchAsync(limited, items, cancellationToken).ConfigureAwait(false);
            var messages = new List<ReceivedMessage>();

            foreach (var summary in summaries.Reverse())
                messages.Add(await ConvertSummaryAsync(imapFolder, summary, includeBody, cancellationToken).ConfigureAwait(false));

            return Result<IReadOnlyList<ReceivedMessage>>.Success(messages.AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error searching '{folder}'", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Moves a message from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public async Task<Result> MoveMessageAsync(string source, uint uid, string destination, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var src = await GetImapFolderAsync(source, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (src is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Source '{source}' not found");
            var dst = await GetImapFolderAsync(destination, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (dst is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Destination '{destination}' not found");

            await src.MoveToAsync(new UniqueId(uid), dst, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Moved UID {uid} from '{source}' to '{destination}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error moving message UID {uid}", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Copies a message from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public async Task<Result> CopyMessageAsync(string source, uint uid, string destination, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var src = await GetImapFolderAsync(source, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (src is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Source '{source}' not found");
            var dst = await GetImapFolderAsync(destination, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (dst is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Destination '{destination}' not found");

            await src.CopyToAsync(new UniqueId(uid), dst, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Copied UID {uid} from '{source}' to '{destination}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error copying message UID {uid}", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Permanently deletes a message by marking it \Deleted and expunging.</summary>
    public async Task<Result> DeleteMessageAsync(string folder, uint uid, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            await imapFolder.AddFlagsAsync(new UniqueId(uid), MkFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
            await imapFolder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Deleted UID {uid} from '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error deleting message UID {uid}", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Adds or removes flags on a message.</summary>
    /// <param name="folder">IMAP folder.</param>
    /// <param name="uid">Message UID.</param>
    /// <param name="flags">Flags to set or clear.</param>
    /// <param name="add">True to add flags; false to remove them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result> SetMessageFlagsAsync(
        string folder, uint uid, ModelMsgFlags flags, bool add = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            var mkFlags = ToMailKitFlags(flags);
            var uniqueId = new UniqueId(uid);

            if (add) await imapFolder.AddFlagsAsync(uniqueId, mkFlags, true, cancellationToken).ConfigureAwait(false);
            else     await imapFolder.RemoveFlagsAsync(uniqueId, mkFlags, true, cancellationToken).ConfigureAwait(false);

            _logger?.Info($"{(add ? "Added" : "Removed")} flags {flags} on UID {uid} in '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error setting flags on UID {uid}", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    // ── IDLE ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts RFC 2177 IDLE monitoring on the given folder.
    /// Fires <see cref="NewMailReceived"/> when new messages arrive.
    /// </summary>
    /// <param name="folder">IMAP folder to monitor (typically "INBOX").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result> StartIdleAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (_idleTask is not null)
            return MailErrors.Internal(MailErrorCodes.ImapError, "IDLE already running — call StopIdle() first");

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            if (!_client!.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                _logger?.Warning("IMAP server does not support IDLE");
                return MailErrors.Internal(MailErrorCodes.ImapError, "Server does not support IDLE");
            }

            var imapFolder = await GetImapFolderAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null)
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            _idleCts = new CancellationTokenSource();
            var idleToken = _idleCts.Token;
            var refresh = TimeSpan.FromMinutes(_config.IdleRefreshMinutes);

            _idleTask = Task.Run(async () =>
            {
                while (!idleToken.IsCancellationRequested)
                {
                    try
                    {
                        await EnsureConnectedAsync(idleToken).ConfigureAwait(false);
                        if (!imapFolder.IsOpen)
                            await imapFolder.OpenAsync(FolderAccess.ReadOnly, idleToken).ConfigureAwait(false);

                        var prevCount = imapFolder.Count;

                        using var doneCts = new CancellationTokenSource(refresh);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(idleToken, doneCts.Token);

                        try { await _client!.IdleAsync(doneCts.Token, linked.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (doneCts.IsCancellationRequested && !idleToken.IsCancellationRequested) { /* refresh — normal */ }

                        if (imapFolder.Count > prevCount)
                        {
                            _logger?.Info($"New mail in '{folder}': {imapFolder.Count} messages");
                            NewMailReceived?.Invoke(this, new NewMailEventArgs { FolderName = folder, MessageCount = imapFolder.Count });
                        }
                    }
                    catch (OperationCanceledException) when (idleToken.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        _logger?.Error($"IDLE error, retrying: {ex.Message}");
                        try { await Task.Delay(5_000, idleToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }

                _logger?.Info("IDLE monitoring stopped");
            }, idleToken);

            _logger?.Info($"Started IDLE on '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error("Error starting IDLE", ex);
            return MailErrors.Internal(MailErrorCodes.ImapError, ex);
        }
    }

    /// <summary>Stops the IDLE background loop and waits for it to complete (up to 5 s).</summary>
    public void StopIdle()
    {
        if (_idleCts is null) return;
        _idleCts.Cancel();
        try { _idleTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _idleCts.Dispose();
        _idleCts = null;
        _idleTask = null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true }) return;
        _logger?.Info("IMAP reconnecting...");
        _client?.Dispose();
        var result = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"IMAP reconnect failed: {result.Error!.Code}: {result.Error.Message}");
    }

    private async Task<IMailFolder?> GetImapFolderAsync(string name, FolderAccess access, CancellationToken ct)
    {
        try
        {
            IMailFolder? folder = string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? _client!.Inbox
                : await _client!.GetFolderAsync(name, ct).ConfigureAwait(false);

            if (folder is null) return null;

            if (access != FolderAccess.None)
            {
                if (!folder.IsOpen)
                    await folder.OpenAsync(access, ct).ConfigureAwait(false);
                else if (folder.Access < access)
                {
                    await folder.CloseAsync(false, ct).ConfigureAwait(false);
                    await folder.OpenAsync(access, ct).ConfigureAwait(false);
                }
            }

            return folder;
        }
        catch (FolderNotFoundException) { return null; }
        catch (Exception ex)
        {
            _logger?.Warning($"Error resolving folder '{name}': {ex.Message}");
            return null;
        }
    }

    private async Task<ReceivedMessage> ConvertSummaryAsync(
        IMailFolder folder, IMessageSummary summary, bool includeBody, CancellationToken ct)
    {
        string? textBody = null; string? htmlBody = null;
        var attachments = new List<ReceivedAttachment>();

        if (includeBody && summary.UniqueId.IsValid)
        {
            try
            {
                var msg = await folder.GetMessageAsync(summary.UniqueId, ct).ConfigureAwait(false);
                textBody = msg.TextBody;
                htmlBody = msg.HtmlBody;
                foreach (var att in msg.Attachments)
                    if (att is MimePart part)
                        attachments.Add(new ReceivedAttachment { FileName = part.FileName, ContentType = part.ContentType.MimeType });
            }
            catch (Exception ex) { _logger?.Warning($"Could not fetch body for UID {summary.UniqueId}: {ex.Message}"); }
        }

        var env = summary.Envelope;
        if (env is null) throw new InvalidOperationException($"Message summary for UID {summary.UniqueId} has no envelope");
        return new ReceivedMessage
        {
            MessageId = env.MessageId,
            Uid = summary.UniqueId.Id,
            From = env.From?.Mailboxes.FirstOrDefault()?.Address,
            FromName = env.From?.Mailboxes.FirstOrDefault()?.Name,
            To = env.To?.Mailboxes.Select(m => m.Address).ToList() ?? [],
            Cc = env.Cc?.Mailboxes.Select(m => m.Address).ToList() ?? [],
            Subject = env.Subject,
            TextBody = textBody,
            HtmlBody = htmlBody,
            Date = env.Date ?? DateTimeOffset.MinValue,
            Flags = ConvertFlags(summary.Flags ?? MkFlags.None),
            Folder = folder.FullName,
            Attachments = attachments.AsReadOnly()
        };
    }

    private static ModelMsgFlags ConvertFlags(MkFlags mk)
    {
        var f = ModelMsgFlags.None;
        if (mk.HasFlag(MkFlags.Seen))     f |= ModelMsgFlags.Seen;
        if (mk.HasFlag(MkFlags.Flagged))  f |= ModelMsgFlags.Flagged;
        if (mk.HasFlag(MkFlags.Answered)) f |= ModelMsgFlags.Answered;
        if (mk.HasFlag(MkFlags.Deleted))  f |= ModelMsgFlags.Deleted;
        if (mk.HasFlag(MkFlags.Draft))    f |= ModelMsgFlags.Draft;
        return f;
    }

    private static MkFlags ToMailKitFlags(ModelMsgFlags f)
    {
        var mk = MkFlags.None;
        if (f.HasFlag(ModelMsgFlags.Seen))     mk |= MkFlags.Seen;
        if (f.HasFlag(ModelMsgFlags.Flagged))  mk |= MkFlags.Flagged;
        if (f.HasFlag(ModelMsgFlags.Answered)) mk |= MkFlags.Answered;
        if (f.HasFlag(ModelMsgFlags.Deleted))  mk |= MkFlags.Deleted;
        if (f.HasFlag(ModelMsgFlags.Draft))    mk |= MkFlags.Draft;
        return mk;
    }

    private static SearchQuery BuildQuery(ImapSearchCriteria c)
    {
        var q = c.IncludeDeleted ? SearchQuery.All : SearchQuery.NotDeleted;

        if (!string.IsNullOrWhiteSpace(c.Subject)) q = q.And(SearchQuery.SubjectContains(c.Subject));
        if (!string.IsNullOrWhiteSpace(c.From))    q = q.And(SearchQuery.FromContains(c.From));
        if (!string.IsNullOrWhiteSpace(c.To))      q = q.And(SearchQuery.ToContains(c.To));
        if (!string.IsNullOrWhiteSpace(c.Body))    q = q.And(SearchQuery.BodyContains(c.Body));

        if (c.Since.HasValue)  q = q.And(SearchQuery.SentSince(c.Since.Value));
        if (c.Before.HasValue) q = q.And(SearchQuery.SentBefore(c.Before.Value));

        if (c.HasFlags.HasValue)
        {
            if (c.HasFlags.Value.HasFlag(ModelMsgFlags.Seen))     q = q.And(SearchQuery.Seen);
            if (c.HasFlags.Value.HasFlag(ModelMsgFlags.Flagged))  q = q.And(SearchQuery.Flagged);
            if (c.HasFlags.Value.HasFlag(ModelMsgFlags.Answered)) q = q.And(SearchQuery.Answered);
            if (c.HasFlags.Value.HasFlag(ModelMsgFlags.Draft))    q = q.And(SearchQuery.Draft);
        }

        if (c.NotFlags.HasValue)
        {
            if (c.NotFlags.Value.HasFlag(ModelMsgFlags.Seen))     q = q.And(SearchQuery.NotSeen);
            if (c.NotFlags.Value.HasFlag(ModelMsgFlags.Flagged))  q = q.And(SearchQuery.NotFlagged);
            if (c.NotFlags.Value.HasFlag(ModelMsgFlags.Answered)) q = q.And(SearchQuery.NotAnswered);
            if (c.NotFlags.Value.HasFlag(ModelMsgFlags.Draft))    q = q.And(SearchQuery.NotDraft);
        }

        return q;
    }

    private static SecureSocketOptions MapSecurity(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.None     => SecureSocketOptions.None,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailSecurityMode.SslTls   => SecureSocketOptions.SslOnConnect,
        _                         => SecureSocketOptions.Auto
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdle();
        _client?.Dispose();
        _client = null;
        GC.SuppressFinalize(this);
    }
}
