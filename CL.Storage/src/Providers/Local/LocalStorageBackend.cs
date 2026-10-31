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

/// <summary>Provides storage operations over a local path or mounted UNC root.</summary>
public sealed class LocalStorageBackend : IStorageBackend, IStorageAttributeService, IStorageAppendService, IStorageSpaceService, Sync.IStorageWatchService
{
    /// <inheritdoc />
    public const long DefaultMaxBufferedDownloadBytes = 67_108_864;
    private static readonly StorageCapabilities LocalCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        StorageFeature.AtomicMove |
        StorageFeature.AtomicReplace |
        StorageFeature.ConditionalCreate |
        StorageFeature.RangeReads |
        StorageFeature.ChangeNotifications |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Links |
        StorageFeature.SetTimestamps |
        StorageFeature.CreateLinks |
        StorageFeature.ReadLinks |
        // Unix permission bits exist only on Unix-like systems.
        (OperatingSystem.IsWindows() ? StorageFeature.None : StorageFeature.Permissions));

    private readonly LocalPathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly StorageCapabilities _capabilities;

    /// <summary>
    /// Initializes a root-confined local-filesystem backend. Path checks protect against traversal in
    /// caller-supplied paths; they are not a sandbox against concurrent changes to the tree under the root.
    /// </summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="configuration">Local root and link-handling configuration.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    public LocalStorageBackend(
        string connectionId,
        LocalConnectionConfig configuration,
        long maxBufferedDownloadBytes = DefaultMaxBufferedDownloadBytes)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxBufferedDownloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));

        ConnectionId = connectionId;
        _paths = new LocalPathResolver(configuration.RootPath, configuration.FollowLinks);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _capabilities = DetectCaseInsensitive(_paths.Root)
            ? new StorageCapabilities(LocalCapabilities.Features | StorageFeature.CaseInsensitivePaths, LocalCapabilities.Limits)
            : LocalCapabilities;
    }

    /// <inheritdoc />
    public string ConnectionId { get; }

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.Local;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    /// <remarks>
    /// <see cref="StorageFeature.CaseInsensitivePaths"/> is decided by the root's file system, probed when the
    /// connection is created (see <see cref="DetectCaseInsensitive"/>).
    /// </remarks>
    public StorageCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Whether names under <paramref name="root"/> compare without regard to case. It depends on the file system and
    /// the mount, not the operating system (APFS and ext4 volumes can be either, a Windows folder can be made
    /// case-sensitive, a Linux mount of NTFS or SMB is not), so it is probed: an entry of the root, named with the
    /// case of its ASCII letters swapped (see <see cref="SwapCase"/>), either resolves or not. A root with no such
    /// entry, or one that cannot be read,
    /// gets the operating system's default (Windows and macOS insensitive, others sensitive). Subfolders are assumed
    /// to behave like the root.
    /// </summary>
    internal static bool DetectCaseInsensitive(string root)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).Take(64))
            {
                var name = Path.GetFileName(entry);
                var swapped = SwapCase(name);
                if (string.Equals(swapped, name, StringComparison.Ordinal))
                    continue;
                if (!Path.Exists(Path.Combine(root, swapped)))
                    return false;
                // On a case-sensitive file system the swapped name may be another entry: it resolves, but not to this one.
                var literal = Directory.EnumerateFileSystemEntries(root, swapped, new EnumerationOptions { MatchCasing = MatchCasing.CaseSensitive, RecurseSubdirectories = false });
                if (literal.Any(found => string.Equals(Path.GetFileName(found), swapped, StringComparison.Ordinal)))
                    continue;
                return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// The name with the case of its ASCII letters swapped. Other letters are left alone: file systems fold case by
    /// their own tables, which differ from .NET's invariant mapping outside ASCII (NTFS does not map Turkish dotless
    /// <c>ı</c> to <c>I</c>, nor Georgian as .NET does), so only ASCII tells how the file system compares names. A name
    /// without ASCII letters comes back unchanged and is skipped by the probe.
    /// </summary>
    internal static string SwapCase(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsAsciiLetterUpper(c) ? (char)(c + 32) : char.IsAsciiLetterLower(c) ? (char)(c - 32) : c;
            }
        });

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(resolved.Error!));
        try
        {
            var item = CreateItem(resolved.Value!.StoragePath, resolved.Value.FullPath);
            return Task.FromResult(item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"Storage item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(item));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.FromException(error, "Get item info")));
        }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<bool>.Failure(resolved.Error!));
        try
        {
            _ = File.GetAttributes(resolved.Value!.FullPath);
            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(Result<bool>.Success(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool>.Failure(StorageErrors.FromException(error, "Check item existence")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Result<StoragePage>.Failure(resolved.Error!);

        try
        {
            if (!Directory.Exists(resolved.Value!.FullPath))
                return Result<StoragePage>.Failure(StorageErrors.NotFound($"Directory '{resolved.Value.StoragePath}' was not found."));

            // Directory enumeration runs once per listing pass instead of once per page.
            var target = resolved.Value!;
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                token => Task.FromResult(Result<IEnumerable<StorageItem>>.Success(
                    EnumerateItems(target, options.Recursive, token).ToArray().AsEnumerable())),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Result<StoragePage>.Failure(StorageErrors.FromException(error, "List directory"));
        }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error!));
        try
        {
            Directory.CreateDirectory(resolved.Value!.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Create directory")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadAsync(
        string path,
        Stream source,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return await StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken).ConfigureAwait(false);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "The local provider does not support atomic ETag or version upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "Local storage does not persist provider user metadata."));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A file path is required for upload."));

        string? stagingPath = null;
        try
        {
            var parent = Path.GetDirectoryName(resolved.Value.FullPath)!;
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

            if (Directory.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination is a directory."));
            if (!options.Overwrite && File.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination already exists."));

            stagingPath = CreateStagingPath(parent, "upload");
            await using (var destination = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 81_920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, resolved.Value.FullPath, options.Overwrite);
            stagingPath = null;
            return await GetInfoAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && File.Exists(resolved.Value.FullPath))
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination already exists."));
        }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Upload file"));
        }
        finally
        {
            TryDeleteStagingFile(stagingPath);
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(
        string path,
        byte[] content,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
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
        await using var source = download.Value!;
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
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
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

    private static StorageItem? CreateItem(string storagePath, string fullPath, FileAttributes? knownAttributes = null)
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
            LinkTarget = type == StorageItemType.Link ? info.LinkTarget : null,
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

    /// <inheritdoc />
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
            var absolute = Path.IsPathRooted(raw) ? raw : Path.Combine(Path.GetDirectoryName(info.FullName)!, raw);
            return Task.FromResult(Result<StorageLinkInfo>.Success(new StorageLinkInfo(raw, _paths.ToStoragePath(absolute))));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.FromException(error, "Read link")));
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A file path is required."));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Value.FullPath)!);
            await using (var target = new FileStream(resolved.Value.FullPath, FileMode.Append, FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous))
                await source.CopyToAsync(target, 65_536, cancellationToken).ConfigureAwait(false);
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
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
