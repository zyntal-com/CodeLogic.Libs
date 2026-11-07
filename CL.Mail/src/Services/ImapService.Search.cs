using CL.Mail.Models;
using CodeLogic.Core.Results;
using MailKit;
using MailKit.Search;
using ModelMsgFlags = CL.Mail.Models.MessageFlags;

namespace CL.Mail.Services;

// Search: criteria to MailKit query, newest-first results.
public sealed partial class ImapService
{
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
}
