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

    /// <summary>Returns the checksum the server holds for a file, without downloading it.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">File path relative to the mounted root.</param>
    /// <param name="algorithm">Requested digest algorithm.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The server checksum, or <c>storage.unsupported</c> when the server has none for this algorithm.</returns>
    public static Task<Result<StorageChecksum>> GetServerChecksumAsync(
        this IStorageService storage,
        string path,
        StorageChecksumAlgorithm algorithm = StorageChecksumAlgorithm.Sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageChecksumService checksums
            ? checksums.GetServerChecksumAsync(path, algorithm, cancellationToken)
            : Task.FromResult(Result<StorageChecksum>.Failure(StorageErrors.Unsupported("This storage connection does not report server checksums.")));
    }

    /// <summary>
    /// Returns an item's digest. By default the server's stored checksum is used when it has one for
    /// the algorithm, and otherwise the content is streamed through a client-side digest without
    /// buffering. MD5 is available for interoperability; SHA-256 or stronger should be used for
    /// security-sensitive verification. Byte ranges are always computed.
    /// </summary>
    public static async Task<Result<StorageChecksum>> ComputeChecksumAsync(
        this IStorageService storage,
        string path,
        StorageChecksumAlgorithm algorithm = StorageChecksumAlgorithm.Sha256,
        StorageDownloadOptions? options = null,
        IProgress<StorageTransferProgress>? progress = null,
        StorageChecksumMode mode = StorageChecksumMode.PreferServer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(algorithm))
            return Result<StorageChecksum>.Failure(StorageErrors.InvalidPath(
                "The checksum algorithm is invalid."));
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageChecksum>.Failure(validation.Error!);

        var wholeFile = options.Offset == 0 && options.Length is null && options.VersionId is null;
        if (mode != StorageChecksumMode.ComputeOnly && wholeFile && storage is IStorageChecksumService checksums)
        {
            var server = await checksums.GetServerChecksumAsync(path, algorithm, cancellationToken).ConfigureAwait(false);
            if (server.IsSuccess || server.Error?.Code != StorageErrors.UnsupportedCode || mode == StorageChecksumMode.ServerOnly)
                return server;
        }
        else if (mode == StorageChecksumMode.ServerOnly)
        {
            return Result<StorageChecksum>.Failure(StorageErrors.Unsupported(
                "A server checksum is only available for whole files on connections that report them."));
        }

        var download = await storage.DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<StorageChecksum>.Failure(download.Error!);
        var buffer = ArrayPool<byte>.Shared.Rent(65_536);
        try
        {
            var source = download.Value!;
            await using var sourceLifetime = source.ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(ToHashAlgorithmName(algorithm));
            long processed = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, 65_536), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                processed = checked(processed + read);
                progress?.Report(new StorageTransferProgress(processed, options.Length, false));
            }
            var digest = hash.GetHashAndReset();
            progress?.Report(new StorageTransferProgress(processed, options.Length, true));
            return Result<StorageChecksum>.Success(new StorageChecksum(
                algorithm,
                Convert.ToHexStringLower(digest),
                processed));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<StorageChecksum>.Failure(StorageErrors.FromException(
                error,
                "Compute storage checksum"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Computes a digest and compares it to hexadecimal expected bytes in constant time.</summary>
    public static async Task<Result<StorageChecksumVerification>> VerifyChecksumAsync(
        this IStorageService storage,
        string path,
        string expectedHexValue,
        StorageChecksumAlgorithm algorithm = StorageChecksumAlgorithm.Sha256,
        StorageDownloadOptions? options = null,
        IProgress<StorageTransferProgress>? progress = null,
        StorageChecksumMode mode = StorageChecksumMode.PreferServer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrWhiteSpace(expectedHexValue) || expectedHexValue.Any(char.IsWhiteSpace))
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "An uninterrupted hexadecimal checksum is required."));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHexValue);
        }
        catch (FormatException)
        {
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "The expected checksum is not valid hexadecimal."));
        }

        var expectedLength = ChecksumLength(algorithm);
        if (expectedLength is null)
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                "The checksum algorithm is invalid."));
        if (expected.Length != expectedLength.Value)
            return Result<StorageChecksumVerification>.Failure(StorageErrors.InvalidPath(
                $"The expected {algorithm} checksum must contain {expectedLength.Value} bytes."));

        var actual = await storage.ComputeChecksumAsync(
            path,
            algorithm,
            options,
            progress,
            mode,
            cancellationToken).ConfigureAwait(false);
        if (actual.IsFailure)
            return Result<StorageChecksumVerification>.Failure(actual.Error!);
        var actualBytes = Convert.FromHexString(actual.Value!.HexValue);
        return Result<StorageChecksumVerification>.Success(new StorageChecksumVerification(
            actual.Value,
            CryptographicOperations.FixedTimeEquals(actualBytes, expected)));
    }

    /// <summary>Reads user metadata when the connection advertises metadata reads.</summary>
    public static Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        this IStorageService storage,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageMetadataService metadata
            ? metadata.GetMetadataAsync(path, cancellationToken)
            : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                StorageErrors.Unsupported("This storage connection does not support metadata operations.")));
    }

    /// <summary>Updates user metadata when the connection advertises metadata writes.</summary>
    public static Task<Result<StorageItem>> SetMetadataAsync(
        this IStorageService storage,
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageMetadataService metadataService
            ? metadataService.SetMetadataAsync(path, metadata, options, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("This storage connection does not support metadata updates.")));
    }

    /// <summary>Reads provider-native object tags when supported by the connection.</summary>
    public static Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        this IStorageService storage,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageTagService tags
            ? tags.GetTagsAsync(path, cancellationToken)
            : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object tags.")));
    }

    /// <summary>Merges or replaces provider-native object tags when supported by the connection.</summary>
    public static Task<Result<StorageItem>> SetTagsAsync(
        this IStorageService storage,
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        return storage is IStorageTagService tagService
            ? tagService.SetTagsAsync(path, tags, options, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unsupported("This storage connection does not support object tag updates.")));
    }

    /// <summary>Appends content to a file when the connection supports it.</summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">File path relative to the mounted root.</param>
    /// <param name="source">Content to append.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The updated file, or <c>storage.unsupported</c>.</returns>
    public static Task<Result<StorageItem>> AppendAsync(this IStorageService storage, string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage is IStorageAppendService append
            ? append.AppendAsync(path, source, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("This storage connection cannot append.")));
    }

    /// <summary>
    /// Deletes the library's own staging and backup items (<c>.cl-storage-*</c>, <c>.clstorage-*</c>)
    /// under a directory that are older than <paramref name="olderThan"/>, such as leftovers of a crash.
    /// </summary>
    /// <param name="storage">Storage connection.</param>
    /// <param name="path">Directory to scan recursively.</param>
    /// <param name="olderThan">Minimum age; keep this well above your longest transfer so running transfers are not disturbed.</param>
    /// <param name="cancellationToken">Token used to cancel the provider requests.</param>
    /// <returns>The number of items deleted.</returns>
    public static async Task<Result<int>> CleanupStaleStagingAsync(this IStorageService storage, string path, TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var stale = new List<StorageItem>();
        await foreach (var item in storage.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true, IncludeInternal = true }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure) return Result<int>.Failure(item.Error!);
            if (Providers.StorageListFilter.IsInternal(item.Value!.Name) && item.Value.LastModified is { } modified && modified < cutoff)
                stale.Add(item.Value);
        }
        var deleted = 0;
        foreach (var item in stale)
        {
            var result = await storage.DeleteAsync(item.Path, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure) return Result<int>.Failure(result.Error!);
            deleted++;
        }
        return Result<int>.Success(deleted);
    }
}
