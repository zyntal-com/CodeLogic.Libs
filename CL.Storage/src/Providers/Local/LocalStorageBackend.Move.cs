using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

public sealed partial class LocalStorageBackend
{

    /// <inheritdoc />
    public Task<Result> MoveAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var endpoints = ResolveTransfer(sourcePath, destinationPath, options);
        if (endpoints.IsFailure)
            return Task.FromResult(Result.Failure(endpoints.Error!));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAttributes = File.GetAttributes(endpoints.Value!.Source.FullPath);
            if ((sourceAttributes & FileAttributes.Directory) != 0)
            {
                var relationship = StorageTransferPath.ValidateDirectoryDestination(
                    endpoints.Value.Source.StoragePath,
                    endpoints.Value.Destination.StoragePath,
                    _paths.Comparison);
                if (relationship.IsFailure)
                    return Task.FromResult(Result.Failure(relationship.Error!));
            }
            if (!options.Overwrite && DestinationExists(endpoints.Value.Destination.FullPath))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The move destination already exists.")));
            EnsureTransferParent(endpoints.Value.Destination.FullPath, options.CreateParents);
            if ((sourceAttributes & FileAttributes.Directory) != 0)
            {
                if (DestinationExists(endpoints.Value.Destination.FullPath))
                    return Task.FromResult(Result.Failure(StorageErrors.Conflict("An existing directory cannot be overwritten by native move.")));
                Directory.Move(endpoints.Value.Source.FullPath, endpoints.Value.Destination.FullPath);
            }
            else
            {
                File.Move(endpoints.Value.Source.FullPath, endpoints.Value.Destination.FullPath, options.Overwrite);
            }
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && DestinationExists(endpoints.Value!.Destination.FullPath))
        {
            return Task.FromResult(Result.Failure(StorageErrors.Conflict("The move destination already exists.")));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Move item")));
        }
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(Root);
            return Task.FromResult((attributes & FileAttributes.Directory) != 0
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable("The configured local root is not a directory.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Check local storage health")));
        }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default)
        where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(
            StorageErrors.Unsupported("The local provider does not use a native client session.")));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IEnumerable<StorageItem> EnumerateItems(ResolvedLocalPath root, bool recursive, CancellationToken cancellationToken)
    {
        var pending = new Stack<ResolvedLocalPath>();
        var visited = new HashSet<string>(_paths.Comparer)
        {
            GetDirectoryIdentity(root.FullPath)
        };
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var fullPath in Directory.EnumerateFileSystemEntries(directory.FullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeName = Path.GetFileName(fullPath);
                var storagePath = directory.StoragePath.Length == 0
                    ? relativeName
                    : directory.StoragePath + "/" + relativeName;
                var attributes = File.GetAttributes(fullPath);
                var item = CreateItem(storagePath, fullPath, attributes)!;
                yield return item;

                // A directory link is reported as a link and not descended into: whether to follow it is the
                // caller's choice (LinkHandling), and its contents under the link's path would otherwise be taken
                // for a real folder's.
                if (!recursive || item.ItemType != StorageItemType.Directory)
                    continue;
                if (!visited.Add(GetDirectoryIdentity(fullPath)))
                    continue;
                pending.Push(new ResolvedLocalPath(storagePath, fullPath));
            }
        }
    }

    private static string GetDirectoryIdentity(string fullPath)
    {
        var info = new DirectoryInfo(fullPath);
        var target = (info.Attributes & FileAttributes.ReparsePoint) != 0
            ? info.ResolveLinkTarget(returnFinalTarget: true)
            : null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? fullPath));
    }

    private StorageItem? CreateItem(string storagePath, string fullPath, FileAttributes? knownAttributes = null)
    {
        FileAttributes attributes;
        try { attributes = knownAttributes ?? File.GetAttributes(fullPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }

        // Only symbolic links and junctions are links: other reparse points (cloud-file placeholders such as
        // OneDrive's, deduplicated files, app execution aliases) are ordinary files and folders.
        var type = LocalPathResolver.IsLink(fullPath, attributes)
            ? StorageItemType.Link
            : (attributes & FileAttributes.Directory) != 0
                ? StorageItemType.Directory
                : StorageItemType.File;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        var name = storagePath.Length == 0 ? info.Name : storagePath.Split('/')[^1];
        return new StorageItem
        {
            Path = storagePath,
            Name = name,
            ItemType = type,
            Size = type == StorageItemType.File ? ((FileInfo)info).Length : null,
            LastModified = new DateTimeOffset(info.LastWriteTimeUtc),
            Created = new DateTimeOffset(info.CreationTimeUtc),
            LastAccessed = new DateTimeOffset(info.LastAccessTimeUtc),
            ContentType = type == StorageItemType.File ? GetContentType(info.Extension) : null,
            ETag = type == StorageItemType.File ? SyntheticETag((FileInfo)info) : null,
            UnixMode = OperatingSystem.IsWindows() ? null : (int)info.UnixFileMode,
            // Only a target inside the root is reported; a link that leads out shows no target.
            LinkTarget = type == StorageItemType.Link ? _paths.VisibleLinkTarget(fullPath, info.LinkTarget)?.RawTarget : null,
            IsHidden = (attributes & FileAttributes.Hidden) != 0 || name.StartsWith('.')
        };
    }

    /// <summary>
    /// A weak validator (<c>W/"…"</c>) built from the last-write time, the creation time (on Unix, the birth time or
    /// the inode change time), and the length. It is not a content version: file systems with coarse timestamps
    /// (FAT 2 s, exFAT, HFS+ 1 s, some SMB and NFS mounts) and tools that restore times can give two versions the
    /// same value, so it must not be the only proof that content is unchanged, nor a resume identity.
    /// </summary>
    internal static string SyntheticETag(FileInfo info) =>
        $"W/\"{info.LastWriteTimeUtc.Ticks:x}-{info.CreationTimeUtc.Ticks:x}-{info.Length:x}\"";

    /// <inheritdoc />
    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return Task.FromResult(Result.Failure(StorageErrors.Unsupported("Unix permissions are not available on Windows.")));
        return Attribute(path, "Set permissions", full =>
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(full, (UnixFileMode)unixMode);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported(".NET has no API for changing local file ownership.")));

    /// <inheritdoc />
    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        Attribute(path, "Set timestamps", full =>
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (!info.Exists) throw new FileNotFoundException(null, full);
            if (lastModified is { } modified) info.LastWriteTimeUtc = modified.UtcDateTime;
            if (lastAccessed is { } accessed) info.LastAccessTimeUtc = accessed.UtcDateTime;
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The link stores a relative target so it keeps working if the root moves. On Windows, creating
    /// symbolic links needs Developer Mode or the "Create symbolic links" privilege; without it the call
    /// fails with <c>storage.permission_denied</c>.
    /// </remarks>
    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var link = _paths.ResolveLink(linkPath);
        if (link.IsFailure) return Task.FromResult(Result.Failure(link.Error!));
        if (link.Value!.StoragePath.Length == 0)
            return Task.FromResult(Result.Failure(StorageErrors.InvalidPath("A link cannot replace the root.")));
        var target = _paths.Resolve(targetPath);
        if (target.IsFailure) return Task.FromResult(Result.Failure(target.Error!));
        try
        {
            if (File.Exists(link.Value.FullPath) || Directory.Exists(link.Value.FullPath) || new FileInfo(link.Value.FullPath).LinkTarget is not null)
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The link path already exists.")));
            var linkDirectory = Path.GetDirectoryName(link.Value.FullPath)!;
            Directory.CreateDirectory(linkDirectory);
            var relative = Path.GetRelativePath(linkDirectory, target.Value!.FullPath);
            if (Directory.Exists(target.Value.FullPath))
                Directory.CreateSymbolicLink(link.Value.FullPath, relative);
            else
                File.CreateSymbolicLink(link.Value.FullPath, relative);
            return Task.FromResult(Result.Success());
        }
        catch (IOException error) when (error.HResult == unchecked((int)0x80070522))
        {
            return Task.FromResult(Result.Failure(StorageErrors.PermissionDenied(
                "Create link: Windows requires Developer Mode or the symbolic-link privilege.")));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Create link")));
        }
    }
}
