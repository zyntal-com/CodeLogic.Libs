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
    public async Task<Result<StorageItem>> UploadBytesAsync(
        string path,
        byte[] content,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var source = new MemoryStream(content, writable: false);
        await using var sourceLifetime = source.ConfigureAwait(false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result<Stream>.Failure(validation.Error!));
        if (options.VersionId is not null)
            return Task.FromResult(Result<Stream>.Failure(StorageErrors.Unsupported(
                "Local storage does not support version-specific downloads.")));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<Stream>.Failure(resolved.Error!));

        FileStream? stream = null;
        try
        {
            if (Directory.Exists(resolved.Value!.FullPath))
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.Conflict("A directory cannot be downloaded as a file.")));
            stream = new FileStream(
                resolved.Value.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (options.Offset > stream.Length)
            {
                stream.Dispose();
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the file length.")));
            }
            stream.Position = options.Offset;
            Stream result = options.Length.HasValue
                ? new RangeReadStream(stream, Math.Min(options.Length.Value, stream.Length - options.Offset))
                : stream;
            return Task.FromResult(Result<Stream>.Success(result));
        }
        catch (OperationCanceledException) { stream?.Dispose(); throw; }
        catch (Exception error)
        {
            stream?.Dispose();
            return Task.FromResult(Result<Stream>.Failure(StorageErrors.FromException(error, "Download file")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(
        string path,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<byte[]>.Failure(validation.Error!);
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;

        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure)
            return Result<byte[]>.Failure(info.Error!);
        long? expected = null;
        if (info.Value!.Size is { } knownSize)
        {
            var available = Math.Max(0, knownSize - options.Offset);
            expected = options.Length.HasValue ? Math.Min(options.Length.Value, available) : available;
            if (expected > limit || expected > int.MaxValue)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
        }

        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure)
            return Result<byte[]>.Failure(download.Error!);
        var source = download.Value!;
        await using var sourceLifetime = source.ConfigureAwait(false);
        using var destination = expected.HasValue ? new MemoryStream((int)expected.Value) : new MemoryStream();
        var effectiveLimit = Math.Min(limit, int.MaxValue);
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length > effectiveLimit - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        string path,
        StorageDeleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result.Failure(validation.Error!));
        if (options.Condition is { IsEmpty: false })
            return Task.FromResult(Result.Failure(StorageErrors.Unsupported(
                "The local provider does not support atomic ETag or version delete conditions.")));
        // A link itself can be deleted even when following links is disabled; its target is never touched.
        var resolved = _paths.ResolveLink(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error!));
        if (resolved.Value!.StoragePath.Length == 0)
            return Task.FromResult(Result.Failure(StorageErrors.InvalidPath("The configured root cannot be deleted.")));

        try
        {
            var attributes = File.GetAttributes(resolved.Value.FullPath);
            if ((attributes & FileAttributes.Directory) != 0 && LocalPathResolver.IsLink(resolved.Value.FullPath, attributes))
                Directory.Delete(resolved.Value.FullPath, recursive: false); // removes the link, not the target tree
            else if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(resolved.Value.FullPath, options.Recursive);
            else
                File.Delete(resolved.Value.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(options.IgnoreMissing
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound($"Storage item '{resolved.Value.StoragePath}' was not found.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Delete item")));
        }
    }

    /// <inheritdoc />
    public Task<Result> CopyAsync(
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
        string? stagingPath = null;
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
                return Task.FromResult(Result.Failure(StorageErrors.Unsupported("Native local directory copy is not supported.")));
            }
            if (Directory.Exists(endpoints.Value.Destination.FullPath))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The copy destination is a directory.")));
            EnsureTransferParent(endpoints.Value.Destination.FullPath, options.CreateParents);
            if (!options.Overwrite && DestinationExists(endpoints.Value.Destination.FullPath))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The copy destination already exists.")));
            var parent = Path.GetDirectoryName(endpoints.Value.Destination.FullPath)!;
            stagingPath = CreateStagingPath(parent, "copy");
            File.Copy(endpoints.Value.Source.FullPath, stagingPath, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, endpoints.Value.Destination.FullPath, options.Overwrite);
            stagingPath = null;
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && DestinationExists(endpoints.Value!.Destination.FullPath))
        {
            return Task.FromResult(Result.Failure(StorageErrors.Conflict("The copy destination already exists.")));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Copy item")));
        }
        finally
        {
            TryDeleteStagingFile(stagingPath);
        }
    }
}
