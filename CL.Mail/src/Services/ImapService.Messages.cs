using CL.Mail.Models;
using CodeLogic.Core.Results;
using MailKit;
using MimeKit;
using MkFlags = MailKit.MessageFlags;
using ModelMsgFlags = CL.Mail.Models.MessageFlags;

namespace CL.Mail.Services;

// Message operations: fetch a page, get one by UID, move, copy, delete, flags; the summary/attachment conversion.
public sealed partial class ImapService
{
    /// <summary>
    /// Fetches a page of messages from a folder, newest first.
    /// </summary>
    /// <param name="folder">IMAP folder path (e.g., "INBOX").</param>
    /// <param name="offset">Zero-based page offset (0 = most recent messages).</param>
    /// <param name="count">Number of messages per page.</param>
    /// <param name="includeBody">When true, fetches the full body and lists the attachments (without content).</param>
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

    /// <summary>
    /// Fetches a single message by UID with full body and attachment content. An attachment whose decoded size
    /// exceeds <see cref="ImapConfig.MaxAttachmentBytes"/> comes back with <see cref="ReceivedAttachment.TooLarge"/>
    /// set, its <see cref="ReceivedAttachment.Size"/> and no <see cref="ReceivedAttachment.Content"/>.
    /// </summary>
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
                    var received = await ReadAttachmentAsync(part, _config.MaxAttachmentBytes, cancellationToken).ConfigureAwait(false);
                    if (received.TooLarge)
                        _logger?.Warning($"Attachment '{received.FileName}' on UID {uid} in '{folder}' is {received.Size} bytes, above Imap.MaxAttachmentBytes ({_config.MaxAttachmentBytes}); content omitted");
                    attachments.Add(received);
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

    // ── Conversion helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a MIME part into a <see cref="ReceivedAttachment"/> without buffering more than
    /// <paramref name="maxBytes"/>: the decoder counts every byte but keeps only the first <paramref name="maxBytes"/>,
    /// so an oversized part yields <see cref="ReceivedAttachment.TooLarge"/> with its full decoded
    /// <see cref="ReceivedAttachment.Size"/> and no content.
    /// </summary>
    internal static async Task<ReceivedAttachment> ReadAttachmentAsync(MimePart part, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        using var sink = new BoundedSink(maxBytes);
        if (part.Content is { } content)
            await content.DecodeToAsync(sink, cancellationToken).ConfigureAwait(false);

        return new ReceivedAttachment
        {
            FileName = part.FileName,
            ContentType = part.ContentType.MimeType,
            Size = sink.Total,
            Content = sink.Overflowed ? null : sink.ToArray(),
            TooLarge = sink.Overflowed
        };
    }

    /// <summary>A write-only stream that keeps the first <c>max</c> bytes and counts the rest.</summary>
    private sealed class BoundedSink(long max) : Stream
    {
        private readonly MemoryStream _kept = new();

        public long Total { get; private set; }
        public bool Overflowed => Total > max;

        public byte[] ToArray() => _kept.ToArray();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var room = max - Total;
            if (room > 0)
                _kept.Write(buffer[..(int)Math.Min(room, buffer.Length)]);
            Total += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            if (Total < max) _kept.WriteByte(value);
            Total++;
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _kept.Dispose();
            base.Dispose(disposing);
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
}
