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
/// <para>
/// One IMAP connection serves every call. Calls are serialized, so the service is safe to use from several
/// threads; while IDLE is running, a call interrupts it, runs, and IDLE resumes afterwards.
/// </para>
/// </summary>
public sealed class ImapService : IDisposable
{
    private readonly ImapConfig _config;
    private readonly ILogger? _logger;
    private ImapClient? _client;
    private volatile bool _disposed;

    // Serializes every use of _client: public calls and the IDLE loop.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Public calls waiting for (or holding) the gate; the IDLE loop does not start an IDLE while any are waiting.
    private int _pendingCalls;
    // Ends the IDLE command in progress gracefully (DONE) so a waiting call can take the connection.
    private CancellationTokenSource? _idleDone;

    private readonly Lock _idleLock = new();
    private CancellationTokenSource? _idleCts;    // stop requested
    private CancellationTokenSource? _idleAbort;  // hard abort when a graceful stop does not finish in time
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

    /// <summary>
    /// Connects and authenticates to the IMAP server. An existing connection is closed and replaced.
    /// </summary>
    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "IMAP connection error");
        }
    }

    /// <summary>Gracefully disconnects from the IMAP server and stops IDLE if running.</summary>
    public async Task<Result> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StopIdle();
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            if (_client is { IsConnected: true })
            {
                await _client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
                _logger?.Info("Disconnected from IMAP");
            }
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "Error disconnecting from IMAP");
        }
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    /// <summary>Lists all selectable folders on the server including INBOX.</summary>
    public async Task<Result<IReadOnlyList<ModelMailFolder>>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            var personal = client.GetFolder(client.PersonalNamespaces[0]);
            var subfolders = await personal.GetSubfoldersAsync(true, cancellationToken).ConfigureAwait(false);
            var folders = new List<ModelMailFolder>();

            // INBOX
            var inbox = client.Inbox ?? throw new InvalidOperationException("IMAP client Inbox is null");
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.Warning($"Could not stat folder '{f.FullName}': {ex.Message}");
                }
            }

            return Result<IReadOnlyList<ModelMailFolder>>.Success(folders.AsReadOnly());
        }
        catch (Exception ex)
        {
            return Fail(ex, "Error listing folders");
        }
    }

    /// <summary>Creates a new mail folder by name.</summary>
    public async Task<Result> CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var personal = client.GetFolder(client.PersonalNamespaces[0]);
            await personal.CreateAsync(name, true, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Created folder: {name}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error creating folder '{name}'");
        }
    }

    /// <summary>Deletes a folder by its full IMAP path.</summary>
    public async Task<Result> DeleteFolderAsync(string fullName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var folder = await GetImapFolderAsync(client, fullName, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (folder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{fullName}' not found");
            await folder.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Deleted folder: {fullName}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error deleting folder '{fullName}'");
        }
    }

    /// <summary>Renames a folder.</summary>
    public async Task<Result> RenameFolderAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var folder = await GetImapFolderAsync(client, oldName, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (folder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{oldName}' not found");
            if (folder.ParentFolder is null) return MailErrors.Internal(MailErrorCodes.ImapError, $"Folder '{oldName}' has no parent folder");
            await folder.RenameAsync(folder.ParentFolder, newName, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Renamed folder: {oldName} → {newName}");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error renaming folder '{oldName}'");
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
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
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
            return Fail(ex, $"Error fetching messages from '{folder}'");
        }
    }

    /// <summary>Fetches a single message by UID with full body and attachment content.</summary>
    public async Task<Result<ReceivedMessage>> GetMessageAsync(
        string folder, uint uid, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
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
            return Fail(ex, $"Error getting message UID {uid} from '{folder}'");
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
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
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
            return Fail(ex, $"Error searching '{folder}'");
        }
    }

    /// <summary>Moves a message from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public async Task<Result> MoveMessageAsync(string source, uint uid, string destination, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var src = await GetImapFolderAsync(client, source, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (src is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Source '{source}' not found");
            var dst = await GetImapFolderAsync(client, destination, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (dst is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Destination '{destination}' not found");

            await src.MoveToAsync(new UniqueId(uid), dst, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Moved UID {uid} from '{source}' to '{destination}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error moving message UID {uid}");
        }
    }

    /// <summary>Copies a message from <paramref name="source"/> to <paramref name="destination"/>.</summary>
    public async Task<Result> CopyMessageAsync(string source, uint uid, string destination, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var src = await GetImapFolderAsync(client, source, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (src is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Source '{source}' not found");
            var dst = await GetImapFolderAsync(client, destination, FolderAccess.None, cancellationToken).ConfigureAwait(false);
            if (dst is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Destination '{destination}' not found");

            await src.CopyToAsync(new UniqueId(uid), dst, cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Copied UID {uid} from '{source}' to '{destination}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error copying message UID {uid}");
        }
    }

    /// <summary>Permanently deletes a message by marking it \Deleted and expunging.</summary>
    public async Task<Result> DeleteMessageAsync(string folder, uint uid, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            if (imapFolder is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");

            await imapFolder.AddFlagsAsync(new UniqueId(uid), MkFlags.Deleted, true, cancellationToken).ConfigureAwait(false);
            await imapFolder.ExpungeAsync(cancellationToken).ConfigureAwait(false);
            _logger?.Info($"Deleted UID {uid} from '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, $"Error deleting message UID {uid}");
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
            using var _ = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
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
            return Fail(ex, $"Error setting flags on UID {uid}");
        }
    }

    // ── IDLE ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts RFC 2177 IDLE monitoring on the given folder.
    /// Fires <see cref="NewMailReceived"/> when new messages arrive.
    /// <para>
    /// Requires <see cref="ImapConfig.EnableIdle"/>; when it is <c>false</c> the call fails with
    /// <see cref="MailErrorCodes.ImapConfigInvalid"/>. The loop reconnects after a dropped connection and
    /// re-opens the folder on the new connection. Other calls on this service interrupt IDLE while they run.
    /// </para>
    /// </summary>
    /// <param name="folder">IMAP folder to monitor (typically "INBOX").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Result> StartIdleAsync(string folder, CancellationToken cancellationToken = default)
    {
        if (!_config.EnableIdle)
            return MailErrors.Validation(MailErrorCodes.ImapConfigInvalid, "IDLE is disabled: set Imap.EnableIdle to true");

        lock (_idleLock)
        {
            if (_idleTask is not null)
                return MailErrors.Internal(MailErrorCodes.ImapError, "IDLE already running — call StopIdle() first");
            _idleTask = Task.CompletedTask; // reserve; replaced below or cleared on failure
        }

        var started = false;
        try
        {
            int initialCount;
            using (await LockAsync(cancellationToken).ConfigureAwait(false))
            {
                var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    _logger?.Warning("IMAP server does not support IDLE");
                    return MailErrors.Internal(MailErrorCodes.ImapError, "Server does not support IDLE");
                }

                var imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
                if (imapFolder is null)
                    return MailErrors.NotFound(MailErrorCodes.FolderNotFound, $"Folder '{folder}' not found");
                initialCount = imapFolder.Count;
            }

            var stop = new CancellationTokenSource();
            var abort = new CancellationTokenSource();
            lock (_idleLock)
            {
                _idleCts = stop;
                _idleAbort = abort;
                _idleTask = Task.Run(() => IdleLoopAsync(folder, initialCount, stop.Token, abort.Token), CancellationToken.None);
            }
            started = true;

            _logger?.Info($"Started IDLE on '{folder}'");
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Fail(ex, "Error starting IDLE");
        }
        finally
        {
            if (!started)
                lock (_idleLock) { _idleTask = null; }
        }
    }

    private async Task IdleLoopAsync(string folder, int lastCount, CancellationToken stopToken, CancellationToken abortToken)
    {
        var refresh = TimeSpan.FromMinutes(_config.IdleRefreshMinutes);
        ImapClient? boundClient = null;
        IMailFolder? imapFolder = null;

        while (!stopToken.IsCancellationRequested)
        {
            int? newCount = null;
            try
            {
                await _gate.WaitAsync(stopToken).ConfigureAwait(false);
                try
                {
                    var client = await EnsureConnectedAsync(stopToken).ConfigureAwait(false);

                    // A reconnect replaces the client: the folder object of the old one is dead, so resolve it again.
                    if (imapFolder is null || !ReferenceEquals(boundClient, client))
                    {
                        imapFolder = await GetImapFolderAsync(client, folder, FolderAccess.None, stopToken).ConfigureAwait(false)
                            ?? throw new FolderNotFoundException(folder);
                        boundClient = client;
                    }

                    // A call on another folder closes this one (IMAP selects one folder at a time).
                    if (!imapFolder.IsOpen)
                        await imapFolder.OpenAsync(FolderAccess.ReadOnly, stopToken).ConfigureAwait(false);
                    newCount = CheckNewMail(imapFolder, ref lastCount);

                    if (newCount is null)
                    {
                        var done = new CancellationTokenSource();
                        Interlocked.Exchange(ref _idleDone, done);
                        try
                        {
                            // Only idle when no call is waiting for the connection (re-checked after publishing
                            // _idleDone, so a call that arrives now either sees it or is seen here).
                            if (Volatile.Read(ref _pendingCalls) == 0)
                            {
                                done.CancelAfter(refresh);
                                using var stopReg = stopToken.Register(static d => ((CancellationTokenSource)d!).Cancel(), done);
                                await client.IdleAsync(done.Token, abortToken).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _idleDone, null);
                            done.Dispose();
                        }

                        newCount = CheckNewMail(imapFolder, ref lastCount);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.Error($"IDLE error, retrying: {ex.Message}");
                imapFolder = null;
                try { await Task.Delay(5_000, stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // Raised outside the lock, so a handler may call this service.
            if (newCount is { } count)
            {
                _logger?.Info($"New mail in '{folder}': {count} messages");
                try { NewMailReceived?.Invoke(this, new NewMailEventArgs { FolderName = folder, MessageCount = count }); }
                catch (Exception ex) { _logger?.Error("NewMailReceived handler threw", ex); }
            }
        }

        _logger?.Info("IDLE monitoring stopped");
    }

    private static int? CheckNewMail(IMailFolder folder, ref int lastCount)
    {
        var count = folder.Count;
        var grew = count > lastCount;
        lastCount = count;
        return grew ? count : null;
    }

    /// <summary>
    /// Stops the IDLE background loop and waits for it to complete (up to 5 s; an IDLE that does not end
    /// gracefully in that time is aborted).
    /// </summary>
    public void StopIdle()
    {
        CancellationTokenSource? stop, abort;
        Task? task;
        lock (_idleLock)
        {
            stop = _idleCts;
            abort = _idleAbort;
            task = _idleTask;
            _idleCts = null;
            _idleAbort = null;
            _idleTask = null;
        }
        if (stop is null) return;

        stop.Cancel();
        try
        {
            if (task is not null && !task.Wait(TimeSpan.FromSeconds(5)))
            {
                abort?.Cancel();
                task.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (AggregateException) { }

        stop.Dispose();
        abort?.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes the connection for one call. A running IDLE is ended gracefully first, and resumes when the call
    /// releases the connection.
    /// </summary>
    private async Task<Releaser> LockAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _pendingCalls);
        try
        {
            var done = Volatile.Read(ref _idleDone);
            if (done is not null)
            {
                try { done.Cancel(); }
                catch (ObjectDisposedException) { /* the IDLE already ended */ }
            }
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCalls);
        }
        return new Releaser(_gate);
    }

    private readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    /// <summary>Opens a new connection, replacing (and disposing) the current one. Caller holds the gate.</summary>
    private async Task<ImapClient> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var security = MapSecurity(_config.SecurityMode);

        var previous = _client;
        _client = null;
        if (previous is not null)
        {
            try
            {
                if (previous.IsConnected)
                    await previous.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Debug($"Closing the previous IMAP connection failed: {ex.Message}");
            }
            finally
            {
                previous.Dispose();
            }
        }

        var client = new ImapClient { Timeout = _config.TimeoutSeconds * 1000 };
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, security, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _logger?.Info($"Connected to IMAP {_config.Host}:{_config.Port}");
        return client;
    }

    /// <summary>Returns a connected, authenticated client, reconnecting when needed. Caller holds the gate.</summary>
    private async Task<ImapClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true, IsAuthenticated: true } client) return client;
        if (_client is not null) _logger?.Info("IMAP reconnecting...");
        return await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMailFolder?> GetImapFolderAsync(ImapClient client, string name, FolderAccess access, CancellationToken ct)
    {
        IMailFolder? folder;
        try
        {
            folder = string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(name, ct).ConfigureAwait(false);
        }
        catch (FolderNotFoundException) { return null; }

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

    /// <summary>
    /// Maps an exception from an IMAP operation to an <see cref="Error"/> of the matching kind and logs it.
    /// </summary>
    private Error Fail(Exception ex, string what)
    {
        switch (ex)
        {
            case OperationCanceledException:
                _logger?.Warning($"{what}: cancelled");
                return MailErrors.Cancelled();
            case ImapConfigException:
                _logger?.Error($"{what}: {ex.Message}");
                return MailErrors.Validation(MailErrorCodes.ImapConfigInvalid, ex.Message);
            case AuthenticationException:
                _logger?.Error($"{what}: authentication failed", ex);
                return MailErrors.Unauthorized(MailErrorCodes.ImapAuthenticationFailed, "Authentication failed");
            case TimeoutException:
                _logger?.Error($"{what}: timed out", ex);
                return MailErrors.Timeout(MailErrorCodes.ImapTimeout, "IMAP operation timed out");
            case SocketException or IOException or ServiceNotConnectedException or SslHandshakeException:
                _logger?.Error($"{what}: IMAP server {_config.Host}:{_config.Port} unreachable", ex);
                return MailErrors.Unavailable(MailErrorCodes.ImapConnectionFailed,
                    $"Could not connect to IMAP server {_config.Host}:{_config.Port}", ex);
            case FolderNotFoundException:
                _logger?.Warning($"{what}: {ex.Message}");
                return MailErrors.NotFound(MailErrorCodes.FolderNotFound, ex.Message);
            case MessageNotFoundException:
                _logger?.Warning($"{what}: {ex.Message}");
                return MailErrors.NotFound(MailErrorCodes.MessageNotFound, ex.Message);
            case ImapCommandException command:
                _logger?.Warning($"{what}: server answered {command.Response}: {command.ResponseText}");
                return MailErrors.Validation(MailErrorCodes.ImapRejected, ex.Message, $"IMAP {command.Response}");
            default:
                _logger?.Error(what, ex);
                return MailErrors.Internal(MailErrorCodes.ImapError, ex);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warning($"Could not fetch body for UID {summary.UniqueId}: {ex.Message}");
            }
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

    /// <summary>
    /// Maps the configured security mode. An unknown value fails closed: it is a configuration error, never a
    /// silent fallback to opportunistic TLS.
    /// </summary>
    internal static SecureSocketOptions MapSecurity(MailSecurityMode mode) => mode switch
    {
        MailSecurityMode.None     => SecureSocketOptions.None,
        MailSecurityMode.StartTls => SecureSocketOptions.StartTls,
        MailSecurityMode.SslTls   => SecureSocketOptions.SslOnConnect,
        _                         => throw new ImapConfigException($"Imap.SecurityMode '{mode}' is not a known security mode")
    };

    private sealed class ImapConfigException(string message) : Exception(message);

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopIdle();
        _client?.Dispose();
        _client = null;
    }
}
