using System.Runtime.CompilerServices;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

public static partial class StorageServiceExtensions
{

    /// <summary>Sends a raw FTP or SSH command when the connection allows it.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="command">Command text.</param>
    /// <param name="cancellationToken">Token used to cancel the command.</param>
    /// <returns>The reply, or <c>storage.unsupported</c>.</returns>
    public static Task<Result<StorageCommandResult>> ExecuteCommandAsync(this IStorageService storage, string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageCommandService commands
            ? commands.ExecuteCommandAsync(command, cancellationToken)
            : Task.FromResult(Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("This storage connection does not support raw commands.")));
    }

    /// <summary>Reads free and used space when the connection reports it.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Path relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>Space figures, or <c>storage.unsupported</c>.</returns>
    public static Task<Result<StorageSpaceInfo>> GetSpaceAsync(this IStorageService storage, string path = "", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageSpaceService space
            ? space.GetSpaceAsync(path, cancellationToken)
            : Task.FromResult(Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("This storage connection does not report free space.")));
    }

    /// <summary>Sets Unix permission bits when supported by the connection.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="unixMode">Permission bits, for example octal 0755.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or <c>storage.unsupported</c> when the connection has no permission support.</returns>
    public static Task<Result> SetPermissionsAsync(this IStorageService storage, string path, int unixMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAttributeService attributes
            ? attributes.SetPermissionsAsync(path, unixMode, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support permissions.")));
    }

    /// <summary>Sets Unix permission bits from octal text such as <c>755</c> or <c>0644</c>.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="octalMode">Octal permission text.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or <c>storage.invalid_content</c> when the text is not a valid mode.</returns>
    public static Task<Result> SetPermissionsAsync(this IStorageService storage, string path, string octalMode, CancellationToken cancellationToken = default) =>
        UnixPermissions.TryParseOctal(octalMode, out var mode)
            ? storage.SetPermissionsAsync(path, mode, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.InvalidContent($"'{octalMode}' is not an octal permission mode.")));

    /// <summary>
    /// Applies permissions to every item under a directory, like FileZilla's recursive chmod: files get
    /// <paramref name="fileMode"/> and directories <paramref name="directoryMode"/>, including the root directory.
    /// </summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Directory path relative to the mounted root.</param>
    /// <param name="fileMode">Mode for files, or <see langword="null"/> to leave files unchanged.</param>
    /// <param name="directoryMode">Mode for directories, or <see langword="null"/> to leave directories unchanged.</param>
    /// <param name="cancellationToken">Token used to cancel the provider requests.</param>
    /// <returns>The number of items changed, or the first failure.</returns>
    public static async Task<Result<int>> SetPermissionsRecursiveAsync(
        this IStorageService storage,
        string path,
        int? fileMode,
        int? directoryMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (fileMode is null && directoryMode is null)
            return Result<int>.Failure(StorageErrors.InvalidContent("A file or directory mode is required."));
        var changed = 0;
        await foreach (var item in storage.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure) return Result<int>.Failure(item.Error!);
            var mode = item.Value!.ItemType == StorageItemType.Directory ? directoryMode : item.Value.ItemType == StorageItemType.File ? fileMode : null;
            if (mode is null) continue;
            var result = await storage.SetPermissionsAsync(item.Value.Path, mode.Value, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) return Result<int>.Failure(result.Error!);
            changed++;
        }
        if (directoryMode is { } rootMode && StoragePath.Normalize(path).Value is { Length: > 0 } root)
        {
            var result = await storage.SetPermissionsAsync(root, rootMode, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) return Result<int>.Failure(result.Error!);
            changed++;
        }
        return Result<int>.Success(changed);
    }

    /// <summary>Changes the numeric owner and/or group when supported by the connection.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="ownerId">New owner ID, or <see langword="null"/> to keep it.</param>
    /// <param name="groupId">New group ID, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or <c>storage.unsupported</c>.</returns>
    public static Task<Result> SetOwnerAsync(this IStorageService storage, string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAttributeService attributes
            ? attributes.SetOwnerAsync(path, ownerId, groupId, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support ownership changes.")));
    }

    /// <summary>Sets modification and access times when supported by the connection.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="lastModified">New modification time, or <see langword="null"/> to keep it.</param>
    /// <param name="lastAccessed">New access time, or <see langword="null"/> to keep it.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or <c>storage.unsupported</c>.</returns>
    public static Task<Result> SetTimestampsAsync(this IStorageService storage, string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAttributeService attributes
            ? attributes.SetTimestampsAsync(path, lastModified, lastAccessed, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support setting timestamps.")));
    }

    /// <summary>Creates a symbolic link when supported by the connection.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="linkPath">Path of the link to create.</param>
    /// <param name="targetPath">Path of the item the link points to.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or <c>storage.unsupported</c>.</returns>
    public static Task<Result> CreateLinkAsync(this IStorageService storage, string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAttributeService attributes
            ? attributes.CreateLinkAsync(linkPath, targetPath, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support creating links.")));
    }

    /// <summary>Reads where a symbolic link points when supported by the connection.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Link path relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The link target, or <c>storage.unsupported</c>.</returns>
    public static Task<Result<StorageLinkInfo>> ReadLinkAsync(this IStorageService storage, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAttributeService attributes
            ? attributes.ReadLinkAsync(path, cancellationToken)
            : Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported("This storage connection does not support reading links.")));
    }

    /// <summary>Creates a temporary signed read/write URL when supported by the connection.</summary>
    public static Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        this IStorageService storage,
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageSignedUrlService signedUrls
            ? signedUrls.CreateSignedUrlAsync(path, options, cancellationToken)
            : Task.FromResult(Result<StorageSignedUrl>.Failure(
                StorageErrors.Unsupported("This storage connection does not support signed URLs.")));
    }

    /// <summary>Lists one page of versions for an exact object path.</summary>
    public static Task<Result<StorageVersionPage>> ListVersionsAsync(
        this IStorageService storage,
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageVersionService versions
            ? versions.ListVersionsAsync(path, options, cancellationToken)
            : Task.FromResult(Result<StorageVersionPage>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions.")));
    }

    /// <summary>Requests deletion of one exact object version, subject to provider retention policy.</summary>
    public static Task<Result> DeleteVersionAsync(
        this IStorageService storage,
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageVersionService versions
            ? versions.DeleteVersionAsync(path, versionId, cancellationToken)
            : Task.FromResult(Result.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions.")));
    }

    /// <summary>Enumerates every version page while rejecting repeated provider tokens.</summary>
    public static async IAsyncEnumerable<Result<StorageVersionPage>> EnumerateVersionPagesAsync(
        this IStorageService storage,
        string path,
        StorageVersionListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            yield return Result<StorageVersionPage>.Failure(validation.Error!);
            yield break;
        }

        var continuationToken = options.ContinuationToken;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(continuationToken))
            seenTokens.Add(continuationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await storage.ListVersionsAsync(
                path,
                options with { ContinuationToken = continuationToken },
                cancellationToken).ConfigureAwait(false);
            yield return page;
            if (page.IsFailure)
                yield break;
            var next = page.Value!.ContinuationToken;
            if (string.IsNullOrEmpty(next))
                yield break;
            if (!seenTokens.Add(next))
            {
                yield return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                    "The storage provider repeated a continuation token while enumerating versions."));
                yield break;
            }
            continuationToken = next;
        }
    }
}
