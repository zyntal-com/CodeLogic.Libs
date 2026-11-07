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

    /// <summary>
    /// Downloads with <see cref="StorageConflictPolicy.Resume"/>: the content goes to a partial file next to the
    /// destination (<c>name.clstorage-partial</c>), with the remote version it holds recorded beside it
    /// (<c>name.clstorage-partial.json</c>) before the first byte. An interrupted download keeps both, and the next
    /// one appends only the missing range, but only while the remote is still that version; anything else, including
    /// a local file this download did not leave, is downloaded again from the start. The destination is replaced only
    /// when the content is complete.
    /// </summary>
    private static async Task<Result<FileInfo>> ResumableDownloadAsync(
        IStorageService storage,
        string sourcePath,
        string destination,
        StorageDownloadOptions? options,
        bool createParents,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(Path.GetFileName(destination)))
            return Result<FileInfo>.Failure(StorageErrors.InvalidPath("The destination must identify a file."));
        if (createParents)
            Directory.CreateDirectory(parent);
        else if (!Directory.Exists(parent))
            return Result<FileInfo>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

        var remote = await storage.GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (remote.IsFailure) return Result<FileInfo>.Failure(remote.Error!);
        var identity = IdentityOf(sourcePath, remote.Value!, options);
        var partial = destination + ".clstorage-partial";
        var record = partial + ".json";
        var offset = File.Exists(partial) && ReadPartialIdentity(record) is { } recorded && recorded.Proves(identity) &&
            new FileInfo(partial).Length is var present && present <= identity.Size
            ? present
            : 0;
        if (offset == 0)
        {
            File.Delete(partial);
            await File.WriteAllTextAsync(record, JsonSerializer.Serialize(identity), cancellationToken).ConfigureAwait(false);
        }

        if (identity.Size is not { } total || offset < total)
        {
            var download = await storage.DownloadAsync(sourcePath, (options ?? new StorageDownloadOptions()) with { Offset = offset }, cancellationToken).ConfigureAwait(false);
            if (download.IsFailure) return Result<FileInfo>.Failure(download.Error!);
            var source = download.Value!;
            await using (source.ConfigureAwait(false))
            {
                var target = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous);
                await using (target.ConfigureAwait(false))
                {
                    await source.CopyToAsync(target, 65_536, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    target.Flush(flushToDisk: true);
                }
            }
        }
        if (offset > 0)
        {
            // The tail must come from the same version as the prefix.
            var after = await storage.GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (after.IsFailure) return Result<FileInfo>.Failure(after.Error!);
            if (!identity.Proves(IdentityOf(sourcePath, after.Value!, options)))
            {
                File.Delete(partial);
                File.Delete(record);
                return Result<FileInfo>.Failure(StorageErrors.Conflict("The remote file changed while the download was resumed; download it again."));
            }
        }
        if (identity.Size is { } expected && new FileInfo(partial).Length != expected)
            return Result<FileInfo>.Failure(StorageErrors.Conflict(
                $"The downloaded content is {new FileInfo(partial).Length} bytes, not {expected}.",
                $"expectedLength={expected};actualLength={new FileInfo(partial).Length}"));
        File.Move(partial, destination, overwrite: true);
        File.Delete(record);
        return Result<FileInfo>.Success(new FileInfo(destination));
    }

    private static PartialDownloadIdentity IdentityOf(string sourcePath, StorageItem item, StorageDownloadOptions? options) =>
        new(sourcePath, item.Size, item.ETag, options?.VersionId ?? item.VersionId, item.LastModified);

    private static PartialDownloadIdentity? ReadPartialIdentity(string record)
    {
        try { return JsonSerializer.Deserialize<PartialDownloadIdentity>(File.ReadAllText(record)); }
        catch (Exception) { return null; }
    }

    /// <summary>Applies a conflict policy to a local download destination.</summary>
    private static async Task<Result<(bool Skip, string Path, bool Overwrite)>> DecideLocalConflictAsync(
        IStorageService storage,
        string sourcePath,
        string destination,
        StorageConflictPolicy policy,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        if (policy is StorageConflictPolicy.Fail or StorageConflictPolicy.Overwrite or StorageConflictPolicy.Resume || !File.Exists(destination))
            return Result<(bool, string, bool)>.Success((false, destination, policy != StorageConflictPolicy.Fail));
        if (policy == StorageConflictPolicy.Skip)
            return Result<(bool, string, bool)>.Success((true, destination, false));
        if (policy == StorageConflictPolicy.Rename)
        {
            for (var attempt = 1; attempt <= 1_000; attempt++)
            {
                var candidate = Registry.StorageConflictResolver.Candidate(destination.Replace('\\', '/'), attempt).Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return Result<(bool, string, bool)>.Success((false, candidate, false));
            }
            return Result<(bool, string, bool)>.Failure(StorageErrors.Conflict("No free local file name was found."));
        }

        var remote = await storage.GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (remote.IsFailure) return Result<(bool, string, bool)>.Failure(remote.Error!);
        var local = new FileInfo(destination);
        var remoteSize = options is { Offset: 0, Length: null } or null ? remote.Value!.Size : null;
        var newer = Registry.StorageConflictResolver.IsNewer(remote.Value!.LastModified, new DateTimeOffset(local.LastWriteTimeUtc));
        var sizeDiffers = Registry.StorageConflictResolver.SizeDiffers(remoteSize, local.Length);
        var replace = policy switch
        {
            StorageConflictPolicy.OverwriteIfNewer => newer,
            StorageConflictPolicy.OverwriteIfSizeDiffers => sizeDiffers,
            _ => newer || sizeDiffers
        };
        return Result<(bool, string, bool)>.Success((!replace, destination, replace));
    }

    /// <summary>Downloads bounded text content.</summary>
    public static async Task<Result<string>> ReadTextAsync(
        this IStorageService storage,
        string path,
        StorageDownloadOptions? options = null,
        Encoding? encoding = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = await storage.DownloadBytesAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (bytes.IsFailure) return Result<string>.Failure(bytes.Error!);
        try
        {
            var content = bytes.Value!;
            var preamble = encoding.GetPreamble();
            var offset = preamble.Length > 0 && content.AsSpan().StartsWith(preamble)
                ? preamble.Length
                : 0;
            var text = encoding.GetString(content, offset, content.Length - offset);
            return Result<string>.Success(text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text);
        }
        catch (DecoderFallbackException)
        {
            return Result<string>.Failure(StorageErrors.InvalidContent("The stored content is not valid text in the selected encoding."));
        }
    }

    /// <summary>Encodes and uploads text with an explicit serialization bound.</summary>
    public static Task<Result<StorageItem>> WriteTextAsync(
        this IStorageService storage,
        string path,
        string content,
        StorageUploadOptions? options = null,
        Encoding? encoding = null,
        int maxEncodedBytes = DefaultSerializedContentLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(content);
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (maxEncodedBytes <= 0)
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.InvalidPath(
                "maxEncodedBytes must be greater than zero.")));
        var byteCount = encoding.GetByteCount(content);
        if (byteCount > maxEncodedBytes)
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.TooLarge(
                $"Encoded text exceeds the {maxEncodedBytes} byte limit.")));
        var bytes = encoding.GetBytes(content);
        return storage.UploadBytesAsync(path, bytes, options, cancellationToken);
    }

    /// <summary>Downloads and deserializes bounded JSON content.</summary>
    public static async Task<Result<T>> ReadJsonAsync<T>(
        this IStorageService storage,
        string path,
        StorageDownloadOptions? downloadOptions = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var bytes = await storage.DownloadBytesAsync(path, downloadOptions, cancellationToken).ConfigureAwait(false);
        if (bytes.IsFailure) return Result<T>.Failure(bytes.Error!);
        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes.Value!, jsonOptions);
            return value is null
                ? Result<T>.Failure(StorageErrors.InvalidContent("The JSON content resolved to null."))
                : Result<T>.Success(value);
        }
        catch (JsonException)
        {
            return Result<T>.Failure(StorageErrors.InvalidContent("The stored content is not valid JSON for the requested type."));
        }
    }

    /// <summary>Serializes JSON into a bounded buffer and uploads it.</summary>
    public static async Task<Result<StorageItem>> WriteJsonAsync<T>(
        this IStorageService storage,
        string path,
        T value,
        StorageUploadOptions? uploadOptions = null,
        JsonSerializerOptions? jsonOptions = null,
        int maxSerializedBytes = DefaultSerializedContentLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (maxSerializedBytes <= 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath(
                "maxSerializedBytes must be greater than zero."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var content = new BoundedMemoryStream(maxSerializedBytes);
            await using var contentLifetime = content.ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(content, value, jsonOptions, cancellationToken).ConfigureAwait(false);
            content.Position = 0;
            return await storage.UploadAsync(path, content, uploadOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (StorageBufferLimitException)
        {
            return Result<StorageItem>.Failure(StorageErrors.TooLarge(
                $"Serialized JSON exceeds the {maxSerializedBytes} byte limit."));
        }
        catch (JsonException)
        {
            return Result<StorageItem>.Failure(StorageErrors.InvalidContent(
                "The value could not be serialized as JSON."));
        }
    }
}
