using CL.Mail.Models;
using CodeLogic.Core.Results;
using MailKit;

namespace CL.Mail.Services;

public sealed partial class ImapService
{
    /// <summary>
    /// Fetches one attachment on demand using its <see cref="ReceivedAttachment.PartSpecifier"/>. Only that MIME section
    /// is downloaded, with independent wire and decoded byte limits; oversized content is returned as a stub.
    /// </summary>
    /// <param name="folder">IMAP folder.</param>
    /// <param name="uid">Message UID.</param>
    /// <param name="partSpecifier">Part identifier returned on an attachment descriptor.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public async Task<Result<ReceivedAttachment>> GetAttachmentAsync(string folder, uint uid, string partSpecifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partSpecifier))
            return MailErrors.Validation(MailErrorCodes.InvalidAttachment, "An attachment part identifier is required.");
        try
        {
            using var lease = await LockAsync(cancellationToken).ConfigureAwait(false);
            var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var mailbox = await GetImapFolderAsync(client, folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            if (mailbox is null) return MailErrors.NotFound(MailErrorCodes.FolderNotFound, "Folder not found.");
            var summaries = await mailbox.FetchAsync([new UniqueId(uid)], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, cancellationToken).ConfigureAwait(false);
            var part = summaries.FirstOrDefault()?.Attachments.FirstOrDefault(p => p.PartSpecifier == partSpecifier);
            if (part is null) return MailErrors.NotFound(MailErrorCodes.AttachmentNotFound, "Attachment not found.");
            return await ImapMessageReader.ReadPartAsync(part, Math.Min(_config.MaxAttachmentBytes, _config.MaxMessageBytes),
                (p, count, ct) => mailbox.GetStreamAsync(new UniqueId(uid), p.PartSpecifier, 0, count, ct, new ImapTransferLimit(count)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { return Fail(ex, "Could not fetch attachment"); }
    }
}
