using CL.Mail.Models;
using CodeLogic.Core.Results;
using MailKit;
using ModelMailFolder = CL.Mail.Models.MailFolder;

namespace CL.Mail.Services;

// Folder operations: list, create, delete, rename.
public sealed partial class ImapService
{
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
}
