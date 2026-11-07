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

    /// <summary>Uploads a caller-owned stream while reporting bytes consumed by the provider.</summary>
    public static async Task<Result<StorageItem>> UploadWithProgressAsync(
        this IStorageService storage,
        string path,
        Stream source,
        IProgress<StorageTransferProgress> progress,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        // The backend pipeline meters the stream and keeps it seekable, so upload retries still work.
        return await storage.UploadAsync(path, source, (options ?? new StorageUploadOptions()) with { Progress = progress }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns an owned download stream that reports bytes as the caller reads it.</summary>
    public static async Task<Result<Stream>> DownloadWithProgressAsync(
        this IStorageService storage,
        string path,
        IProgress<StorageTransferProgress> progress,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);

        long? total = options.Length;
        if (!total.HasValue)
        {
            var info = await storage.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsSuccess && info.Value!.Size.HasValue)
                total = Math.Max(0, info.Value.Size.Value - options.Offset);
        }
        var download = await storage.DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        return download.IsSuccess
            ? Result<Stream>.Success(new ProgressReadStream(download.Value!, progress, total, leaveOpen: false))
            : Result<Stream>.Failure(download.Error!);
    }

    /// <summary>Streams a local file into storage while keeping ownership of the local stream internal.</summary>
    public static async Task<Result<StorageItem>> UploadFileAsync(
        this IStorageService storage,
        string destinationPath,
        string sourceFilePath,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A source file path is required."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullPath = Path.GetFullPath(sourceFilePath);
            var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var sourceLifetime = source.ConfigureAwait(false);
            // Newer-only conflict policies compare against the local file's modification time.
            if (options?.ConflictPolicy is not null && options.SourceLastModified is null)
                options = options with { SourceLastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath)) };
            if (options?.ConflictPolicy == StorageConflictPolicy.Resume && options.SourceIdentity is null)
                options = options with { SourceIdentity = fullPath };
            return await storage.UploadAsync(destinationPath, source, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Open local upload file"));
        }
    }

    /// <summary>
    /// Streams an item to a sibling staging file and atomically commits it, preserving an existing
    /// destination when the download fails or is cancelled.
    /// </summary>
    public static async Task<Result<FileInfo>> DownloadToFileAsync(
        this IStorageService storage,
        string sourcePath,
        string destinationFilePath,
        StorageDownloadOptions? options = null,
        bool overwrite = true,
        bool createParents = true,
        StorageConflictPolicy? conflictPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(destinationFilePath))
            return Result<FileInfo>.Failure(StorageErrors.InvalidPath("A destination file path is required."));
        cancellationToken.ThrowIfCancellationRequested();

        string? stagingPath = null;
        try
        {
            var destination = Path.GetFullPath(destinationFilePath);
            if (conflictPolicy == StorageConflictPolicy.Resume && options is null or { Offset: 0, Length: null })
                return await ResumableDownloadAsync(storage, sourcePath, destination, options, createParents, cancellationToken).ConfigureAwait(false);
            if (conflictPolicy is not null)
            {
                var decision = await DecideLocalConflictAsync(storage, sourcePath, destination, conflictPolicy.Value, options, cancellationToken).ConfigureAwait(false);
                if (decision.IsFailure) return Result<FileInfo>.Failure(decision.Error!);
                if (decision.Value.Skip) return Result<FileInfo>.Success(new FileInfo(destination));
                destination = decision.Value.Path;
                overwrite = decision.Value.Overwrite;
            }
            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(Path.GetFileName(destination)))
                return Result<FileInfo>.Failure(StorageErrors.InvalidPath("The destination must identify a file."));
            if (createParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<FileInfo>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

            var download = await storage.DownloadAsync(sourcePath, options, cancellationToken).ConfigureAwait(false);
            if (download.IsFailure) return Result<FileInfo>.Failure(download.Error!);

            stagingPath = Path.Combine(parent, $".clstorage-download-{Guid.NewGuid():N}.tmp");
            var source = download.Value!;
            await using (source.ConfigureAwait(false))
            {
                var destinationStream = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (destinationStream.ConfigureAwait(false))
                {
                    await source.CopyToAsync(destinationStream, 65_536, cancellationToken).ConfigureAwait(false);
                    await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destinationStream.Flush(flushToDisk: true);
                }
            }

            File.Move(stagingPath, destination, overwrite);
            stagingPath = null;
            return Result<FileInfo>.Success(new FileInfo(destination));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) when (!overwrite && destinationFilePath is not null && File.Exists(Path.GetFullPath(destinationFilePath)))
        {
            return Result<FileInfo>.Failure(StorageErrors.Conflict("The local download destination already exists."));
        }
        catch (Exception error)
        {
            return Result<FileInfo>.Failure(StorageErrors.FromException(error, "Write local download file"));
        }
        finally
        {
            if (stagingPath is not null)
            {
                try { File.Delete(stagingPath); }
                catch { }
            }
        }
    }

    /// <summary>The remote version a resumable download's partial file holds the start of.</summary>
    private sealed record PartialDownloadIdentity(string Source, long? Size, string? ETag, string? VersionId, DateTimeOffset? LastModified)
    {
        /// <summary>
        /// Whether a partial recorded for this version is a prefix of <paramref name="current"/>: the same source,
        /// length, and every identity the server reports, of which there must be at least one.
        /// </summary>
        public bool Proves(PartialDownloadIdentity current) =>
            Size is not null && (current.ETag ?? current.VersionId ?? (object?)current.LastModified) is not null &&
            this == current;
    }
}
