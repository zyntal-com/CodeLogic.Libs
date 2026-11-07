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
    /// <remarks>
    /// The target is reported only when it lies inside the root; otherwise the item is still confirmed to be a link,
    /// but <see cref="StorageLinkInfo.RawTarget"/> is empty and <see cref="StorageLinkInfo.StoragePath"/> null, so a
    /// link cannot disclose paths outside the root. Reading a link does not follow it, so this works with
    /// <see cref="LocalConnectionConfig.FollowLinks"/> off as well.
    /// </remarks>
    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var link = _paths.ResolveLink(path);
        if (link.IsFailure) return Task.FromResult(Result<StorageLinkInfo>.Failure(link.Error!));
        try
        {
            var info = new FileInfo(link.Value!.FullPath);
            if (!info.Exists && !Directory.Exists(info.FullName) && info.LinkTarget is null)
                return Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.NotFound($"Item '{link.Value.StoragePath}' was not found.")));
            if (info.LinkTarget is not { } raw)
                return Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Conflict($"Item '{link.Value.StoragePath}' is not a link.")));
            var visible = _paths.VisibleLinkTarget(info.FullName, raw);
            return Task.FromResult(Result<StorageLinkInfo>.Success(visible is { } target
                ? new StorageLinkInfo(target.RawTarget, target.StoragePath)
                : new StorageLinkInfo(string.Empty, null)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.FromException(error, "Read link")));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Like an upload with its default options: a missing parent directory is created, a directory at the path is
    /// a conflict.
    /// </remarks>
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A file path is required."));
        try
        {
            if (Directory.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The append destination is a directory."));
            EnsureTransferParent(resolved.Value.FullPath, createParents: true); // the upload default (StorageUploadOptions.CreateParents)
            var target = new FileStream(resolved.Value.FullPath, FileMode.Append, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, 65_536, cancellationToken).ConfigureAwait(false);
            }
            return CreateItem(resolved.Value.StoragePath, resolved.Value.FullPath) is { } item
                ? Result<StorageItem>.Success(item)
                : Result<StorageItem>.Failure(StorageErrors.NotFound("The appended file disappeared."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Append file"));
        }
    }

    /// <inheritdoc />
    public Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Task.FromResult(Result<StorageSpaceInfo>.Failure(resolved.Error!));
        try
        {
            var drive = DriveFor(resolved.Value!.FullPath);
            return Task.FromResult(Result<StorageSpaceInfo>.Success(new StorageSpaceInfo(
                drive.TotalSize, drive.AvailableFreeSpace, drive.TotalSize - drive.TotalFreeSpace)));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(Result<StorageSpaceInfo>.Failure(StorageErrors.FromException(error, "Get free space")));
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Sync.StorageChange> WatchNativeAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            throw new ArgumentException(resolved.Error!.Message, nameof(path));
        if (!Directory.Exists(resolved.Value!.FullPath))
            throw new DirectoryNotFoundException($"Directory '{resolved.Value.StoragePath}' was not found.");
        return Sync.StorageWatch.WatchFileSystemAsync(resolved.Value.FullPath, recursive, _paths.ToStoragePath, cancellationToken);
    }

    /// <summary>Finds the mounted volume holding a path: the longest matching mount point on Unix, the drive root on Windows.</summary>
    private static DriveInfo DriveFor(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return new DriveInfo(Path.GetPathRoot(fullPath)!);
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && fullPath.StartsWith(drive.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .First();
    }

    private Task<Result> Attribute(string path, string operation, Action<string> change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Task.FromResult(Result.Failure(resolved.Error!));
        try
        {
            change(resolved.Value!.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, operation)));
        }
    }

    private Result<TransferEndpoints> ResolveTransfer(string sourcePath, string destinationPath, StorageTransferOptions options)
    {
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<TransferEndpoints>.Failure(validation.Error!);
        // Local files have no versions and no enforced destination conditions.
        if (options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false })
            return Result<TransferEndpoints>.Failure(StorageErrors.Unsupported(
                "The local provider cannot pin a copy or move to a source version or keep a destination condition."));
        var source = _paths.Resolve(sourcePath);
        if (source.IsFailure)
            return Result<TransferEndpoints>.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath);
        if (destination.IsFailure)
            return Result<TransferEndpoints>.Failure(destination.Error!);
        if (source.Value!.StoragePath.Length == 0 || destination.Value!.StoragePath.Length == 0)
            return Result<TransferEndpoints>.Failure(StorageErrors.InvalidPath("Transfer paths cannot be the configured root."));
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value.StoragePath,
            destination.Value.StoragePath,
            _paths.Comparison);
        if (relationship.IsFailure)
            return Result<TransferEndpoints>.Failure(relationship.Error!);
        if (options.ExpectedSourceETag is { } expected)
        {
            // Compared immediately before the copy or move: a local file has no server to enforce it. The local ETag is
            // weak (see SyntheticETag), so this is a weak comparison: a mismatch proves the file changed, a match is the
            // best a local file can say and does not prove the content is the same.
            var current = CreateItem(source.Value.StoragePath, source.Value.FullPath);
            if (current is null)
                return Result<TransferEndpoints>.Failure(StorageErrors.NotFound($"Storage item '{source.Value.StoragePath}' was not found."));
            if (!StorageETags.WeakEquals(expected, current.ETag))
                return Result<TransferEndpoints>.Failure(StorageErrors.Conflict(
                    $"The source '{source.Value.StoragePath}' changed since it was read.",
                    $"expectedETag={expected};actualETag={current.ETag}"));
        }
        return Result<TransferEndpoints>.Success(new TransferEndpoints(source.Value, destination.Value));
    }

    private static void EnsureTransferParent(string destinationPath, bool createParents)
    {
        var parent = Path.GetDirectoryName(destinationPath)!;
        if (createParents)
            Directory.CreateDirectory(parent);
        else if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The destination parent directory does not exist.");
    }

    private static bool DestinationExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static string CreateStagingPath(string parent, string operation) =>
        Path.Combine(parent, $".cl-storage-{operation}-{Guid.NewGuid():N}.tmp");

    private static void TryDeleteStagingFile(string? path)
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch { }
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private sealed record TransferEndpoints(ResolvedLocalPath Source, ResolvedLocalPath Destination);
}
